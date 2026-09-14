---
description: Gracefully stop the locally-running hello MCP server (releases Hello.dll's file lock) instead of killing its process
allowed-tools: mcp__hello__stop
---

The `hello` MCP server (spawned by Claude Code per `.mcp.json`) holds `Hello.dll` open for the
lifetime of its process. That blocks `dotnet build`/`dotnet test` in this repo with a file-lock
error (`MSB3026`/`MSB3027`, "The process cannot access the file ... Hello.dll ... The file is
locked by: \".NET Host (<pid>)\"") until the process exits.

When that happens, call the `stop` tool (`mcp__hello__stop`) instead of asking the user for
permission to kill the process. It responds first, then exits its own read loop and returns from
`Run()` normally -- the same graceful shutdown path a real client disconnect takes (see
`src/Hello/Program.cs`'s `HandleToolsCall`) -- so the file handle is released cleanly, no OS-level
process kill needed. The build will succeed on the next attempt once the process has actually
exited (its `dotnet` host process disappears within a moment of the tool call returning).
