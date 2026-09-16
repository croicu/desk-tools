#!/usr/bin/env python3
"""Minimal, hand-rolled MCP server over stdio exposing a single unlink_program_files tool.

The counterpart to link-program-files.py: removes the ".dev" junction inside
"C:\\Program Files\\Desk Tools" (see that script's own remarks for the full mechanism --
src/Base/DevRedirect.cs is what actually consumes it). Never touches "Desk Tools" itself, so
there's no "restore the real install" step needed here at all, unlike this tool's earlier,
abandoned rename-the-whole-folder design. See link-program-files.py's own remarks for why this
runs as a real MCP tool (via `desk mcp unlink-program-files`) instead of a UAC-prompting VS Code
task -- it inherits Service's own elevated process instead. Mirrors goodbye.py's own shape (no
SDK, newline-delimited JSON-RPC 2.0 over stdio, protocol version "2025-06-18" only, same error
codes); entirely self-contained.
"""

from __future__ import annotations

import json
import os
import stat
import sys
from typing import Any

SUPPORTED_PROTOCOL_VERSION = "2025-06-18"
UNLINK_PROGRAM_FILES_TOOL_NAME = "unlink_program_files"

PARSE_ERROR_CODE = -32700
INVALID_REQUEST_CODE = -32600
METHOD_NOT_FOUND_CODE = -32601
INVALID_PARAMS_CODE = -32602
INTERNAL_ERROR_CODE = -32603

UNLINK_PROGRAM_FILES_INPUT_SCHEMA = {"type": "object", "properties": {}, "additionalProperties": False}

LIVE_DIR = r"C:\Program Files\Desk Tools"
DEV_DIR = os.path.join(LIVE_DIR, ".dev")


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


def unlink_program_files() -> str:
    if not is_reparse_point(DEV_DIR):
        raise RuntimeError(f"'{DEV_DIR}' is not currently a junction -- nothing to unlink.")

    # If Service is currently running as the dev-redirected copy (see src/Base/DevRedirect.cs),
    # this very tool was launched from inside DEV_DIR itself (src/Service/ToolLauncher.cs sets
    # every launched tool's own cwd to the registry's own directory, which for a dev-redirected
    # Service is DEV_DIR) -- Windows implicitly locks a process's own cwd, so removing DEV_DIR out
    # from under ourselves would fail with WinError 32 ("used by another process") unless we step
    # out of it first. See this repo's own git history for link-program-files.py's earlier,
    # independently-confirmed run-in with exactly this class of bug.
    os.chdir(LIVE_DIR)

    # rmdir on a junction/reparse-point directory removes just the reparse point itself, not the
    # real build output it points at -- never shutil.rmtree here, that would recurse into the
    # target and delete the actual out/ folder.
    os.rmdir(DEV_DIR)
    return f"Unlinked '{DEV_DIR}'. Service.exe/Desk.exe under '{LIVE_DIR}' will run normally again on their next start."


def handle_initialize(id_: Any) -> None:
    write_result(
        id_,
        {
            "protocolVersion": SUPPORTED_PROTOCOL_VERSION,
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": "unlink-program-files", "version": "0.1.0"},
        },
    )


def handle_tools_list(id_: Any) -> None:
    write_result(
        id_,
        {
            "tools": [
                {
                    "name": UNLINK_PROGRAM_FILES_TOOL_NAME,
                    "description": "Removes the .dev junction inside C:\\Program Files\\Desk Tools.",
                    "inputSchema": UNLINK_PROGRAM_FILES_INPUT_SCHEMA,
                }
            ]
        },
    )


def handle_tools_call(id_: Any, params: Any) -> None:
    if not isinstance(params, dict) or not isinstance(params.get("name"), str):
        write_error(id_, INVALID_PARAMS_CODE, "Invalid params: 'name' is required.")
        return

    name = params["name"]
    if name != UNLINK_PROGRAM_FILES_TOOL_NAME:
        write_error(id_, INVALID_PARAMS_CODE, f"Unknown tool: {name}")
        return

    try:
        message = unlink_program_files()
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
