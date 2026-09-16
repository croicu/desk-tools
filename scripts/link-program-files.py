#!/usr/bin/env python3
"""Minimal, hand-rolled MCP server over stdio exposing a single link_program_files tool.

Backs up the real, MSI-installed "C:\\Program Files\\Desk Tools" to "Desk Tools.bak" and replaces
it with a directory junction into the parent of this script's own deployed folder (out/
<Configuration>/net10.0/[<RID>/], one level up from the scripts/ subfolder
scripts/Scripts.csproj actually copies this into -- see its own remarks) -- so a plain
`dotnet build` is reflected under Program Files immediately, without a reinstall. Deliberately
runs as a real MCP
tool launched via `desk mcp link-program-files` (see scripts/goodbye.py's own remarks on the
registry mechanism) rather than a VS Code task, specifically so it inherits Service's own elevated
process instead of popping a UAC prompt -- the whole reason this project's Service runs elevated in
the first place. See unlink-program-files.py for the reverse operation. Mirrors goodbye.py's own
shape (no SDK, newline-delimited JSON-RPC 2.0 over stdio, protocol version "2025-06-18" only, same
error codes); entirely self-contained, same reasoning as goodbye.py's own remarks on why it doesn't
share code with service_mcp_server.py/shutdown_service.py.
"""

from __future__ import annotations

import json
import os
import stat
import subprocess
import sys
from typing import Any

SUPPORTED_PROTOCOL_VERSION = "2025-06-18"
LINK_PROGRAM_FILES_TOOL_NAME = "link_program_files"

PARSE_ERROR_CODE = -32700
INVALID_REQUEST_CODE = -32600
METHOD_NOT_FOUND_CODE = -32601
INVALID_PARAMS_CODE = -32602
INTERNAL_ERROR_CODE = -32603

LINK_PROGRAM_FILES_INPUT_SCHEMA = {"type": "object", "properties": {}, "additionalProperties": False}

LIVE_DIR = r"C:\Program Files\Desk Tools"
BACKUP_DIR = r"C:\Program Files\Desk Tools.bak"


def write_result(id_: Any, result: Any) -> None:
    _write({"jsonrpc": "2.0", "id": id_, "result": result})


def write_error(id_: Any, code: int, message: str) -> None:
    _write({"jsonrpc": "2.0", "id": id_, "error": {"code": code, "message": message}})


def _write(envelope: dict[str, Any]) -> None:
    # stdout carries only valid MCP protocol lines -- same rule src/Hello/Program.cs follows, since
    # the stdio transport requires it.
    sys.stdout.write(json.dumps(envelope) + "\n")
    sys.stdout.flush()


def is_reparse_point(path: str) -> bool:
    # Covers both a directory symlink and a junction (they're different reparse tags) -- we only
    # need "is this path already redirected somewhere", not which kind.
    try:
        attrs = os.stat(path, follow_symlinks=False).st_file_attributes
    except (FileNotFoundError, AttributeError):
        return False
    return bool(attrs & stat.FILE_ATTRIBUTE_REPARSE_POINT)


def link_program_files() -> str:
    # This script itself lives in <OutDir>/scripts/ (see scripts/Scripts.csproj's own remarks) --
    # the junction target is <OutDir> itself, alongside Service.exe, one level up from here.
    target = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    if is_reparse_point(LIVE_DIR):
        return f"'{LIVE_DIR}' is already a junction -- nothing to do."

    if not os.path.isdir(LIVE_DIR):
        raise RuntimeError(f"'{LIVE_DIR}' does not exist -- install the MSI first.")

    if os.path.exists(BACKUP_DIR):
        raise RuntimeError(
            f"'{BACKUP_DIR}' already exists -- resolve it manually (e.g. run "
            "unlink_program_files) before linking again."
        )

    os.rename(LIVE_DIR, BACKUP_DIR)
    try:
        subprocess.run(
            ["cmd", "/c", "mklink", "/J", LIVE_DIR, target],
            check=True,
            capture_output=True,
            text=True,
        )
    except subprocess.CalledProcessError as error:
        # Don't leave Program Files without a Desk Tools folder at all if mklink itself failed.
        os.rename(BACKUP_DIR, LIVE_DIR)
        detail = (error.stderr or error.stdout or "").strip()
        raise RuntimeError(f"mklink failed, rolled back: {detail}") from error

    return f"Linked '{LIVE_DIR}' -> '{target}' (original install backed up to '{BACKUP_DIR}')."


def handle_initialize(id_: Any) -> None:
    write_result(
        id_,
        {
            "protocolVersion": SUPPORTED_PROTOCOL_VERSION,
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": "link-program-files", "version": "0.1.0"},
        },
    )


def handle_tools_list(id_: Any) -> None:
    write_result(
        id_,
        {
            "tools": [
                {
                    "name": LINK_PROGRAM_FILES_TOOL_NAME,
                    "description": (
                        "Backs up the real C:\\Program Files\\Desk Tools to Desk Tools.bak and "
                        "replaces it with a junction into this build's own output folder."
                    ),
                    "inputSchema": LINK_PROGRAM_FILES_INPUT_SCHEMA,
                }
            ]
        },
    )


def handle_tools_call(id_: Any, params: Any) -> None:
    if not isinstance(params, dict) or not isinstance(params.get("name"), str):
        write_error(id_, INVALID_PARAMS_CODE, "Invalid params: 'name' is required.")
        return

    name = params["name"]
    if name != LINK_PROGRAM_FILES_TOOL_NAME:
        write_error(id_, INVALID_PARAMS_CODE, f"Unknown tool: {name}")
        return

    try:
        message = link_program_files()
        write_result(id_, {"content": [{"type": "text", "text": message}], "isError": False})
    except RuntimeError as error:
        write_result(id_, {"content": [{"type": "text", "text": str(error)}], "isError": True})


def handle_line(line: str) -> None:
    try:
        request = json.loads(line)
    except json.JSONDecodeError:
        write_error(None, PARSE_ERROR_CODE, "Parse error")
        return

    id_present = "id" in request
    id_ = request.get("id")
    method = request.get("method")

    if not isinstance(method, str):
        if id_present:
            write_error(id_, INVALID_REQUEST_CODE, "Invalid request: 'method' is required.")
        return

    if not id_present:
        # A notification (e.g. notifications/initialized) -- consumed, no response ever sent.
        return

    params = request.get("params")

    try:
        if method == "initialize":
            handle_initialize(id_)
        elif method == "tools/list":
            handle_tools_list(id_)
        elif method == "tools/call":
            handle_tools_call(id_, params)
        else:
            write_error(id_, METHOD_NOT_FOUND_CODE, f"Method not found: {method}")
    except Exception as error:  # stdin is a system boundary -- same reasoning as src/Hello's own catch-all
        write_error(id_, INTERNAL_ERROR_CODE, f"Internal error: {error}")


def main() -> int:
    for raw_line in sys.stdin:
        line = raw_line.strip()
        if line:
            handle_line(line)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
