#!/usr/bin/env python3
"""Minimal, hand-rolled MCP server over stdio exposing a single link_program_files tool.

Creates a ".dev" junction inside the real, MSI-installed "C:\\Program Files\\Desk Tools", pointing
at a dev build's own output folder (out/<Configuration>/net10.0/[<RID>/]) given explicitly as the
tool's own "target" argument -- NOT self-inferred from this script's own location. That was tried
first (walk up two directories from `__file__`, matching where scripts/Scripts.csproj deploys this
script within the dev build's own output) and is wrong: this script is *also* deployed to the
*installed* copy via the MSI (see installer/Setup.wixproj's own remarks), and it has to run from
there -- that's the only copy Service can execute before any ".dev" redirect exists in the first
place. Self-inferring from `__file__` in that context resolves to "C:\\Program Files\\Desk Tools"
itself, producing a junction that points at itself -- confirmed the hard way: Service/Desk.exe's
own DevRedirect handoff (see src/Base/DevRedirect.cs) then tries to hand off to ".dev\\Desk.exe",
which resolves back through the very same junction to itself, which does the same check again,
spawning real nested child processes each with one more literal ".dev" appended to their own
AppContext.BaseDirectory string (never canonicalized away, since the reparse point is
transparently re-traversed at each additional ".dev" segment) until Windows' path-length limit
finally breaks the chain -- around 45 real processes deep in the run that actually hit this.
`link_program_files` now validates the given target isn't LIVE_DIR itself or nested inside it, as
a second line of defense.

src/Base/DevRedirect.cs is what actually makes this junction do anything: the *installed*
Service.exe/Desk.exe check for ".dev" as the very first thing they do at startup and, if present,
re-spawn themselves from inside it instead of running normally, so a plain `dotnet build` is
reflected under Program Files immediately, without a reinstall.

This replaces an earlier version of this tool that renamed "Desk Tools" itself to "Desk
Tools.bak" and junctioned the *whole* folder -- abandoned because a running process's own cwd (set
by src/Service/ToolLauncher.cs to LIVE_DIR for every launched tool, this one included) blocks
Windows from renaming that same directory (WinError 32, reproduced and confirmed independent of
elevation), and even past that, the real elevated Service still hit an unexplained WinError 5
renaming its own live install folder. Creating a plain subfolder junction *inside* LIVE_DIR
sidesteps both problems entirely -- it's not the folder any running process's cwd or image is
tied to, just an ordinary write inside a folder that's already open, no different from writing any
other file there.

Deliberately runs as a real MCP tool launched via `desk mcp link-program-files` (see
scripts/goodbye.py's own remarks on the registry mechanism) rather than a VS Code task,
specifically so it inherits Service's own elevated process instead of popping a UAC prompt -- the
whole reason this project's Service runs elevated in the first place. See
unlink-program-files.py for the reverse operation. Mirrors goodbye.py's own shape (no SDK,
newline-delimited JSON-RPC 2.0 over stdio, protocol version "2025-06-18" only, same error codes);
entirely self-contained, same reasoning as goodbye.py's own remarks on why it doesn't share code
with service_mcp_server.py/shutdown_service.py.
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

LINK_PROGRAM_FILES_INPUT_SCHEMA = {
    "type": "object",
    "properties": {
        "target": {
            "type": "string",
            "description": (
                "Absolute path to the dev build's own output folder to link to "
                "(e.g. C:\\...\\desk-tools\\out\\Debug\\net10.0\\win-x64)."
            ),
        }
    },
    "required": ["target"],
    "additionalProperties": False,
}

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


def link_program_files(target: str) -> str:
    target = os.path.abspath(target)

    if is_reparse_point(DEV_DIR):
        return f"'{DEV_DIR}' is already a junction -- nothing to do."

    if not os.path.isdir(LIVE_DIR):
        raise RuntimeError(f"'{LIVE_DIR}' does not exist -- install the MSI first.")

    if os.path.exists(DEV_DIR):
        raise RuntimeError(f"'{DEV_DIR}' already exists but isn't a junction -- resolve it manually before linking.")

    if not os.path.isdir(target):
        raise RuntimeError(f"'{target}' does not exist -- build it first (e.g. `dotnet build`).")

    # Refuses a self-referential target outright rather than trusting the caller: a target equal
    # to (or nested inside) LIVE_DIR would make ".dev" resolve back through itself, and
    # DevRedirect.cs's own handoff would then recurse into itself indefinitely on Service/Desk's
    # next start -- confirmed the hard way (see this function's own remarks above).
    normalized_target = os.path.normcase(target)
    normalized_live_dir = os.path.normcase(LIVE_DIR)
    if normalized_target == normalized_live_dir or normalized_target.startswith(normalized_live_dir + os.sep):
        raise RuntimeError(f"'{target}' is '{LIVE_DIR}' itself (or nested inside it) -- refusing a self-referential link.")

    try:
        subprocess.run(
            ["cmd", "/c", "mklink", "/J", DEV_DIR, target],
            check=True,
            capture_output=True,
            text=True,
        )
    except subprocess.CalledProcessError as error:
        detail = (error.stderr or error.stdout or "").strip()
        raise RuntimeError(f"mklink failed: {detail}") from error

    return f"Linked '{DEV_DIR}' -> '{target}'. Service.exe/Desk.exe under '{LIVE_DIR}' will now hand off to the dev build on their next start."


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
                        "Creates a .dev junction inside C:\\Program Files\\Desk Tools pointing at "
                        "this build's own output folder, so Service.exe/Desk.exe hand off to it on "
                        "their next start."
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

    arguments = params.get("arguments")
    if not isinstance(arguments, dict) or not isinstance(arguments.get("target"), str):
        write_error(id_, INVALID_PARAMS_CODE, "Invalid params: 'arguments.target' is required.")
        return

    try:
        message = link_program_files(arguments["target"])
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
