---
description: Point C:\Program Files\Desk Tools at this repo's own build output (backing up the real install first), so a plain dotnet build is reflected there immediately
allowed-tools: mcp__link-program-files__link_program_files
---

After a real MSI install (`install-msi (Debug)`), `C:\Program Files\Desk Tools` holds a static
copy of whatever was built at install time -- a plain `dotnet build` afterward doesn't change what
PATH's own `desk`/`Desk.exe` or `.mcp.json` actually run until you reinstall.

When the user wants that folder to track the repo's own build output instead (so edits show up on
the next `dotnet build` with no reinstall), call the `link_program_files` tool
(`mcp__link-program-files__link_program_files`) rather than doing the rename/junction by hand.
It runs inside Service's own already-elevated process, so it needs no UAC prompt -- doing the
same rename (`Desk Tools` -> `Desk Tools.bak`) and `mklink /J` from an unelevated shell would just
fail on Program Files' ACLs. It's idempotent (a no-op if already linked) and refuses to run if a
backup already exists, so it's safe to call speculatively rather than checking state first. See
`unlink-program-files`'s own skill to put the real install back.
