# PROTOCOL.md

CLI signature and file format schemas for `desk-tools`.

## CLI

<!-- Command name, arguments, flags, exit codes. -->

Both `src/Service` (`desk-tools`) and `src/Hello` (`hello`) accept:

- `--log <dir>` -- write a timestamped log file into `<dir>` (creating it if missing), in addition
  to whatever console/debug sinks are already active; overrides `settings.json`'s `logDir` if both
  are given. See `docs/ARCHITECTURE.md`'s `FileLog`/`Correlation` entries for the sink itself and
  the CLAUDE.md Logging section for the file's line format.

`src/Service` additionally accepts `--debug` (override `settings.json`'s `debug` flag) and `-h`/
`--help` (print usage and exit 0); an unrecognized argument exits 2. `src/Hello` accepts only
`--log` today -- it's an MCP client-launched stdio server, not something invoked interactively with
`--help` in mind, and has no CLI-driven debug override (`settings.json`'s `debug` alone still drives
it).

## MCP (`src/Hello`)

Minimal, hand-rolled (no SDK) MCP server over stdio -- launched by an MCP client via `command`
(e.g. `dotnet Hello.dll`), speaks newline-delimited JSON-RPC 2.0 on stdin/stdout per the
[stdio transport spec](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports),
verified 2026-09-13. Supports protocol version `2025-06-18` only.

Implements the standard `initialize` -> `initialized` notification -> operation lifecycle
([spec](https://modelcontextprotocol.io/specification/2025-06-18/basic/lifecycle)) and:

- `tools/list` -- returns one tool:
  - `say_hello` -- `inputSchema: {"type":"object","properties":{},"additionalProperties":false}`
    (no arguments).
- `tools/call` -- `say_hello` returns `{"content":[{"type":"text","text":"Hi from MCP"}],"isError":false}`.
  An unknown tool name is a JSON-RPC `-32602` (Invalid params) error, not a tool-level
  `isError: true` result.

Unknown methods get `-32601` (Method not found); malformed JSON gets `-32700` (Parse error) with
`id: null`. Not yet implemented: `resources`, `prompts`, `listChanged` notifications, pagination --
this is intentionally minimal, a stepping stone toward the real dispatch work referenced in
`docs/ARCHITECTURE.md`'s `Host` entry.

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
- `logDir` (string, directory, default unset -- no file logging) -- installs a `FileLog` sink
  writing a timestamped log file into this directory; overridable per-invocation by `--log <dir>`
  (see the CLI section above). See `docs/ARCHITECTURE.md`'s `FileLog` entry.
