---
description: Remove the C:\Program Files\Desk Tools dev junction and restore the real MSI-installed folder from its backup
allowed-tools: mcp__unlink-program-files__unlink_program_files
---

The counterpart to `link-program-files`'s own skill: once `C:\Program Files\Desk Tools` has been
pointed at this repo's own build output (see that skill), the real, MSI-installed copy sits
untouched at `C:\Program Files\Desk Tools.bak`.

When the user wants the real install back (e.g. before testing an actual MSI install/upgrade, or
just done with local dev), call the `unlink_program_files` tool
(`mcp__unlink-program-files__unlink_program_files`) rather than doing the rmdir/rename by hand.
Same reasoning as `link-program-files`: it runs inside Service's own already-elevated process, so
no UAC prompt is needed. It refuses to run unless `Desk Tools` is currently a junction with a real
backup present, so it's safe to call speculatively.
