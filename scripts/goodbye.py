#!/usr/bin/env python3
"""Minimal, hand-rolled MCP server over stdio exposing a single say_goodbye tool.

Mirrors src/Hello/Program.cs's own shape (no SDK, newline-delimited JSON-RPC 2.0 over stdio,
protocol version "2025-06-18" only, same error codes) but in plain-stdlib Python -- the first
tool registered via scripts/Scripts.csproj's build-time MCP registry generation (see issue #29's
extensibility goal and that project's own remarks) rather than a per-.csproj <McpToolName>. Unlike
scripts/service_mcp_server.py, this has no Service-control logic of its own and so no need to
import find_repo_root/resolve_port from shutdown_service.py -- entirely self-contained.
"""

from __future__ import annotations

import json
import sys
from typing import Any

SUPPORTED_PROTOCOL_VERSION = "2025-06-18"
SAY_GOODBYE_TOOL_NAME = "say_goodbye"

PARSE_ERROR_CODE = -32700
INVALID_REQUEST_CODE = -32600
METHOD_NOT_FOUND_CODE = -32601
INVALID_PARAMS_CODE = -32602
INTERNAL_ERROR_CODE = -32603

SAY_GOODBYE_INPUT_SCHEMA = {"type": "object", "properties": {}, "additionalProperties": False}


def write_result(id_: Any, result: Any) -> None:
    _write({"jsonrpc": "2.0", "id": id_, "result": result})


def write_error(id_: Any, code: int, message: str) -> None:
    _write({"jsonrpc": "2.0", "id": id_, "error": {"code": code, "message": message}})


def _write(envelope: dict[str, Any]) -> None:
    # stdout carries only valid MCP protocol lines -- same rule src/Hello/Program.cs follows, since
    # the stdio transport requires it.
    sys.stdout.write(json.dumps(envelope) + "\n")
    sys.stdout.flush()


def handle_initialize(id_: Any) -> None:
    write_result(
        id_,
        {
            "protocolVersion": SUPPORTED_PROTOCOL_VERSION,
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": "goodbye", "version": "0.1.0"},
        },
    )


def handle_tools_list(id_: Any) -> None:
    write_result(
        id_,
        {
            "tools": [
                {
                    "name": SAY_GOODBYE_TOOL_NAME,
                    "description": "Prints a friendly goodbye.",
                    "inputSchema": SAY_GOODBYE_INPUT_SCHEMA,
                }
            ]
        },
    )


def handle_tools_call(id_: Any, params: Any) -> None:
    if not isinstance(params, dict) or not isinstance(params.get("name"), str):
        write_error(id_, INVALID_PARAMS_CODE, "Invalid params: 'name' is required.")
        return

    name = params["name"]
    if name != SAY_GOODBYE_TOOL_NAME:
        write_error(id_, INVALID_PARAMS_CODE, f"Unknown tool: {name}")
        return

    write_result(id_, {"content": [{"type": "text", "text": "Bye from MCP"}], "isError": False})


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
