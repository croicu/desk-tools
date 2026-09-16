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

## MCP (`scripts/service_mcp_server.py`)

Same shape as `src/Hello`'s server above (hand-rolled, no SDK, newline-delimited JSON-RPC 2.0 over
stdio, protocol version `2025-06-18` only, same error codes) but in plain-stdlib Python instead of
C#, and exposing Service's shutdown rather than Hello's own tools -- registered in `.mcp.json` as
`service`.

- `tools/list` -- one tool, `shutdown` (same empty `inputSchema` shape as `src/Hello`'s tools).
- `tools/call` -- `shutdown` sends the Echo protocol's `shutdown` request to `Host` (see below) and
  returns `{"content":[{"type":"text","text":"Shutdown requested."}],"isError":false}` on success.
  Unlike `src/Hello`'s tools, this one can genuinely fail at runtime (`Host` isn't reachable) --
  that's `isError: true` with an explanatory message, a tool-level failure, not a JSON-RPC protocol
  error (an unknown tool *name* is still `-32602`, same distinction `src/Hello` already draws).

## Echo protocol (`src/Service`'s `Host` / `src/Desk`)

JSON-RPC 2.0, newline-delimited, one request per TCP connection (see
[issue #31](https://github.com/croicu/desk-tools/issues/31) -- this section's name predates the
switch away from the original plain-text echo protocol it replaced; kept for continuity with
`docs/ARCHITECTURE.md`'s own references). A client connects to `Host`'s loopback listener (port
from `settings.json`'s `port`, see below), writes exactly one JSON-RPC request line, and reads
exactly one JSON-RPC response line back, then the connection closes. A request with no `id` is a
notification per the JSON-RPC spec: consumed, no response ever sent, regardless of method. A client
that sends nothing before closing its own end likewise gets no reply, just a closed connection.
Same error codes/envelope shapes as `src/Hello`'s own hand-rolled dispatch (see the MCP section
above): malformed JSON is `-32700` (Parse error, `id: null`); a request missing `method` is
`-32600` (Invalid Request); an unrecognized method is `-32601` (Method not found); an unexpected
exception while handling a request is `-32603` (Internal error).

Two methods today, both no-params:

- `ping` -- result `"pong"`. A liveness/reachability check.
- `shutdown` -- result `"ok"`. Asks `Host` to shut down gracefully once it has replied
  ([issue #22](https://github.com/croicu/desk-tools/issues/22)) -- still replies first, so the
  client gets a definitive acknowledgment before the listener actually stops. Shares `Host`'s
  existing idle-timeout shutdown path rather than a separate mechanism, so the teardown itself
  (stop listening, join the accept thread) is identical either way.

`src/Desk` is this protocol's main client, with two subcommands (see the CLI section above):

- `desk ping` -- sends a `ping` request, prints the result (`pong`), and exits. If `Host` isn't
  reachable, auto-starts it via the installed scheduled task (`schtasks /run /tn "Desk Tools
  Service"`, see `docs/ARCHITECTURE.md`'s `ServiceLauncher` entry and
  [issue #27](https://github.com/croicu/desk-tools/issues/27)) -- deliberately *not* a plain child
  process, since `Host` is meant to run at the scheduled task's own elevated integrity level, not
  Desk's own (typically lower) one -- then retries once. Only works once Service has actually been
  installed via the MSI (the scheduled task must already be registered); fails with a clear error
  otherwise, or if it never becomes reachable within the startup wait.
- `desk shutdown` -- stays fail-fast, no retry, no auto-start (shutting down something that isn't
  running isn't an error worth auto-starting for). Sends a `shutdown` request and, on success,
  prints a fixed confirmation rather than the raw JSON-RPC result.

`scripts/shutdown_service.py` is a second, standalone client -- plain-stdlib Python (`socket`/
`json`/`argparse`, no dependencies), for shutting `Host` down without the .NET toolchain involved.
Resolves the port the same way (`settings.json`/`settings.local.json`'s `port`, local overriding,
default `51823`), or `--port` to skip that entirely. Its own copy of the `"shutdown"` method name is
kept in sync by hand with `Host.ShutdownMethod`, same as `src/Desk`'s.

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

### MCP tool registry -- `mcp-registry/<name>.json`

Build-time-generated, not hand-written or checked in (see [issue #29](https://github.com/croicu/desk-tools/issues/29)
and `docs/ARCHITECTURE.md`'s `Directory.Build.targets` entry) -- one small file per MCP tool,
written to `$(OutDir)mcp-registry/` after a plain `dotnet build` (the same shared output folder
settings.json itself lands in) or `$(PublishDir)mcp-registry/` after `dotnet publish` (including the
MSI's own publish step, `installer/Setup.wixproj`, which now publishes `Hello.csproj` alongside
`Service.csproj`/`Desk.csproj` specifically so this ends up shipped there too), by any project that
opts in via its own `<McpToolName>` MSBuild property. Each file is a full `.mcp.json`-shaped server
fragment (`type`/`command`/`args`/`env`, matching that file's own `"hello"` entry exactly) plus a
`name` field `.mcp.json` itself doesn't need (there, the tool's name is the surrounding object's own
key; this registry is one file per tool, so there's no such key to borrow one from):

```json
{"name": "hello", "type": "stdio", "command": "dotnet", "args": ["Hello.dll"], "env": {}}
```

- `name` (string) -- the tool's registry name, from the opted-in project's own `<McpToolName>`
  (e.g. `"hello"`).
- `type` (string) -- MCP transport, always `"stdio"` for now (every tool this registry currently
  describes uses it).
- `command` (string) -- always `"dotnet"` for an entry this MSBuild-based generation produces
  (every project it can run against is necessarily a .NET one). A future non-.NET (e.g. Python)
  tool's own entry would set this to something else (e.g. `"python"`) instead -- deliberately not a
  separate `"type": "dotnet"`-style field, since `.mcp.json`'s own `type` already means transport, a
  same-named field with a different meaning would collide.
- `args` (array of strings) -- the built file's bare name (`$(TargetFileName)`, e.g. `"Hello.dll"`),
  resolvable relative to the registry file's own directory, not a full or repo-relative path.
- `env` (object) -- always `{}` for now; nothing needs a per-tool env var yet.

`src/Hello` is the one tool registered so far; a future non-.NET (e.g. Python) tool could add its
own entry to the same folder by convention, without needing any of this MSBuild machinery itself.

`src/Service/McpToolLauncher.cs` (see [issue #30](https://github.com/croicu/desk-tools/issues/30)
and `docs/ARCHITECTURE.md`'s own entry) is the first consumer: given a registry name, it reads and
parses that tool's fragment and spawns it as a child process with redirected stdin/stdout via
`src/Service/ToolLauncher.cs`. Still just that process+pipes primitive -- not yet wired to any
external trigger (no echo/wire-protocol command, no `.mcp.json` changes, no actual MCP JSON-RPC
proxying through a client connection).
