# PROTOCOL.md

CLI signature and file format schemas for `desk-tools`.

## CLI

<!-- Command name, arguments, flags, exit codes. -->

`src/Service` (`desk-tools`), `src/Pager` (`pager`), and `src/Desk` (`desk`) all accept:

- `--log <dir>` -- write a timestamped log file into `<dir>` (creating it if missing), in addition
  to whatever console/debug sinks are already active; overrides `settings.json`'s `logDir` if both
  are given. See `docs/ARCHITECTURE.md`'s `FileLog`/`Correlation` entries for the sink itself and
  the CLAUDE.md Logging section for the file's line format.

`src/Service` additionally accepts `--debug` (override `settings.json`'s `debug` flag) and `-h`/
`--help` (print usage and exit 0); an unrecognized argument exits 2. `src/Pager` accepts only
`--log` today -- it's an MCP client-launched stdio server, not something invoked interactively with
`--help` in mind, and has no CLI-driven debug override (`settings.json`'s `debug` alone still drives
it). `src/Desk` additionally requires exactly one bare subcommand, `ping`, `shutdown`, or
`mcp <name>` (a verb -- what Desk should do -- rather than a `--`-prefixed flag, since they're
mutually exclusive, not independent options); missing one, giving more than one, `mcp` with no name
argument, or any other unrecognized argument exits 2, same as `src/Service`. See the Echo protocol
section below for what each subcommand sends.

`desk mcp <name>` is itself a stdio MCP proxy (see
[issue #33](https://github.com/croicu/desk-tools/issues/33)): usable as a real `.mcp.json`
`command` entry pointing at Desk instead of a tool's own executable directly. Once launched, its
own stdout carries only the launched tool's traffic (Desk installs a silent logging sink before
this starts, the same discipline `tests/MCP/Hello`'s own `Program.Start` documents for itself) -- see the
Echo protocol section below for the full exchange.

## MCP (`tests/MCP/Hello`)

A **test fixture**, no longer a shipped tool ([issue #86](https://github.com/croicu/desk-tools/issues/86)): the minimal
reference the pager mirrors, launched by the integration tests through a throwaway registry.

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

## MCP (`src/Pager`)

A standalone MCP server that sends push notifications through [ntfy](https://ntfy.sh)
([issue #76](https://github.com/croicu/desk-tools/issues/76)). Same hand-rolled stdio transport and protocol
version (`2025-06-18`) as `tests/MCP/Hello` above: `initialize`, `tools/list`, `tools/call`, the same `-32601` /
`-32700` / unknown-tool `-32602` errors; server name `pager`. Run it straight from `.mcp.json`
(`"command": "pager"`, with `NTFY_TOPIC` in its `env`) or through Service (`desk mcp pager`), which delivers the
topic from KeePass.

**Environment** (read once at start; a missing or unusable value never stops the server, only `page`):

- `NTFY_TOPIC` -- required: the topic to publish to (letters, digits, `_` and `-`, 1 to 64 characters). The one
  secret: anyone who knows it can read and post. Declared in the registry as `NTFY_TOPIC` <- KeePass entry
  `ntfy`, key `desk-pager`.
- `NTFY_SERVER` -- optional: an absolute `http`/`https` URL, default `https://ntfy.sh/`.

**`tools/list`** returns one tool, `page`, with `inputSchema`: `message` (string, required, at most 4096
characters), `title` (string, at most 250), `priority` (integer 1 to 5; 3 is the default), `tags` (array of at most
10 strings of at most 50 characters); no other properties.

**`tools/call` `page`** publishes one notification and returns a normal tool result:
`{"content":[{"type":"text","text":"..."}],"isError":false}` -- `"Notification sent."` on success.
Everything that goes wrong is `isError: true` with a sentence the model can read, never a JSON-RPC error:
`Not sent: ...` for arguments that fail validation; `The pager is not configured: ...` when `NTFY_TOPIC` or
`NTFY_SERVER` is missing or unusable (docs/ARCHITECTURE.md, invariant 3); `The ntfy server '<host>' refused the
notification: <status>.`; `Could not reach the ntfy server '<host>': ...`; `Timed out after 15 seconds ...`. No
message ever contains the topic.

**Wire format**: one `POST` to the server root (`https://ntfy.sh/`) with `Content-Type: application/json` and the
body `{"topic": ..., "message": ..., "title"?: ..., "priority"?: ..., "tags"?: [...]}` -- ntfy's JSON publishing
API -- rather than `POST /<topic>`, so the topic is never in a URL, a header or a log line, and non-ASCII text needs
no header encoding. Timeout 15 seconds.

## Echo protocol (`src/Service`'s `Host` / `src/Desk`)

JSON-RPC 2.0, newline-delimited, one request per TCP connection (see
[issue #31](https://github.com/croicu/desk-tools/issues/31) -- this section's name predates the
switch away from the original plain-text echo protocol it replaced; kept for continuity with
`docs/ARCHITECTURE.md`'s own references). A client connects to `Host`'s loopback listener (port
from `settings.json`'s `port`, see below), writes exactly one JSON-RPC request line, and reads
exactly one JSON-RPC response line back, then the connection closes. A request with no `id` is a
notification per the JSON-RPC spec: consumed, no response ever sent, regardless of method. A client
that sends nothing before closing its own end likewise gets no reply, just a closed connection.
Same error codes/envelope shapes as `tests/MCP/Hello`'s own hand-rolled dispatch (see the MCP section
above): malformed JSON is `-32700` (Parse error, `id: null`); a request missing `method` is
`-32600` (Invalid Request); an unrecognized method is `-32601` (Method not found); missing/malformed
`params` on a method that needs them is `-32602` (Invalid params); an unexpected exception while
handling a request is `-32603` (Internal error).

Three methods today:

- `ping` -- no params, result `"pong"`. A liveness/reachability check.
- `shutdown` -- no params, result `"ok"`. Asks `Host` to shut down gracefully once it has replied
  ([issue #22](https://github.com/croicu/desk-tools/issues/22)) -- still replies first, so the
  client gets a definitive acknowledgment before the listener actually stops. Shares `Host`'s
  existing idle-timeout shutdown path rather than a separate mechanism, so the teardown itself
  (stop listening, join the accept thread) is identical either way. Exits immediately even if
  launched MCP tools are still running -- those exit on their own once they notice Service is gone
  (`DESK_PARENT_PID` watchdog, see `docs/ARCHITECTURE.md`).
- `mcp` -- params `{"name": "<mcp-registry name>", "processId": <caller's own PID>}`. Launches the
  named tool (see the MCP tool registry entry below, and `docs/ARCHITECTURE.md`'s `McpToolLauncher`
  entry) and hands the caller direct access to its stdin/stdout by duplicating the underlying pipe
  handles directly into the caller's own process via Win32 `DuplicateHandle`
  ([issue #32](https://github.com/croicu/desk-tools/issues/32)) -- `Host` steps out of the exchange
  entirely once it replies, rather than relaying the tool's traffic itself for its whole lifetime.
  Result `{"stdin": "<decimal handle value>", "stdout": "<decimal handle value>"}` -- text, not a
  JSON number, since a Win32 `HANDLE` is a pointer (8 bytes on x64) and a JSON number can't reliably
  round-trip that precision. Windows-only (the mechanism itself is); `-32603` (Internal error) on
  any other platform, on a missing/malformed registry entry, or on the duplication itself failing.
  Trusts the caller-supplied `processId` as-is -- no verification against the real TCP connection's
  owning process (a known, deliberate simplification; the loopback listener is local-machine-only
  exposure either way).
  Optional param `"registryDir"` (string, fully qualified local path, no UNC;
  [issue #69](https://github.com/croicu/desk-tools/issues/69)): a caller-chosen registry searched
  *before* the shipped one -- `<registryDir>/mcp-registry/<name>.json` is used if it exists (its `args`
  and working directory resolve against `registryDir`), otherwise the shipped registry. **Refused with
  `-32602` unless `settings.allowCallerRegistry` is `true`** (default `false`): Service is elevated and
  any local process can reach it, so honoring a caller's registry by default would let that process run
  arbitrary programs, and request secrets for them, at Service's privilege. For development and tests.

`src/Desk` is this protocol's main client, with three subcommands (see the CLI section above):

- `desk ping` -- sends a `ping` request, prints the result (`pong`), and exits. If `Host` isn't
  reachable, auto-starts it via the installed scheduled task (`schtasks /run /tn "Desk Tools
  Service"`, see `docs/ARCHITECTURE.md`'s `Starter` entry and
  [issue #27](https://github.com/croicu/desk-tools/issues/27)) -- deliberately *not* a plain child
  process, since `Host` is meant to run at the scheduled task's own elevated integrity level, not
  Desk's own (typically lower) one -- and waits until it answers. Only works once Service has actually been
  installed via the MSI (the scheduled task must already be registered); fails with a clear error
  otherwise, or if it never becomes reachable within the startup wait (60 seconds: a cold first
  start of a freshly built Service has been seen taking about 30).
- `desk shutdown` -- stays fail-fast, no retry, no auto-start (shutting down something that isn't
  running isn't an error worth auto-starting for). Sends a `shutdown` request and, on success,
  prints a fixed confirmation rather than the raw JSON-RPC result. **If Service is not running** (the
  connection is refused) there is nothing to shut down, which is the state asked for, so it prints "The
  service is not running; nothing to shut down." and **exits 0** -- not an error, and no stack trace even
  with `debug` on ([issue #80](https://github.com/croicu/desk-tools/issues/80)). Any other failure (the
  connection breaking mid-request, say) is still an error with exit code 1.
- `desk mcp <name>` -- sends an `mcp` request (same auto-start-on-failure retry as `ping`), opens
  the two duplicated handles the result returns, then bridges this process's own stdin/stdout to
  them (`src/Desk/McpProxy.cs`, see `docs/ARCHITECTURE.md`'s own entry) until its own stdin reaches
  EOF -- at which point it closes its copy of the tool's stdin (so the tool sees EOF too and exits
  its own read loop gracefully) and exits itself. Makes `desk mcp <name>` usable as a real
  `.mcp.json` `command` entry pointing at Desk instead of the tool's own executable directly --
  Desk never parses the relayed traffic, purely a line relay. `--registry <dir>` (mcp only) sends it
  as the request's `registryDir` (made absolute first) -- see the `mcp` method above for the setting
  that must allow it.

## File formats

<!-- Schemas for any files this project reads or writes. -->

### settings.json discovery order

`Settings.Load()`/`Settings.Section()` (`src/Base/Settings.cs`) read these tiers, each overriding
the previous key-by-key (`Settings.Load()`, the core `"settings"` object, reads tiers 1 and 3; the
per-user tier 2 is read by `Settings.Section()` only -- see below). **Settings are read when a process
starts**, so a change to any of these files takes effect only after the process is restarted -- for Service,
`desk shutdown` and let the next `desk` call (or the scheduled task) start it again:

1. `settings.json` next to the running module (`AppContext.BaseDirectory` -- e.g. the install
   directory of a deployed build). The base tier.
2. The per-user file, `%LOCALAPPDATA%\Desk Tools\settings.json` (the platform's local
   application-data folder elsewhere; [issue #58](https://github.com/croicu/desk-tools/issues/58)).
   Written by the program itself -- `UserSettings.Set(section, key, value)` -- for values only the
   user's own machine can know, e.g. the KeePass database path the secrets file picker records
   (`secrets` section, `databasePath` key; the picker runs only when it is missing or empty).
3. `settings.local.json` next to the running module -- a personal, gitignored override (see
   `.gitignore`) of everything above, including the per-user file.

**Nothing is read relative to the current working directory**
([issue #70](https://github.com/croicu/desk-tools/issues/70)). Service is elevated, so a cwd-relative
settings file would let anyone who can write the directory Service happens to run in change its core
settings (`port`, `logDir`, `allowCallerRegistry`, ...). Every default location is next to the module,
which only an administrator can write in an installed build. (`Settings.Load`/`Section` take an optional
explicit `path` for tests and embedding hosts that choose the file themselves; nothing passes one by default.)

**The per-user file is honored only for non-core sections.** Any process running as the user can
write it, while Service reads it elevated, so it may supply sections that sit beside the `"settings"`
object (read with `Settings.Section(name)`), never the core `"settings"` object itself (`logDir`,
`port`, `debug`, `idleTimeout`, `logLevel`, ...): `Settings.Load()` does not read it, `Section("settings")`
skips it, and `UserSettings.Set` refuses that section. A per-user file that is not valid JSON, or a
section in it that is not an object, is logged and ignored rather than thrown, so it cannot take down
an elevated Service; `UserSettings.Set` in turn never overwrites an existing file that is not valid
JSON. Writes go to a temporary file that is moved into place (a reader never sees a half-written
file); concurrent writers are last-one-wins. `Settings.Section()` merges the named section key-by-key
across the tiers.

The checked-in module-tier `settings.json` (and an optional, gitignored `settings.local.json`)
live at the **repo root**, not inside `src/Service/` or `src/Pager/` -- `src/Service` and
`src/Pager` build into one shared output folder (`src/Directory.Build.props`' `BaseOutputPath`), so
a single root-level pair avoids either app's own file silently clobbering the other's at that
shared path. `src/Directory.Build.targets` copies both into each app's own `$(OutDir)` after
`Build` (imperative `Copy` tasks, not a declarative `CopyToOutputDirectory` item -- the latter
propagates through any `ProjectReference`, which would leak these files into every test project
that references `Pager`/`Service` too) and `settings.json` alone into `$(PublishDir)` after
`Publish` -- `settings.local.json` is deliberately never published, since
`installer/Setup.wixproj` globs its entire publish output into the MSI and a developer's personal
override must never ship there.

A malformed module-tier `settings.json` throws (`SettingsError`); a malformed `settings.local.json` is
logged and ignored, since it's the optional/personal tier. If the module tier has no file, `Load()` falls
back to restrictive defaults (`debug=false`, `logLevel=error`) and logs a warning.

### settings.json -- `"tools"` section

Per-tool overrides of the core `settings.console`, read with `Settings.Section("tools")` on every launch (so a
change applies to the next launch without restarting Service) from any tier -- settings.json, the per-user file or
settings.local.json (showing a console is harmless, so the per-user tier is fine for it):

```json
"tools": { "hello": { "console": true } }
```

- `<tool name>.console` (boolean) -- whether that tool, named as in `mcp-registry/<name>.json`, gets a visible console
  window; the name is matched case-insensitively. Absent means the `console` default. A malformed entry (not an
  object, or a `console` that is not a boolean) is logged as a warning and ignored: the tool launches with the
  default, never fails to launch.

### settings.json -- `"secrets"` section

Read with `Settings.Section("secrets")`; written to the per-user file by the secrets file picker
([issue #61](https://github.com/croicu/desk-tools/issues/61)).

- `databasePath` (string, absolute path, default unset) -- the KeePassXC database (`.kdbx`, KDBX 4) the
  secrets code opens. Must be an existing **local** file: a UNC path or a network-drive path is refused (the
  elevated Service would otherwise authenticate to whatever server it names). Missing or empty means "ask":
  the file picker opens once and its answer is saved here. A value that is present but unusable (the file
  moved, a non-`.kdbx` path, a network path) is an error that names this setting, not a prompt.

### settings.json / settings.local.json -- `"settings"` object

- `idleTimeout` (number, seconds, default `600`) -- how long the resident process (`Host`, see
  `docs/ARCHITECTURE.md`) may go without accepting a connection before it exits. Interim activity
  signal only (accept == activity, for now) -- see
  [issue #5](https://github.com/croicu/desk-tools/issues/5). The clock only runs while no MCP tool
  launched via `mcp` is still alive; it restarts when the last one exits
  ([issue #45](https://github.com/croicu/desk-tools/issues/45)). An explicit `shutdown` request
  ignores this and exits immediately.
- `allowCallerRegistry` (boolean, default `false`) -- whether Service honors an `mcp` request's
  `registryDir` (a caller-chosen tool registry, `desk mcp <name> --registry <dir>`;
  [issue #69](https://github.com/croicu/desk-tools/issues/69)). Leave it off in anything shipped: the
  shipped registry is trusted because it sits next to Service, a caller-chosen one is not. Not read
  from the per-user settings file (which any process running as the user can write).
- `console` (boolean, default `false`) -- whether a tool Service launches gets a visible console window
  ([issue #44](https://github.com/croicu/desk-tools/issues/44)). Hidden for every tool by default, whatever its flavor
  (a .NET dll, a Python script) and whichever build: the tools talk over redirected stdio and never need one. This is
  the default for all tools; the `"tools"` section (below) overrides it per tool. Implemented with
  `CreateNoWindow`, which hides the window but not the tool's `conhost.exe`. Tool output on stderr is not captured
  either way: it appears in the console when one is visible and is lost otherwise.
- `logDir` (string, directory, default unset -- no file logging) -- installs a `FileLog` sink
  writing a timestamped log file into this directory; overridable per-invocation by `--log <dir>`
  (see the CLI section above). See `docs/ARCHITECTURE.md`'s `FileLog` entry.
- `port` (number, default `51823`) -- loopback TCP port `src/Service`'s `Host` listens on and
  `src/Desk` connects to (see the Echo protocol section above). Both processes read this from the
  same shared settings.json, with no direct dependency between them beyond that.

### MCP tool registry -- `mcp-registry/<name>.json`

Build-time-generated, not hand-written or checked in (see [issue #29](https://github.com/croicu/desk-tools/issues/29)
and `docs/ARCHITECTURE.md`'s `Directory.Build.targets` entry) -- one small file per MCP tool,
written to `$(OutDir)mcp-registry/` after a plain `dotnet build` (the same shared output folder
settings.json itself lands in) or `$(PublishDir)mcp-registry/` after `dotnet publish` (including the
MSI's own publish step, `installer/Setup.wixproj`, which now publishes `Pager.csproj` alongside
`Service.csproj`/`Desk.csproj` specifically so this ends up shipped there too). A .NET project opts
in via its own `<McpToolName>` MSBuild property; a Python tool opts in via an `<McpPythonTool>` item
in `scripts/Scripts.csproj` instead (see that project's own remarks and `docs/ARCHITECTURE.md`'s
entry for it) -- either way, each file is a full `.mcp.json`-shaped server fragment
(`type`/`command`/`args`/`env`, matching that file's own `"pager"` entry exactly) plus a `name`
field `.mcp.json` itself doesn't need (there, the tool's name is the surrounding object's own key;
this registry is one file per tool, so there's no such key to borrow one from):

```json
{"name": "pager", "type": "stdio", "command": "dotnet", "args": ["Pager.dll"], "env": {}, "secrets": [{"env": "NTFY_TOPIC", "entry": "ntfy", "key": "desk-pager"}]}
```

```json
{"name": "link-program-files", "type": "stdio", "command": "python", "args": ["scripts/link-program-files.py"], "env": {}}
```

- `name` (string) -- the tool's registry name, from the opted-in project's own `<McpToolName>`
  (.NET, e.g. `"pager"`) or `<McpPythonTool>` item (Python, e.g. `"link-program-files"`).
- `type` (string) -- MCP transport, always `"stdio"` for now (every tool this registry currently
  describes uses it).
- `command` (string) -- `"dotnet"` for a .NET-generated entry, `"python"` for a Python one (see
  `scripts/Scripts.csproj`) -- deliberately not a separate `"type": "dotnet"`-style field, since
  `.mcp.json`'s own `type` already means transport, a same-named field with a different meaning
  would collide. Whatever a tool's own runtime is, this is the extensibility point for it.
- `args` (array of strings) -- the tool file's bare name for a .NET entry (`$(TargetFileName)`, e.g.
  `"Pager.dll"`, sitting right next to the registry); a Python entry's own script instead sits one
  level down, in that same folder's `scripts/` subfolder (e.g. `"scripts/link-program-files.py"`), since
  `scripts/Scripts.csproj` copies `.py` files there rather than flat (keeps the repo's own
  `scripts/` layout mirrored in the build output). Either way, still resolvable relative to the
  registry file's own directory, not a full or repo-relative path -- `McpToolLauncher` needs no
  Python-specific path-resolution logic either way, since "resolve relative to the registry's own
  directory" already covers a subfolder path like any other relative path (see that project's own
  remarks).
- `env` (object) -- always `{}` for now; nothing needs a per-tool env var yet. (Service additionally sets `DESK_PARENT_PID` itself at launch, not via the registry -- see `docs/ARCHITECTURE.md`.)
- `secrets` (array, **optional**; absent when a tool declares none) -- the secrets the tool would like in its
  environment, each `{"env": "NTFY_TOPIC", "entry": "ntfy", "key": "desk-pager"}`: the environment variable it
  arrives in, the KeePass entry (full group path), and the `key: value` line in that entry's Notes. Generated
  from `<McpSecret>` items (see `docs/ARCHITECTURE.md`'s `ComputeMcpSecretsFragment` entry) and **only
  declarations -- never a value**. Like the whole manifest it is optional and never gates a launch
  (`docs/ARCHITECTURE.md`, "Invariants for MCP tools"): a malformed declaration (a missing or empty field, an
  invalid or repeated variable name, one the fragment's own `env` already sets, or `DESK_PARENT_PID`) is skipped
  with a warning and the tool starts without it. At launch Service reads each declared value from the user's
  KeePassXC database and sets it as that environment variable (`docs/ARCHITECTURE.md`, "Delivery"); the launch
  waits for the master-password dialog if it has to, and a failure or cancel starts the tool without the variable
  (with a warning).

`src/Pager` (.NET) and `scripts/link-program-files.py`/`scripts/unlink-program-files.py` (Python, via
`scripts/Scripts.csproj`) are the tools registered so far -- real MSBuild machinery in the end, just two different
opt-in mechanisms for the two different kinds of project. (The hello and goodbye tools that used to be here are test
fixtures under `tests/MCP` now, [issue #86](https://github.com/croicu/desk-tools/issues/86), with no registry entry.)

`src/Service/McpToolLauncher.cs` (see [issue #30](https://github.com/croicu/desk-tools/issues/30)
and `docs/ARCHITECTURE.md`'s own entry) is the first consumer: given a registry name, it reads and
parses that tool's fragment and spawns it as a child process with redirected stdin/stdout via
`src/Service/ToolLauncher.cs`. `Host`'s own `mcp` JSON-RPC method (see the Echo protocol section
above and [issue #32](https://github.com/croicu/desk-tools/issues/32)) is the actual external
trigger: it calls `McpToolLauncher.Launch`, then hands the caller direct access to the launched
tool's stdio via `DuplicateHandle` -- still no `.mcp.json` changes and no `src/Desk` consumer of
this yet, that remains a separate future step.
