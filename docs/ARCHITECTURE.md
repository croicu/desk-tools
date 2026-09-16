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
mechanics. Owns a `TcpListener` on `ISettingsProvider.Port` (shared settings.json, default `51823`
-- not yet the real `say_hello` port), an accept loop on a dedicated background thread that
dispatches each connection to the thread pool via a named handler (reads at most one
newline-delimited JSON-RPC 2.0 request line, dispatches it, and writes at most one JSON-RPC
response line back, then closes -- see [issue #31](https://github.com/croicu/desk-tools/issues/31)
for the switch away from this class's earlier plain-text echo protocol; mirrors
`src/Hello/Program.cs`'s own hand-rolled dispatch style, same error codes/envelope shapes, just one
request per TCP connection instead of a persistent stdio read loop), and a
`System.Threading.Timer`-driven idle check that exits the process once no connection has been
accepted for `Settings.IdleTimeout` and none is currently in flight. A client can also trigger
shutdown directly with a `Host.ShutdownMethod` request instead of `Host.PingMethod` -- still
replied to first, then the exact same `_shutdownSignal` the idle-timeout check already sets gets
set from the connection handler instead, so `WaitForIdleShutdown`'s teardown (stop listening, join
the accept thread) runs identically regardless of which of the two triggered it (see
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
Unlike `ServiceConsole`, which has no unit tests at all (verified live instead), `SingletonGuard`
gets its own matching test-side split: `tests/Service/Service.Tests.csproj` mirrors
`Service.csproj`'s exact Platform/-selection ItemGroups (see that project file's own comment), so
`tests/Service/Unit/Platform/Windows/SingletonGuardTests.cs` (real contention/abandonment behavior)
pairs with whichever `SingletonGuard` variant the `Service.csproj` `ProjectReference` itself
actually compiled for that same RID -- win-x64 is `tests/Directory.Build.props`'/
`src/Directory.Build.props`' own default now (see their own remarks), so a plain `dotnet test`
already exercises this real variant, not the no-op stand-in. Reaching
`tests/Service/Unit/Platform/{Neutral,Linux}/SingletonGuardTests.cs` instead (which test the no-op
variants' own documented contract) needs an explicit override -- see CLAUDE.md's Commands section
for the `DESK_TOOLS_RID` environment variable this now takes.

`installer/Package.wxs` registers `Service.exe` as a Windows scheduled task (`"Desk Tools
Service"`), not a formal Windows Service (SCM) -- deferred custom actions shelling out to
`schtasks.exe`, since WiX has no native scheduled-task element (see
`tasks/wix-scheduled-task-authoring.md` and [issue #19](https://github.com/croicu/desk-tools/issues/19)).
Runs at the installing user's own logon (their interactive token, highest privilege that account
allows), started immediately after install too rather than only from the next logon; cleaned up
on rollback/genuine uninstall.

`installer/Setup.wixproj`'s `PublishAppForSetup` target publishes `Service.csproj`, `Hello.csproj`,
and `Desk.csproj` -- all three, not Service alone -- into the same shared `$(AppPublishDir)`
`Package.wxs`'s `Files` glob bundles wholesale (see issue #29): none of the three reference each
other, so publishing only one would never pull the others in, and `Hello`'s own build-time-generated
`mcp-registry/hello.json` (see `docs/PROTOCOL.md`) would otherwise reference a `Hello.dll` that was
never actually shipped in the MSI at all. Confirmed by reading the built `.msi`'s own `File` table
directly, not just the intermediate publish folder -- `Hello.exe`/`Desk.exe`/`Service.exe` and
`mcp-registry/hello.json` are all genuinely embedded.

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
so Desk needs no `ProjectReference` on `Service.csproj` to learn the port), sends one JSON-RPC
request line, waits for the response, and exits. Requires exactly one bare subcommand
(`DeskCommand`, a verb -- not a `--`-prefixed flag, since they're mutually exclusive): `ping`
sends a `ping` request and prints its result (`"pong"`) via `Logger.Print`; `shutdown` sends a
`Host.ShutdownMethod` request (kept in sync by hand as its own literal, since Desk deliberately has
no `ProjectReference` on `Service.csproj` to reference the real constant) and prints a fixed
confirmation instead of the raw JSON-RPC result; `mcp <name>` (see
[issue #33](https://github.com/croicu/desk-tools/issues/33)) launches the named tool and bridges
this process's own stdio to it -- see `McpProxy` below. `Start` forwards the parsed
`CliArguments` into `Run` via a trivial forwarding lambda (`() => Run(arguments)`, same pattern
Hello's own `Start` already uses for its `input` parameter), since `Context.Start`'s `run` delegate
is a plain `Func<int>`. For `mcp`, `Start` also installs a silent `DiagnosticsLog` sink *before*
`Context.Start` can install a real, printing one -- once `McpProxy.Run` starts relaying the
launched tool's own stdout, this process's stdout must carry only that traffic, the same reasoning
`src/Hello/Program.cs`'s own `Start` already documents for itself (no `ProjectReference` to
cross-link a real `<see cref>` to it). The actual connect/send/read exchange lives in `Client`
(`src/Desk/Client.cs`), constructed with an optional port override (same pattern as `Host`'s own
constructor) so a test can point it at a test-local peer instead of the real settings-resolved
port -- `Client.Send` has no notion of "ping" vs "shutdown", it only ever sends a method name with
no params and returns the string result; a separate `Client.LaunchMcpTool` method exists
specifically for `mcp`'s own richer contract (params, and a structured `{"stdin", "stdout"}`
result) rather than generalizing `Send` prematurely. A closed connection, a JSON-RPC error
response, or a failure to connect all raise a plain `AppError`, surfaced through the same
`AppError`-to-exit-code-1
handling every other app here already gets from `Context.Start`. `shutdown` stays fail-fast there,
no retry, no auto-start (shutting down something that isn't running isn't worth auto-starting for);
`ping` and `mcp` instead fall back to `ServiceLauncher.StartAndWaitUntilReachable`
(`src/Desk/ServiceLauncher.cs`) on that first failure, then retry once (see
[issue #27](https://github.com/croicu/desk-tools/issues/27)) -- `mcp`'s own retry also fires on a
non-reachability `AppError` (e.g. an unregistered tool name), a known, minor imprecision inherited
from `ping`'s own existing pattern rather than a new one. No `IConsole` concerns of its own (an
ordinary console app, already console-attached), hence its own `VoidConsole`, same reasoning as
Hello's but for the opposite reason (Hello is headless; Desk is already attached).

`src/Desk/McpProxy.cs` (issue #33): bridges this process's own stdin/stdout to a tool Service
already launched and handed direct pipe access to via `mcp`'s `DuplicateHandle`-based handoff (see
`Host`'s own `mcp` entry above) -- what makes `desk mcp <name>` usable as a real `.mcp.json`
`command` entry pointing at Desk instead of the tool's own executable directly. Opens the two
duplicated handle values as a `SafeFileHandle`/`FileStream` pair each (same construction the
original throwaway spike and `tests/Service/Integration/HelloTests.cs`'s own `mcp` test already
use), then pumps lines in both directions: one background thread reads the tool's stdout and
writes each line via `Logger.Print` (not raw `Console.Write*`, per CLAUDE.md's "Console.* is
confined to src/Base/Sinks" rule -- `Program.Start`'s silent sink means nothing else ever reaches
real stdout while this runs), while the main thread reads this process's own stdin (injectable for
tests, matching Hello's own `Run(TextReader? input)` seam) and writes each line to the tool's
duplicated stdin. Blocks until its own stdin reaches EOF (the parent MCP client disconnected), then
closes its own copy of the tool's stdin -- the same graceful-EOF shutdown path a real client
disconnect already takes elsewhere in this repo -- and gives the output pump a bounded 5s chance to
finish draining before returning. Desk itself never parses the relayed traffic, purely a line
relay -- same "no SDK, hand-roll the transport" approach the rest of this repo's MCP surface
already takes.

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
JSON-RPC `shutdown` request `desk shutdown` does, for shutting the resident process down without
the .NET toolchain involved. Outside `src/`/`tests/` entirely -- no build step, no project file,
just a script -- so it necessarily duplicates a few things `src/Desk` already has rather than
sharing them across languages: its own copy of the `"shutdown"` method-name literal (kept in sync
by hand with `Host.ShutdownMethod`, same as `src/Desk`'s own copy), its own minimal JSON-RPC
request/response envelope handling (no error-code-specific behavior, just success-vs-error), and
its own settings.json-reading logic
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

`src/Directory.Build.targets`'s `GenerateMcpRegistryEntry` target (see `docs/PROTOCOL.md`'s MCP
tool registry entry and [issue #29](https://github.com/croicu/desk-tools/issues/29)): a first,
deliberately small step toward CLAUDE.md's own Mission ("a service for running MCP servers... at a
high privilege mode") -- at build time, writes one small `{"name": ..., "reference": ...}` JSON file
per MCP tool into `$(OutDir)mcp-registry/`, for any project that opts in via its own
`<McpToolName>` property (`src/Hello/Hello.csproj` sets `<McpToolName>hello</McpToolName>`).
Deliberately a property, not an item, unlike `CopySettingsToOutput`'s own `settings.json`/
`settings.local.json` `Copy` tasks above -- MSBuild properties don't propagate to a referencing
project's own build the way items can, so `Hello.Tests` referencing `Hello.csproj` doesn't also
pick up a registry entry the way `CopyToOutputDirectory` items once leaked into every test project
(see that same section's own history). `src/Service/McpToolLauncher.cs` is the first consumer (see
below); `Host`'s own `mcp` JSON-RPC method (issue #32) is the real trigger on top of it -- but
Service actually *proxying* a client connection's own MCP JSON-RPC traffic through to a registered
tool (rather than handing the caller direct `DuplicateHandle`-based access to it, as issue #32
does) remains a deliberately separate, not-yet-started alternative, only worth building if the
`DuplicateHandle` approach ever turns out insufficient (e.g. a future non-Windows host) -- MCP's
stdio transport requires whatever process launches a server to also hold its stdin/stdout pipes,
which a proxying Service would satisfy by keeping the pipes itself and relaying bytes, instead of
handing them off the way `HandleDuplicator` does.

`scripts/Scripts.csproj`/`scripts/goodbye.py`: the first non-.NET MCP registry entry -- a
`Microsoft.Build.NoTargets` project (no C# of its own, just custom MSBuild targets) since Python
tools live in `scripts/` (edited/checked in there, matching `shutdown_service.py`/
`service_mcp_server.py`'s own home) rather than each getting a `src/`-style per-tool project.
Explicitly `<Import>`s `src/Directory.Build.props` (`scripts/` isn't a descendant of `src/` or
`tests/`, so MSBuild's own auto-import wouldn't find it) specifically to reuse its
`BaseOutputPath`/`RuntimeIdentifier` default rather than duplicating them -- the whole point being
that a Python tool's build-time destination is the *identical* shared `out/<Configuration>/
net10.0/[<RID>/]` folder `Service.csproj`/`Hello.csproj`/`Desk.csproj` already build into. An
`<McpPythonTool Include="goodbye" />` item (mirroring `<McpToolName>`'s per-project opt-in for .NET
tools, just item-based since this one project covers every Python tool) drives two pairs of
targets, batched via `Inputs`/`Outputs` over the item so each runs once per tool: one pair copies
`scripts/<name>.py` into `$(OutDir)`/`$(PublishDir)` (`AfterTargets="Build"`/`"Publish"`, mirroring
`Directory.Build.targets`' own `CopySettingsToOutput`/`ToPublish` split), the other writes that
tool's own `mcp-registry/<name>.json` fragment there (`"command": "python"` instead of `"dotnet"`,
otherwise identical shape to `GenerateMcpRegistryEntry`'s own output below). Because the script ends
up co-located with `Service.exe` exactly like a .NET tool's own `.dll` already is,
`McpToolLauncher`/`ToolLauncher` need zero Python-specific code -- the existing "args resolved
relative to the registry's own directory" convention already just works. `scripts/goodbye.py`
itself mirrors `service_mcp_server.py`'s own hand-rolled JSON-RPC shape (no SDK, same protocol
version/error codes) but fully self-contained -- one tool, `say_goodbye`, returning `"Bye from
MCP"`, no `find_repo_root`/settings-reading logic needed since it has no Service-control behavior
of its own. Verified live end-to-end (`desk mcp goodbye`, a real elevated `Service` launching the
real Python process) and by `tests/Service/Integration/GoodbyeTests.cs`.

`src/Service/ToolLauncher.cs`/`ToolProcess.cs` (see
[issue #30](https://github.com/croicu/desk-tools/issues/30)): the process+pipes primitive
`McpToolLauncher`/`HandleDuplicator` are built on. `ToolLauncher.Launch` is a generic
child-process spawn given an already-resolved `command`/`args`/`env`/working directory -- both
standard input and standard output redirected (`UseShellExecute = false`), no BOM on the write side
(same `Encoding(encoderShouldEmitUTF8Identifier: false)` reasoning as `Host.WriteEncoding`/
`Client.WriteEncoding`, since a redirected child's default `StandardInputEncoding` is the OS
codepage, not UTF-8, and a real `Encoding.UTF8` would prepend a BOM a stdio JSON-RPC reader doesn't
expect) -- wraps the result in `ToolProcess`, a thin `IDisposable` exposing
`StandardInput`/`StandardOutput`/`HasExited` (`Dispose` closes stdin first, so a well-behaved tool
sees EOF and exits its own read loop gracefully, the same shutdown path a real client disconnect
takes, before waiting briefly and disposing the underlying `Process`). Also exposes
`DisownAfterHandoff` (see [issue #32](https://github.com/croicu/desk-tools/issues/32)) -- for use
once `HandleDuplicator` has already handed both pipe ends off to another process: releases
Service's own `Process` wrapper without blocking on `WaitForExit`, since the tool is meant to keep
running for as long as its new owner needs it, unlike `Dispose`'s own graceful-and-prompt shutdown
expectation. Deliberately has no notion of the MCP tool registry itself, kept generic so a future
non-registry-sourced process could reuse it. Wraps a `Process.Start` `Win32Exception` (the
executable can't be found/launched at all) and a plain null return alike into `AppError`, matching
this repo's usual error-surfacing convention rather than letting a raw BCL exception escape.

`src/Service/McpToolLauncher.cs`: the registry-aware layer on top of `ToolLauncher` -- given a tool's
registry name, resolves `mcp-registry/<name>.json` from `AppContext.BaseDirectory` (Service's own
directory; overridable via an optional `registryBaseDirectory` testing seam, defaulting to
production behavior, the same pattern as `Host`'s own `port` constructor parameter), parses the full
`.mcp.json`-shaped fragment (`command`/`args`/`env`), and calls `ToolLauncher.Launch` with the
registry's own directory as the working directory (matching `args`' paths, which are relative to
it, not to whichever process happens to be calling this). `AppError` on a missing registry file,
malformed JSON, or a fragment missing/misshaping any of `command`/`args`/`env`.

`src/Service/Platform/{Windows,Linux,Neutral}/HandleDuplicator.cs` (issue #32): hands a launched
`ToolProcess`'s stdin/stdout pipe handles directly to another process via Win32 `DuplicateHandle`,
instead of `Host` staying in the loop as a byte relay for the tool's whole lifetime -- `Duplicate(tool,
targetProcessId)` opens the target process (`OpenProcess(PROCESS_DUP_HANDLE, ...)`), duplicates both
handles into it, closes Service's own copy of the write end (the same lesson a throwaway spike hit
before this was built: the tool's stdin pipe won't see EOF, and so never exits, until every
write-end handle is closed, not just the target's own eventual copy), and returns both duplicated
values as `long`s (a Win32 `HANDLE` is a pointer -- 8 bytes on x64 -- so `int` would silently
truncate). Lives behind the same `Platform/` split `SingletonGuard` already uses, since
`DuplicateHandle`/`OpenProcess` have no cross-platform equivalent (unlike named `Mutex`) -- the
Linux/Neutral variants throw a clear `AppError` for now rather than attempt a different mechanism
(e.g. `SCM_RIGHTS` over a Unix domain socket on Linux).

`Host`'s own `mcp` JSON-RPC method (`Host.HandleMcp`, see docs/PROTOCOL.md's Echo protocol section)
is what actually ties `McpToolLauncher` and `HandleDuplicator` together for a real caller: validates
`params` (`name`/`processId`, `-32602` on anything missing/malformed), calls `McpToolLauncher.Launch`,
then `HandleDuplicator.Duplicate` with the caller-supplied `processId`, and on success calls
`tool.DisownAfterHandoff()` (ownership transferred, so `Host` must not block waiting for the tool to
exit) and returns the two duplicated handle values as decimal strings. Any failure at any step --
before or after the tool was actually launched -- is caught by the same outer catch-all
`HandleLine` already had for every other method, surfacing as `-32603`; if the launch itself
succeeded but duplication failed, the orphaned `ToolProcess` (nobody has handles to it) is torn down
via its normal `Dispose()` rather than leaked. `Host`'s constructor also grew an optional
`mcpRegistryBaseDirectory` testing seam (same pattern as its existing `port` parameter and
`McpToolLauncher.Launch`'s own `registryBaseDirectory`), so a test can point `mcp` at a throwaway
registry fixture instead of the real, build-generated one. Trusts the caller-supplied `processId`
as-is -- no verification against the real TCP connection's owning process (a known, deliberate
simplification; the loopback listener is local-machine-only exposure either way).

Verified two ways: `tests/Service/Unit/Platform/Windows/HandleDuplicatorTests.cs` exercises
`HandleDuplicator` directly (duplicating into the test's own process, since the real
cross-privilege-boundary behavior -- elevated `Service` into unelevated `Desk` -- was already
validated manually via a throwaway spike before this was built); and
`tests/Service/Integration/HelloTests.cs`'s own `Mcp_RealHelloEntry_...` test drives the entire real
path end-to-end -- a real `Host` handling a real `mcp` JSON-RPC request over its loopback listener,
launching the real, build-generated `hello` registry entry, and a real `initialize`
request/response exchanged entirely through the duplicated handles, not `Host`'s own pipes. That
test is Windows-only, guarded at runtime (checking whether the resolved output directory's own name
is "win-x64" -- `Service.csproj`'s own `Platform/` selection, not the literal host OS, decides which
`HandleDuplicator` variant gets linked in) rather than a compile-time `Platform/` split, since its
only other precondition (the real Hello build) has nothing to do with the RID either. Passes on a
plain `dotnet test` now, since win-x64 is `tests/Directory.Build.props`' own default -- the guard
only actually fires if someone deliberately overrides to a different RID.

## Data flow

<!-- How data enters, gets transformed, and leaves the system. -->

`Program.cs` loads `Settings` (see `docs/PROTOCOL.md`'s settings.json discovery order) before
constructing `Host`, so every knob `Host` reads (`IdleTimeout`, `Port`) is already resolved by the
time it starts. At runtime, `Host` reacts to a TCP connection being accepted: it's both the interim
activity signal and the trigger to read one JSON-RPC request line and dispatch it before closing.
`src/Desk` is the other end of that exchange -- it resolves the same `Port` from its own
`Settings.Load()` call (same shared settings.json, no direct dependency between the two processes
beyond that shared file), then connects, sends a request, and reads back `Host`'s JSON-RPC
response.

## Contracts

<!-- Interfaces.cs: public contracts -- persisted/shared data (plain classes/records, no behavior)
     plus behavioral interfaces meant for a consumer to implement/inject (e.g. ILoggingSink,
     already scaffolded there). Contracts.cs: behavioral interfaces that wire this project's own
     internals together -- never referenced by external consumers, unlike Interfaces.cs's. -->
