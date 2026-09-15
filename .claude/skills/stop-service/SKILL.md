---
description: Gracefully stop the locally-running Service resident process (releases Base.dll's/Service.dll's file lock) instead of killing its process
allowed-tools: mcp__service__shutdown
---

Service (whether started directly, e.g. `dotnet Service.dll`, or via the installed scheduled task,
`"Desk Tools Service"`) holds `Base.dll`/`Service.dll` open for the lifetime of its process, same as
the `hello` MCP server does for `Hello.dll` (see the `stop-hello` skill). That blocks `dotnet
build`/`dotnet test` in this repo with a file-lock error (`MSB3026`/`MSB3027`, "The process cannot
access the file ... Service.dll/Base.dll ... The file is locked by: \".NET Host (<pid>)\"") until
the process exits.

When that happens, call the `shutdown` tool (`mcp__service__shutdown`, from the `service` MCP
server registered in `.mcp.json`, `scripts/service_mcp_server.py`) instead of asking the user for
permission to kill the process. It sends `Host`'s reserved `shutdown` line over its loopback TCP
listener, gets an acknowledgment, and `Host` shuts itself down via the same path its own idle
timeout uses (see `src/Service/Host.cs`'s `HandleConnection`/`WaitForIdleShutdown`) -- no OS-level
process kill needed. The build will succeed on the next attempt once the process has actually
exited (its `dotnet` host process disappears within a moment of the tool call returning).

If the tool call itself fails (`isError: true`, "Could not connect to the service on port ..."),
Service either isn't currently running or its listener isn't on the port this repo's settings.json
resolves to -- in that case a locked `Service.dll`/`Base.dll` has some other cause, so fall back to
asking the user rather than assuming the process is still up.
