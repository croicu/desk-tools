---
description: Gracefully stop the locally-running Service resident process (releases Base.dll's/Service.dll's file lock) instead of killing its process
allowed-tools: Bash(desk shutdown)
---

Service holds `Base.dll`/`Service.dll` open for its whole lifetime, which blocks `dotnet
build`/`dotnet test` here with a file-lock error (`MSB3026`/`MSB3027`) until it exits.

When that happens, run `desk shutdown` instead of asking the user for permission to kill the
process. Service replies, then exits through the same path as its idle timeout; tools it launched
exit with it. It never auto-starts Service.

If it fails ("could not connect to the service on port ..."; with `debug: true` this surfaces as an
unhandled-exception trace), Service isn't running, so the lock has some other cause -- ask the user.
