# PROTOCOL.md

CLI signature and file format schemas for `desk-tools`.

## CLI

<!-- Command name, arguments, flags, exit codes. -->

## File formats

<!-- Schemas for any files this project reads or writes. -->

### settings.json discovery order

`Settings.Load()`/`Settings.Section()` (`src/Base/Settings.cs`) read three tiers, each overriding
the previous key-by-key:

1. `settings.json` next to the running module (`AppContext.BaseDirectory` -- e.g. the install
   directory of a deployed build). The base tier.
2. `settings.json` in the current working directory. A per-deployment/per-invocation override of
   the module tier.
3. `settings.local.json` in the current working directory -- a personal, gitignored override (see
   `.gitignore`) of the previous two.

A malformed module-tier or working-directory `settings.json` throws (`SettingsError`); a malformed
`settings.local.json` is logged and ignored, since it's the optional/personal tier. If neither the
module nor working-directory tier has a file, `Load()` falls back to restrictive defaults
(`debug=false`, `logLevel=error`) and logs a warning.

### settings.json / settings.local.json -- `"settings"` object

- `idleTimeout` (number, seconds, default `600`) -- how long the resident process (`Host`, see
  `docs/ARCHITECTURE.md`) may go without accepting a connection before it exits. Interim activity
  signal only (accept == activity, for now) -- see
  [issue #5](https://github.com/croicu/desk-tools/issues/5).
