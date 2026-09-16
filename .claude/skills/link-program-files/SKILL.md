---
description: Make C:\Program Files\Desk Tools\Service.exe and Desk.exe hand off to this repo's own build output on their next start, so a plain dotnet build is reflected there immediately
allowed-tools: mcp__link-program-files__link_program_files
---

After a real MSI install (`install-msi (Debug)`), `C:\Program Files\Desk Tools` holds a static
copy of whatever was built at install time -- a plain `dotnet build` afterward doesn't change what
PATH's own `desk`/`Desk.exe` or `.mcp.json` actually run until you reinstall.

When the user wants that install to track the repo's own build output instead (so edits show up on
the next `dotnet build` with no reinstall), call the `link_program_files` tool
(`mcp__link-program-files__link_program_files`) with a `target` argument -- the absolute path to
the dev build's own output folder (e.g. `<repo>\out\Debug\net10.0\win-x64`) -- rather than doing it
by hand. **`target` must be given explicitly; never guess or omit it.** The tool cannot infer this
itself: it's the same script deployed both to the dev build and to the real install, and it always
*runs* from the installed copy (that's the only one Service can execute before any redirect
exists), so a self-inferred path would just resolve back to the install folder itself -- confirmed
the hard way, producing a `.dev` that pointed at itself and made `DevRedirect.cs`'s own handoff
logic recurse into real nested processes until Windows' path-length limit finally broke the chain.
The tool now validates the given `target` isn't `C:\Program Files\Desk Tools` itself or nested
inside it, and `DevRedirect.cs` has its own independent check too, but get the actual path right
regardless -- ask the user or check the repo's own `out/<Configuration>/net10.0/[<RID>/]` layout if
unsure, don't guess.

It creates a `.dev` junction inside `C:\Program Files\Desk Tools` pointing at `target` --
`src/Base/DevRedirect.cs` is what actually consumes it: the installed `Service.exe`/`Desk.exe`
check for `.dev` as the very first thing they do at startup and, if present, re-spawn themselves
from inside it instead of running normally. This runs inside Service's own already-elevated
process, so it needs no UAC prompt -- doing the same `mklink /J` from an unelevated shell would
just fail on Program Files' ACLs. It's idempotent (a no-op if `.dev` already exists as a junction)
and refuses to run if something non-junction already occupies that path, so it's safe to call
speculatively rather than checking state first. See `unlink-program-files`'s own skill to remove
the redirect. Note: an already-running Service instance doesn't pick this up retroactively -- it
only takes effect on the *next* start (e.g. `desk shutdown` then let it auto-start again, or
restart the scheduled task).
