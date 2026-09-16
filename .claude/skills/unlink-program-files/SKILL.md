---
description: Remove the .dev redirect inside C:\Program Files\Desk Tools so Service.exe/Desk.exe run normally again on their next start
allowed-tools: mcp__unlink-program-files__unlink_program_files
---

The counterpart to `link-program-files`'s own skill: once `C:\Program Files\Desk Tools` has a
`.dev` junction pointing at this repo's own build output (see that skill and
`src/Base/DevRedirect.cs`), the real, MSI-installed copy has been sitting there untouched the
whole time -- `link`/`unlink` never rename or replace `Desk Tools` itself, only add or remove that
one `.dev` subfolder.

When the user wants Service.exe/Desk.exe to stop redirecting to the dev build (e.g. before testing
an actual MSI install/upgrade, or just done with local dev), call the `unlink_program_files` tool
(`mcp__unlink-program-files__unlink_program_files`) rather than doing the rmdir by hand. Same
reasoning as `link-program-files`: it runs inside Service's own already-elevated process, so no
UAC prompt is needed. It refuses to run unless `.dev` currently exists as a junction, so it's safe
to call speculatively. Note: an already-running dev-redirected Service instance doesn't pick this
up retroactively -- it only takes effect on the *next* start (e.g. `desk shutdown` then let it
auto-start again).
