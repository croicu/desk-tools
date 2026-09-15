# ARCHITECTURE.md

Modules, data flow, and contracts for `desk-tools`.

## Modules

<!-- One entry per file under src/Base/ (reusable scaffold: Logger/Settings/Context/Errors/
     Interfaces, plus src/Base/Sinks/ for the ILoggingSink implementations, compiled to its own
     Base.dll), src/Service/ (the resident-process CLI, references Base.csproj), src/Hello/ (a
     minimal stdio MCP server, references Base.csproj), src/Desk/ (a console client for
     src/Service's Host, references Base.csproj), and scripts/ (standalone Python utilities outside
     the .NET build entirely): what it owns, what it depends on. -->

Base.dll is designed to be safe inside a service hosting multiple heterogeneous clients in one
process, each "renting" its own `ExecutionContext` with independent settings/logging. See
CLAUDE.md's Architecture convention 10 -- all ambient state in `src/Base` (the registered-sinks
list, `ConsoleLog`'s/`DebugLog`'s/`FileLog`'s instance guards, `Settings.Current`, `Context.Current`,
`Correlation.Current`) is scoped with `AsyncLocal<T>` rather than plain `static` fields, so one
client's context can't see or disturb another's.

`Correlation` (`src/Base/Correlation.cs`): a short, lazily-generated hex id for the current logical
operation, exposed via `Correlation.Current` -- flows to nested awaits/Tasks like any other
`AsyncLocal` state here, so one operation's own id stays stable across it, distinct from an
unrelated concurrent operation's. Its one consumer today is `FileLog` (below), which tags each
line with it so a log file's interleaved lines (from concurrent operations, in a future
multi-client host) can still be attributed. Deliberately its own leaf type rather than living on
`Context`: `Context.Create` already constructs sinks from `Croicu.Desk.Tools.Base.Sinks`, so putting
the id there instead would make `Sinks` depend back on `Context` -- a cycle worth avoiding even
within one project (Architecture convention 9).

`Logger` (`src/Base/Logger.cs`) fans every output call (`Info`, `Warning`, `Print`, etc.) out to
*all* currently-registered sinks sequentially, in registration order -- not a last-one-wins
override. The three built-in `ILoggingSink` implementations live in `src/Base/Sinks/`, one file
each, under the `Croicu.Desk.Tools.Base.Sinks` namespace (folder-matching namespace, distinct from
`Logger.cs`'s own `Croicu.Desk.Tools.Base`) -- their file/class/namespace names deliberately drop
the redundant "Sink" suffix the folder already conveys:

- `DiagnosticsLog.cs` -- the base sink. Owns its own pending buffer (used by
  `Flush`/`Clear`/`Drain`), so N active sinks each independently record everything without
  corrupting or duplicate-writing into a shared one -- see its class doc comment for the full
  rationale.
- `ConsoleLog.cs` -- adds level/category filtering and prints to the console.
- `DebugLog.cs` -- writes unconditionally (no level/category filtering) to the attached debugger's
  Debug Output window. Its actual write target is an injectable `Action<string>` defaulting to
  `System.Diagnostics.Debug.WriteLine`, since that BCL method is `[Conditional("DEBUG")]` (both
  un-delegate-able directly and compiled out entirely in a Release build) -- tests inject a
  collecting delegate instead of depending on the separate `System.Diagnostics.TraceSource` package
  this TFM would otherwise need for `Debug.Listeners`.
- `FileLog.cs` -- writes unconditionally (same reasoning as `DebugLog`: a persisted file is for
  later post-mortem debugging, not real-time viewing) to a new file inside a caller-supplied
  directory, named after the UTC timestamp the sink was created. Each line is prefixed
  `[timestamp][correlationId][LEVEL][category]` (see `Correlation` above) rather than `ConsoleLog`'s
  bare `[LEVEL][category]`, since a log file can interleave lines from concurrent operations in a
  way the console (read in real time, one operation at a time) doesn't.

`Context` (`src/Base/Context.cs`) is where a host's `Settings` gets fully wired into `Logger`, in
one call: applies `Settings.LogLevel`/`LogCategories`/`ExcludedCategories` to the console sink
(`Logger.ConfigureConsole`), resolves `Settings.Debug` with a host's own CLI override (e.g.
Service's `--debug`) into one `Debug` value, installing a `DebugLog` alongside the console sink
when that resolves true, and resolves `Settings.LogDir` with a host's own `--log <dir>` override
(the override taking precedence when both are given, same direction as the debug override) into a
`FileLog`, installed alongside whatever else is active when non-null -- so a host's `Program.cs`
just calls `Context.Create(settings, debugOverride: ..., logDirOverride: ...)` once and never has
to remember any of these wiring steps itself. Deliberately not `Settings.Load()`'s job: that stays a
pure, side-effect-free settings.json parse (safe to call repeatedly, e.g. from tests), while
installing a sink is host-level policy. Takes plain `bool`/`string?` overrides rather than a host's
own CLI-arguments type, since Architecture convention 9 keeps `src/Base` from ever referencing a
consuming app's namespace -- this keeps `Context` reusable by any host, not coupled to one app's
flag-parsing shape. Exposed via `Context.Current`, `AsyncLocal`-scoped like `Settings.Current`, for
the same multi-client reason as the rest of `src/Base`'s ambient state.

`Context.Start` goes one step further and owns a host's *entire* bootstrap ceremony, not just the
`Settings`-into-`Logger` wiring: brackets an injected `IConsole`'s lifecycle around everything (see
`Interfaces.cs` -- same convention-9 reasoning as `Context.Create`'s plain `bool` override, so
`src/Base` never references a host's own console-attach implementation, e.g. Service's
`ServiceConsole` under `src/Service/Platform/`), loads `Settings` (a malformed settings.json becomes
a logged, `appName`-prefixed error and exit code 1, same as a hand-rolled version of this would do),
calls `Create`, then invokes the host's `Func<int> run` delegate -- catching `AppError` the same way
`Create`'s resolved `Debug` already implies (rethrow when debugging, logged error + exit code 1
otherwise). `src/Service/Program.cs` shows the resulting shape: `Main(string[] args)` -- fixed to
that exact signature, since a `Main` with any extra parameters (even optional ones) isn't recognized
as a CLR entry point at all (`CS5001`) -- forwards to `Start(argv, settingsPath)` (a differently
named method specifically so it can carry a `settingsPath` test hook Main itself can't), which
parses CLI args and calls `Context.Start(console, "desk-tools", settingsPath, debug, logDir, Run)`;
`Run()` is the actual run body passed as that delegate, reading `Settings.Current`/`Context.Current`
for whatever it needs since both are already resolved by the time `Context.Start` invokes it.

`src/Service/Host.cs` (chunk 0 of the resident-process skeleton -- see
[issue #5](https://github.com/croicu/desk-tools/issues/5)): the resident process's bare hosting
mechanics, ahead of real JSON-RPC framing/dispatch. Owns a `TcpListener` on
`ISettingsProvider.Port` (shared settings.json, default `51823` -- not yet the real `say_hello`
port), an accept loop on a dedicated background thread that dispatches each connection to the
thread pool via a named handler (reads at most one newline-delimited line and writes it straight
back, then closes -- plain-text echo, still ahead of real JSON-RPC framing/dispatch), and a
`System.Threading.Timer`-driven idle check that exits the process once no connection has been
accepted for `Settings.IdleTimeout` and none is currently in flight. A client can also trigger
shutdown directly by sending `Host.ShutdownCommand` instead of an ordinary line -- still echoed
back first, then the exact same `_shutdownSignal` the idle-timeout check already sets gets set from
the connection handler instead, so `WaitForIdleShutdown`'s teardown (stop listening, join the
accept thread) runs identically regardless of which of the two triggered it (see
[issue #22](https://github.com/croicu/desk-tools/issues/22) -- deliberately one shutdown path, not
two). `Program.cs`'s `Run()` first acquires `SingletonGuard` (`src/Service/Platform/`) -- a
named `Mutex` (`Local\Desk Tools Service`) guarding against a second `Host` racing for the same
port (see [issue #27](https://github.com/croicu/desk-tools/issues/27)); if another instance already
holds it, `Run()` logs and returns immediately, no `Host` constructed, no port bind attempted. Only
then does it construct `Host` and call `Run()`. Console attach/detach on Windows (`ServiceConsole`,
same `src/Service/Platform/` split) is unrelated prior work -- see the `wingui-console-poc` history.

`src/Service/Platform/{Windows,Linux,Neutral}/SingletonGuard.cs`: the named-`Mutex` guard
`Program.cs`'s `Run()` acquires before constructing `Host`, split across the same three
platform folders `ServiceConsole` already uses (same class name/API shape in each, so `Program.cs`'s
call site needs no conditional branch -- whichever folder `Service.csproj`'s RID-based `ItemGroup`s
select is the only one actually compiled in). Only `Windows` has a real implementation; `Linux` and
`Neutral` are no-ops that always "acquire" successfully -- not because a named `Mutex` is incapable
on Linux (see the Windows variant's own remarks: it's genuinely cross-platform in .NET), but because
there's nothing to guard *against* yet on a platform with no scheduled-task-equivalent resident
process launcher wired up at all (the likely eventual mechanism there is a `systemd --user` service,
the closest analog to the scheduled task -- both "start at login" and "trigger on demand", unlike
cron's periodic-only model). `Local\`, not `Global\`, in the Windows variant -- sufficient for the
actual threat (the scheduled task and any Desk-triggered auto-start both always run under the same
interactively-logged-on user's own session) without needing `SeCreateGlobalPrivilege`, not
guaranteed for every account this might run under. A crashed previous holder's abandoned mutex still
hands over ownership there (`AbandonedMutexException`, caught and logged as a warning, not treated
as a failure) -- no separate crash-detection scheme needed, a named `Mutex` gives that for free.
Unlike `ServiceConsole`, which has no unit tests at all (verified live instead, since the default
Neutral test build never exercises its real per-platform behavior), `SingletonGuard` gets its own
matching test-side split: `tests/Service/Service.Tests.csproj` mirrors `Service.csproj`'s exact
Platform/-selection ItemGroups (see that project file's own comment), so
`tests/Service/Unit/Platform/Windows/SingletonGuardTests.cs` (real contention/abandonment behavior,
only meaningful -- and only compiled at all -- under `dotnet test -r win-x64`) pairs with whichever
`SingletonGuard` variant the `Service.csproj` `ProjectReference` itself actually compiled for that
same RID, while `tests/Service/Unit/Platform/{Neutral,Linux}/SingletonGuardTests.cs` test the no-op
variants' own documented contract under the default (no-RID) `dotnet test` and `-r linux-x64`
respectively. See CLAUDE.md's Commands section for the exact `-r win-x64` invocation.

`installer/Package.wxs` registers `Service.exe` as a Windows scheduled task (`"Desk Tools
Service"`), not a formal Windows Service (SCM) -- deferred custom actions shelling out to
`schtasks.exe`, since WiX has no native scheduled-task element (see
`tasks/wix-scheduled-task-authoring.md` and [issue #19](https://github.com/croicu/desk-tools/issues/19)).
Runs at the installing user's own logon (their interactive token, highest privilege that account
allows), started immediately after install too rather than only from the next logon; cleaned up
on rollback/genuine uninstall.

`src/Hello/Program.cs`: a minimal, hand-rolled (no MCP SDK) MCP server over stdio -- see
`docs/PROTOCOL.md` for the exact methods/shapes it implements. Reads newline-delimited JSON-RPC
requests from stdin in a loop until EOF (or a `stop` tool call, below) ends it, dispatches
`initialize`/`tools/list`/`tools/call`, and writes at most one response line per request via
`Logger.Print` (never a leveled `Logger.Info`/etc. call, since the stdio transport requires stdout
to carry only valid MCP messages). Exposes two tools: `say_hello`, which returns the text "Hi from
MCP"; and `stop`, a graceful shutdown -- `HandleLine`/`HandleToolsCall` return a `bool` ("keep
reading?") up through the dispatch chain specifically so `stop`'s handler can write its own response
first, then have `Run`'s read loop break the same way it would on EOF, rather than an abrupt
`Environment.Exit` that could cut output off mid-flush. Exists so a dev loop rebuilding `Hello.dll`
can ask a locally-running instance to release its file lock via a normal `tools/call` instead of an
external process kill -- see the `stop-hello` skill under `.claude/skills/`. No Settings/persistent
state -- everything it needs is a handful of `const`s and two static tool definitions. Parses one
CLI flag of its own, `--log <dir>`
(see `docs/PROTOCOL.md`), since a stdio server that can never print to its own console needs a
`FileLog` file as its one way to be debugged after the fact.

`src/Desk/Program.cs`: a plain console client for `src/Service`'s `Host` -- connects to its
loopback listener on `ISettingsProvider.Port` (the same shared settings.json `Host` itself reads,
so Desk needs no `ProjectReference` on `Service.csproj` to learn the port), sends one
newline-delimited line, waits for the same line echoed back, and exits. Requires exactly one bare
subcommand (`DeskCommand`, a verb -- not a `--`-prefixed flag, since the two are mutually
exclusive): `ping` sends the fixed line `ping` and prints whatever comes back via `Logger.Print`;
`shutdown` sends `Host.ShutdownCommand` (kept in sync by hand as its own literal, since Desk
deliberately has no `ProjectReference` on `Service.csproj` to reference the real constant) and
prints a fixed confirmation instead of the raw echoed text. `Start` forwards the parsed command into
`Run` via a trivial forwarding lambda (`() => Run(arguments.Command)`, same pattern Hello's own
`Start` already uses for its `input` parameter), since `Context.Start`'s `run` delegate is a plain
`Func<int>`. The actual connect/send/read exchange lives in `Client` (`src/Desk/Client.cs`),
constructed with an optional port override (same pattern as `Host`'s own constructor) so a test can
point it at a test-local peer instead of the real settings-resolved port -- `Client` itself has no
notion of "ping" vs "shutdown", it only ever sends one line and returns what comes back; the
command's meaning is entirely `Host`'s to interpret, based on content. A closed connection or
failure to connect raises a plain `AppError`, surfaced through the same `AppError`-to-exit-code-1
handling every other app here already gets from `Context.Start`. `shutdown` stays fail-fast there,
no retry, no auto-start (shutting down something that isn't running isn't worth auto-starting for);
`ping` instead falls back to `ServiceLauncher.StartAndWaitUntilReachable`
(`src/Desk/ServiceLauncher.cs`) on that first failure, then retries once (see
[issue #27](https://github.com/croicu/desk-tools/issues/27)). No `IConsole` concerns of its own (an
ordinary console app, already console-attached), hence its own `VoidConsole`, same reasoning as
Hello's but for the opposite reason (Hello is headless; Desk is already attached).

`src/Desk/ServiceLauncher.cs`: runs the installed scheduled task (`schtasks /run /tn "Desk Tools
Service"`) rather than launching `Service.dll` as a plain child process -- `Host` is meant to run at
the scheduled task's own elevated integrity level (`installer/Package.wxs`'s `/rl highest`), and a
plain `Process.Start` from Desk would instead run it at Desk's own (typically lower) integrity,
defeating that. `schtasks /run` itself needs no elevation from the caller; Task Scheduler runs the
task under its own configured principal regardless. Polls (a real `ping`, not just a raw TCP
connect) until reachable or a bounded timeout elapses, throwing `AppError` either way on failure --
including when Service was never installed at all (the scheduled task isn't registered), a
deliberate limitation: silently falling back to an unprivileged spawn would be exactly the mistake
this class exists to avoid. Distinct from `tests/Desk/Integration/ServiceTests.cs`'s own spawn logic
despite the surface similarity -- that one launches `Service.dll` directly from the *test*
assembly's own output folder (a different directory than Service's, so it walks up to the repo root
and back down); this always goes through the scheduled task instead, and never needs to resolve
`Service.dll`'s path itself at all.

`scripts/shutdown_service.py`: a standalone, plain-stdlib Python script that sends `Host` the same
`shutdown` request `desk shutdown` does, for shutting the resident process down without the .NET
toolchain involved. Outside `src/`/`tests/` entirely -- no build step, no project file, just a
script -- so it necessarily duplicates a few things `src/Desk` already has rather than sharing them
across languages: its own copy of the `"shutdown"` sentinel literal (kept in sync by hand with
`Host.ShutdownCommand`, same as `src/Desk`'s own copy), and its own settings.json-reading logic
(`port`, local overriding, default `51823` -- a deliberately simplified read of just the two
repo-root files, not `Settings.cs`'s full module-tier/working-directory-tier merge, since that
distinction doesn't apply to a script with a fixed location).

`scripts/service_mcp_server.py`: a second, minimal, hand-rolled MCP server over stdio (see
`docs/PROTOCOL.md`), registered in `.mcp.json` as `service` alongside `src/Hello`'s own `hello`
entry -- same shape (no SDK, same protocol version, same error codes) but in Python rather than C#,
exposing one tool, `shutdown`, that calls straight into `shutdown_service.py`'s own
`find_repo_root`/`resolve_port`/`request_shutdown` via a plain sibling import (reusing that logic
rather than a third independent copy of it). Lets an MCP client (e.g. a Claude Code session working
in this repo) shut `Host` down as a normal tool call, the same way `src/Hello`'s own `stop` tool
lets one gracefully stop Hello -- see the `stop-service` skill under `.claude/skills/` (mirroring
the earlier `stop-hello` one), which calls `mcp__service__shutdown` instead of killing the process
when a build is blocked by a locked `Service.dll`/`Base.dll`.

## Data flow

<!-- How data enters, gets transformed, and leaves the system. -->

`Program.cs` loads `Settings` (see `docs/PROTOCOL.md`'s settings.json discovery order) before
constructing `Host`, so every knob `Host` reads (`IdleTimeout`, `Port`) is already resolved by the
time it starts. At runtime, `Host` reacts to a TCP connection being accepted: it's both the interim
activity signal and the trigger to read one line and echo it back before closing. `src/Desk` is the
other end of that exchange -- it resolves the same `Port` from its own `Settings.Load()` call (same
shared settings.json, no direct dependency between the two processes beyond that shared file), then
connects, sends, and reads back exactly what `Host` echoes.

## Contracts

<!-- Interfaces.cs: public contracts -- persisted/shared data (plain classes/records, no behavior)
     plus behavioral interfaces meant for a consumer to implement/inject (e.g. ILoggingSink,
     already scaffolded there). Contracts.cs: behavioral interfaces that wire this project's own
     internals together -- never referenced by external consumers, unlike Interfaces.cs's. -->
