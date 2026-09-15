#!/usr/bin/env python3
"""Shuts down the Desk Tools Service resident process gracefully.

Connects to Host's loopback TCP listener (see docs/PROTOCOL.md's Echo protocol section) and sends
the reserved "shutdown" line -- Host echoes it back (an acknowledgment) then shuts itself down via
the same path its own idle timeout uses. Mirrors src/Desk's own `desk shutdown` subcommand, just as
a standalone script with no .NET toolchain involved. The "shutdown" sentinel must match
Host.ShutdownCommand (src/Service/Host.cs) -- kept in sync by hand, same as Desk's own copy of that
constant, since there's no shared-across-languages way to reference it.
"""

from __future__ import annotations

import argparse
import json
import socket
import sys
from pathlib import Path

SHUTDOWN_COMMAND = "shutdown"
DEFAULT_PORT = 51823


def find_repo_root(start: Path) -> Path:
    """Walks up from *start* looking for Service.slnx (a repo-root marker) -- mirrors
    tests/Desk/Integration/ServiceTests.cs's own FindRepoRoot: robust to exactly where this script
    is invoked from, no hardcoded parent-hop count."""
    current = start.resolve()
    for candidate in (current, *current.parents):
        if (candidate / "Service.slnx").exists():
            return candidate

    raise SystemExit(f"error: could not locate the repo root (Service.slnx) above '{start}'")


def resolve_port(repo_root: Path) -> int:
    """Reads settings.json then settings.local.json (local overriding) for a "port" key -- same
    precedence direction Settings.cs uses, see docs/PROTOCOL.md's settings.json discovery order.
    Falls back to DEFAULT_PORT if neither file sets one. Doesn't replicate Settings.cs's full
    module-tier/working-directory-tier split -- both files always live at the repo root, so that
    distinction doesn't apply here."""
    port = DEFAULT_PORT
    for filename in ("settings.json", "settings.local.json"):
        path = repo_root / filename
        if not path.exists():
            continue

        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            continue

        value = data.get("settings", {}).get("port")
        if isinstance(value, int):
            port = value

    return port


def request_shutdown(port: int, timeout: float = 5.0) -> str:
    """Connects, sends the shutdown line, and returns the line Host echoed back -- raises OSError
    on any connection failure, same fail-fast, no-retry behavior as Desk's own Client.SendEcho."""
    with socket.create_connection(("127.0.0.1", port), timeout=timeout) as sock:
        sock.sendall(f"{SHUTDOWN_COMMAND}\n".encode("utf-8"))

        reply = b""
        while True:
            chunk = sock.recv(4096)
            if not chunk:
                break
            reply += chunk

        return reply.decode("utf-8").strip()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--port", type=int, default=None, help="override the resolved port (skips settings.json entirely)")
    args = parser.parse_args()

    repo_root = find_repo_root(Path(__file__).parent)
    port = args.port if args.port is not None else resolve_port(repo_root)

    print(f"shutdown: connecting to 127.0.0.1:{port}.")
    try:
        reply = request_shutdown(port)
    except OSError as error:
        print(f"shutdown: error: could not connect to the service on port {port}: {error}", file=sys.stderr)
        return 1

    if reply != SHUTDOWN_COMMAND:
        print(f"shutdown: error: unexpected reply from service: {reply!r}", file=sys.stderr)
        return 1

    print("Shutdown requested.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
