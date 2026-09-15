# PROTOCOL.md

CLI signature and file format schemas for `desk-tools`.

## CLI

<!-- Command name, arguments, flags, exit codes. -->

`src/Service` (`desk-tools`), `src/Hello` (`hello`), and `src/Desk` (`desk`) all accept:

- `--log <dir>` -- write a timestamped log file into `<dir>` (creating it if missing), in addition
  to whatever console/debug sinks are already active; overrides `settings.json`'s `logDir` if both
  are given. See `docs/ARCHITECTURE.md`'s `FileLog`/`Correlation` entries for the sink itself and
  the CLAUDE.md Logging section for the file's line format.

`src/Service` additionally accepts `--debug` (override `settings.json`'s `debug` flag) and `-h`/
`--help` (print usage and exit 0); an unrecognized argument exits 2. `src/Hello` accepts only
`--log` today -- it's an MCP client-launched stdio server, not something invoked interactively with
`--help` in mind, and has no CLI-driven debug override (`settings.json`'s `debug` alone still drives
it). `src/Desk` additionally requires exactly one bare subcommand, `ping` or `shutdown` (a verb --
what Desk should do -- rather than a `--`-prefixed flag, since the two are mutually exclusive, not
independent options); missing it, giving both, or any other unrecognized argument exits 2, same as
`src/Service`. See the Echo protocol section below for what each subcommand sends.

## MCP (`src/Hello`)

Minimal, hand-rolled (no SDK) MCP server over stdio -- launched by an MCP client via `command`
(e.g. `dotnet Hello.dll`), speaks newline-delimited JSON-RPC 2.0 on stdin/stdout per the
[stdio transport spec](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports),
verified 2026-09-13. Supports protocol version `2025-06-18` only.

Implements the standard `initialize` -> `initialized` notification -> operation lifecycle
([spec](https://modelcontextprotocol.io/specification/2025-06-18/basic/lifecycle)) and:

- `tools/list` -- returns two tools, both with
  `inputSchema: {"type":"object","properties":{},"additionalProperties":false}` (no arguments):
  - `say_hello`
  - `stop` -- a graceful, self-terminating shutdown; always advertised (no `settings.debug` gate).
- `tools/call` -- `say_hello` returns `{"content":[{"type":"text","text":"Hi from MCP"}],"isError":false}`.
  `stop` returns `{"content":[{"type":"text","text":"Stopping."}],"isError":false}` and then exits
  its read loop the same way EOF-on-stdin would (see `docs/ARCHITECTURE.md`'s `Program.cs` entry) --
  no further requests on the same connection are handled after it. An unknown tool name is a
  JSON-RPC `-32602` (Invalid params) error, not a tool-level `isError: true` result.

Unknown methods get `-32601` (Method not found); malformed JSON gets `-32700` (Parse error) with
`id: null`. Not yet implemented: `resources`, `prompts`, `listChanged` notifications, pagination --
this is intentionally minimal, a stepping stone toward the real dispatch work referenced in
`docs/ARCHITECTURE.md`'s `Host` entry.

## Echo protocol (`src/Service`'s `Host` / `src/Desk`)

Plain newline-delimited text, not JSON-RPC -- a stepping stone ahead of the real request/response
framing referenced in `docs/ARCHITECTURE.md`'s `Host` entry ([issue #5](https://github.com/croicu/desk-tools/issues/5)).
A client connects to `Host`'s loopback listener (port from `settings.json`'s `port`, see below),
writes exactly one line, and reads exactly one line back: `Host` echoes whatever it read verbatim,
then closes the connection. A client that sends nothing before closing its own end gets no reply,
just a closed connection. `src/Desk` is this protocol's one client today, with two subcommands (see
the CLI section above):

- `desk ping` -- sends the fixed line `ping`, prints whatever comes back, and exits -- no retry and
  no auto-starting `Host` if the connection fails, just a fast, clear error.
- `desk shutdown` -- sends the reserved line `shutdown` (`Host.ShutdownCommand`), asking `Host` to
  shut down gracefully once it has replied ([issue #22](https://github.com/croicu/desk-tools/issues/22)).
  Still echoed back first like any other line, so Desk gets a definitive acknowledgment before the
  listener actually stops; Desk itself just prints a fixed confirmation rather than the raw echoed
  text. Shares `Host`'s existing idle-timeout shutdown path rather than a separate mechanism, so the
  teardown itself (stop listening, join the accept thread) is identical either way. Known
  limitation of this still-plain-text protocol: an ordinary `ping` whose payload happened to equal
  the literal string `shutdown` would also trigger this -- acceptable today since Desk is the only
  client and never sends arbitrary text, worth revisiting once real request framing lands.

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

The checked-in module-tier `settings.json` (and an optional, gitignored `settings.local.json`)
live at the **repo root**, not inside `src/Service/` or `src/Hello/` -- `src/Service` and
`src/Hello` build into one shared output folder (`src/Directory.Build.props`' `BaseOutputPath`), so
a single root-level pair avoids either app's own file silently clobbering the other's at that
shared path. `src/Directory.Build.targets` copies both into each app's own `$(OutDir)` after
`Build` (imperative `Copy` tasks, not a declarative `CopyToOutputDirectory` item -- the latter
propagates through any `ProjectReference`, which would leak these files into every test project
that references `Hello`/`Service` too) and `settings.json` alone into `$(PublishDir)` after
`Publish` -- `settings.local.json` is deliberately never published, since
`installer/Setup.wixproj` globs its entire publish output into the MSI and a developer's personal
override must never ship there.

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
- `port` (number, default `51823`) -- loopback TCP port `src/Service`'s `Host` listens on and
  `src/Desk` connects to (see the Echo protocol section above). Both processes read this from the
  same shared settings.json, with no direct dependency between them beyond that.
