# ARCHITECTURE.md

Modules, data flow, and contracts for `desk-tools`.

## Invariants for MCP tools

These hold for every MCP tool in this repo, and for Service itself. They are not specific to any one
feature (secrets was just the first place they had to be spelled out), so new tools and new Service
capabilities are expected to keep to them.

1. **Service is a convenience, never a gate.** Every MCP tool must be launchable and useful without
   Service -- straight from `.mcp.json` (e.g. `"command": "pager"` for a standalone tool, or
   `dotnet Hello.dll`). Service and its registry add capabilities (running elevated, delivering
   secrets, ...); they are never a prerequisite. A tool therefore ships as a self-contained
   executable that does not reference Service, Desk, or `Secrets.dll`.
2. **The manifest is optional, and so is every part of it.** The registry fragment
   (`mcp-registry/<name>.json`) is what Service reads to launch a tool on someone's behalf; a tool runs
   without one. Inside a fragment only what Service needs to launch it (`command`) is mandatory. An
   optional part (the `secrets` array today) that is malformed, or that cannot be honored, costs only
   that capability: it is skipped with a warning that names the problem, and the tool still starts.
3. **A missing capability degrades the tool; it never blocks it.** A tool takes what it needs from its
   environment (an environment variable, say) and handles its absence itself -- the affected tool call
   answers "not configured", the tool still starts. Service in turn never fails a launch because an
   optional capability could not be provided (a cancelled password prompt, a wrong master password, a
   missing KeePass entry or key, an unreadable database): it launches the tool without that variable and
   logs a warning.
4. **Developer mistakes fail the build; run-time conditions never block the user.** A declaration with a
   missing field or a character that would corrupt the generated JSON fails the *build* (cheapest place to
   fix it). Anything that can differ from machine to machine or session to session -- the case in
   point 3 -- never does.

## Modules

<!-- One entry per file under src/Base/ (reusable scaffold: Logger/Settings/Context/Errors/
     Interfaces, plus src/Base/Sinks/ for the ILoggingSink implementations, compiled to its own
     Base.dll), src/Service/ (the resident-process CLI, references Base.csproj), src/Hello/ (a
     minimal stdio MCP server, references Base.csproj), src/Desk/ (a console client for
     src/Service's Host, references Base.csproj), src/Secrets/ (KeePass-backed secret retrieval,
     references Base.csproj), and scripts/ (standalone Python utilities outside
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
accepted for `Settings.IdleTimeout`, none is currently in flight, **and no handed-off MCP tool is
still running** ([issue #45](https://github.com/croicu/desk-tools/issues/45)): after `mcp` handoff
`Host` keeps a stdio-free watch `Process` handle per tool (`ToolProcess.OpenWatchHandle`), and the
idle check prunes exited ones; while any is alive `Host` counts itself active (logged once at Info),
so the TTL restarts from when the *last* tool exits. A client can also trigger
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

`src/Base/Base.csproj`'s `ShutdownDevMcpServers` target ([issue #55](https://github.com/croicu/desk-tools/issues/55)):
runs once per build (every other `src/` project references Base, so it builds first), `BeforeBuild`,
and shuts the repo's own running MCP servers down so they can't lock `out\`: when a `Service.exe`
process is running it runs the installed `Desk.exe shutdown` -- Service stops, its tools exit (the
parent watchdog, #45), and their `desk mcp` proxies exit (#54), so one command releases every lock;
MSBuild's own copy retry covers the brief lag. Inert unless there is something to stop: Windows only,
not on CI (`CI=true`), not with `DESK_TOOLS_SKIP_SHUTDOWN=1`, only when the machine-wide
`Desk Tools\Desk.exe` exists, and only when a `Service.exe` is running (so a build with nothing to
stop doesn't make Desk fail on connect). Never fails the build. The cost is that the MCP servers show
as disconnected afterward (reconnect with `/mcp`).

`src/Directory.Build.targets`'s `ComputeMcpSecretsFragment` target ([issue #64](https://github.com/croicu/desk-tools/issues/64)):
renders a tool's `<McpSecret Include="ENV_VAR" Entry="..." Key="..." />` items (the environment variable, the
KeePass entry, the key in its Notes) into the registry fragment's optional `"secrets"` array, spliced in by both
generate targets; a tool with no declarations gets a fragment byte-identical to before. Python tools declare the
same item in `scripts/Scripts.csproj` with an extra `Tool="<name>"` metadata (`ValidateMcpPythonSecrets` also
checks that name exists, so a typo cannot silently declare nothing; the per-tool filtering captures the batch's
tool name in a property first -- an item-function argument does not expand `%(McpPythonTool.Identity)`).
Invalid declarations (a missing field, a double quote or backslash, since the fragment is written as raw JSON
text) fail the build. `McpToolLauncher.ParseFragment` reads the array back into typed declarations and, per the
invariants above, **never throws and never blocks the launch** over it: a malformed declaration (bad variable
name, a repeat, one the fragment's own `env` already sets, `DESK_PARENT_PID`) is skipped with a warning and the
tool starts without it. Parsing only for now -- nothing resolves a value yet (the next slice).

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
tools live in `scripts/` (edited/checked in there) rather than each getting a `src/`-style per-tool project.
Explicitly `<Import>`s `src/Directory.Build.props` (`scripts/` isn't a descendant of `src/` or
`tests/`, so MSBuild's own auto-import wouldn't find it) specifically to reuse its
`BaseOutputPath`/`RuntimeIdentifier` default rather than duplicating them -- the whole point being
that a Python tool's build-time destination is that *identical* shared `out/<Configuration>/
net10.0/[<RID>/]` folder `Service.csproj`/`Hello.csproj`/`Desk.csproj` already build into, just one
level down: the `.py` files themselves land in a `scripts/` subfolder of it (mirroring the repo's
own `scripts/` layout), while `mcp-registry/<name>.json` still lands directly in that shared
folder, right next to `Service.exe`, same as a .NET tool's own registry entry. An
`<McpPythonTool Include="goodbye" />` item (mirroring `<McpToolName>`'s per-project opt-in for .NET
tools, just item-based since this one project covers every Python tool) drives two pairs of
targets, batched via `Inputs`/`Outputs` over the item so each runs once per tool: one pair copies
`scripts/<name>.py` into `$(OutDir)scripts\`/`$(PublishDir)scripts\` (`AfterTargets="Build"`/
`"Publish"`, mirroring `Directory.Build.targets`' own `CopySettingsToOutput`/`ToPublish` split), the
other writes that tool's own `mcp-registry/<name>.json` fragment directly in `$(OutDir)`/
`$(PublishDir)` (`"command": "python"` instead of `"dotnet"`, `"args": ["scripts/<name>.py"]`
instead of a bare `$(TargetFileName)`, otherwise identical shape to `GenerateMcpRegistryEntry`'s own
output below). Because the registry stays co-located with `Service.exe` exactly like a .NET tool's
own `.dll` already is, `McpToolLauncher`/`ToolLauncher` need zero Python-specific code -- "args
resolved relative to the registry's own directory" already covers a `scripts/<name>.py`-shaped
relative path exactly like it covers a bare one, no new path-resolution logic needed anywhere.
`link-program-files.py`'s own target-directory computation accounts for this same one-level offset
(walks up two directories from `__file__`, not one, to land back on `$(OutDir)` itself rather than
its own `scripts/` subfolder -- see that script's own remarks). `scripts/goodbye.py`
itself mirrors `src/Hello`'s hand-rolled JSON-RPC shape (no SDK, same protocol
version/error codes) but fully self-contained -- one tool, `say_goodbye`, returning `"Bye from
MCP"`. Verified live end-to-end (`desk mcp goodbye`, a real elevated `Service` launching the
real Python process) and by `tests/Service/Integration/GoodbyeTests.cs`.

`src/Base/DevRedirect.cs`: lets an *installed* `Service.exe`/`Desk.exe` under Program Files hand
off to a local dev build without the installed copy's own folder ever being touched -- the earlier
approach `scripts/link-program-files.py` originally took (rename `C:\Program Files\Desk Tools` to
`Desk Tools.bak`, junction the whole folder) fought Windows the entire way: a running process's own
current directory (`src/Service/ToolLauncher.cs` sets every launched tool's own cwd to the
registry's own directory, which for this specific tool *was* `C:\Program Files\Desk Tools` itself)
implicitly locks that same directory against rename (`WinError 32`, reproduced and confirmed
independent of elevation -- a single process with its own cwd set to a directory cannot rename that
directory, full permissions or not), and even past that fix, the real elevated Service still hit an
unexplained `WinError 5` renaming its own live install folder. `DevRedirect.TryHandoff(exeName,
args)` -- called as the very first thing each app's own `Main` does, before any settings/logging
setup -- checks for a `.dev` subfolder junction next to the running exe (created by
`link-program-files.py`'s current, much smaller job: just that one subfolder junction, never a
rename of anything) and, if present, re-spawns the identically-named exe from inside it (inheriting
this process's own stdio untouched -- critical for `desk mcp <name>`'s stdio proxying), waits for
it, and returns its exit code for the caller to relay immediately. Deliberately does not set the
respawned process's own `WorkingDirectory` to `.dev` itself, for the same cwd-lock reason above --
that would just relocate the same self-referential lock onto `unlink-program-files.py`'s own
`rmdir` of `.dev` (see that script's own remarks, which still needs its own `chdir` guard for
exactly this).

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

`ToolLauncher.Launch` also sets `DESK_PARENT_PID` (Service's own PID; set after the registry's `env`
so a fragment can't override it) in every child's environment. `src/Base/ParentWatchdog.cs`
(`ParentWatchdog.Start`, called early by Hello; the Python tools carry a small inline copy, since
`scripts/` are intentionally self-contained) waits on that PID from a background thread and exits
the tool once it's gone -- the reaping path for a forced `shutdown` or a Service crash, where
`Host`'s own wait-for-children can't help ([issue #45](https://github.com/croicu/desk-tools/issues/45)).
A tool launched by anything other than Service has no variable set and gets no watchdog.

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

`src/Base/UserSettings.cs` ([issue #58](https://github.com/croicu/desk-tools/issues/58)): the per-user
settings file, `%LOCALAPPDATA%\Desk Tools\settings.json` (`Environment.SpecialFolder.LocalApplicationData`,
so also `~/.local/share` on Linux; `DefaultPath` is null, not a relative fallback, on a platform with no such
folder). `Settings.Section()` merges it key-by-key between the working-directory `settings.json` and
`settings.local.json`; `UserSettings.Set(section, key, value)` is the write side (read-modify-write that keeps
every other section/key, temp file then move, refuses the core section and never overwrites a file that is not
valid JSON). The trust rule that shapes all of it: the file is writable by any process running as the user but
read by the elevated Service, so it is honored only for non-core sections -- never `logDir`/`port`/`debug`/
`idleTimeout`/`logLevel` -- and a malformed file is ignored with a warning, not thrown. Cross-platform
(Architecture convention 11); no static mutable state (convention 10). Replaces the separate `secrets.json` the
Secrets design first assumed: the file picker will record `secrets.databasePath` here.

`src/Secrets/` (`Secrets.dll`, namespace `Croicu.Desk.Tools.Secrets`, [issue #50](https://github.com/croicu/desk-tools/issues/50)):
reads credentials out of the user's KeePassXC database so an elevated MCP tool can use a secret
without any value reaching a medium-integrity process. References `Base.csproj` only; types are
`internal`. **Slices 1-3 only (retrieval core, the master-password prompt, and finding the database)** -- nothing in
`src/Service` uses it yet, and `Service.csproj` does not reference it until the wiring slice (the
design, including `<McpSecret>` declarations and `ToolLauncher` delivery via environment variables, is
in the issue).

- `Vault.Read(entry, keys)`: the broker. Asks an injected `IPasswordSource` for the master password
  (up to three attempts, telling the source when one is rejected), has `KeePassCli` read the entry's
  Notes once (one key derivation however many keys), and extracts each `key: value` line. Nothing is
  cached, so rotation needs no invalidation. Errors name the entry and key, never a value.
- `KeePassCli`: runs `keepassxc-cli show -q -a Notes <db> <entry>` through an injected
  `IProcessRunner`, password as one line on stdin (never the command line). Under `-q` a wrong
  password exits 1 with **empty** stderr while a missing entry exits 1 with a message, so exit 1 +
  empty stderr is classified as an authentication failure (re-prompt) and any other failure as a
  plain one -- not by matching message text, which may be localized.
- `Platform/{Windows,Linux,Neutral}/CliLocation.cs` (same per-RID selection as
  `src/Service/Platform/`; flat namespace): the CLI is only ever run from the machine-wide Program
  Files install, never from PATH, an environment variable, or user-writable configuration, and the
  resolved path must stay under that directory. Windows is the only real implementation; the Linux
  and Neutral variants are placeholders that throw `AppError` (guessing a path would be the very
  mistake this class prevents), tracked as a follow-up. Everything else in the assembly is portable
  -- the Platform split also gives the later password prompt and file picker (P/Invoke) a home.
- `NotesParser`: finds the single `key: value` line in the Notes bytes (`
` or `
`, exact
  case-sensitive key); a missing, duplicated, or empty key is an error.
- `PromptedPassword` (`IPasswordSource`, issue #53): asks an `IPasswordPrompt` for the master
  password and **holds** it (pinned bytes) until Service exits (`Dispose`) -- no TTL of its own.
  Callers get copies; `Rejected()` drops it so the next attempt prompts again (a held password that
  stopped working re-prompts rather than failing); a cancel/timeout clears it and returns null; it is
  only reused for the same database; one dialog at a time (a concurrent second caller waits, then
  reuses the answer).
- `Platform/{Windows,Linux,Neutral}/PasswordPrompt.cs` (`IPasswordPrompt`): Windows shows a modal,
  topmost dialog through P/Invoke `DialogBoxIndirectParam` over an in-memory template
  (`Platform/Windows/DialogTemplate.cs`, unit-tested by walking the byte layout) -- no WinForms/WPF.
  It shows the database path and "attempt N of 3", masks the edit box (`ES_PASSWORD`), closes itself
  after a timeout (default 2 minutes, a `WM_TIMER`), and runs on its own STA thread. The password goes
  into pinned `char[]`/`byte[]` buffers (never a `string`), the char buffer is zeroed and the edit box
  cleared before it closes. Because Service is elevated, so is the window: a medium-integrity process
  can't script it or read the box (UIPI). Not DPI-aware, English only. Linux/Neutral throw `AppError`
  (#51). The Windows tests drive the real dialog by sending it messages, tagged `Integration` and
  not parallelized.
- `DatabaseLocation` ([issue #61](https://github.com/croicu/desk-tools/issues/61)): finds the KeePass
  database. The saved `secrets.databasePath` (via `DatabasePathStore`: read through `Settings.Section`, written
  with `UserSettings.Set`, i.e. the per-user file) is used as-is and the file picker is **not** invoked; only a
  missing/empty value opens the picker, validates the answer, and saves it. A saved value that is present but
  unusable is an error to fix (with a detailed message saying where the setting lives), not a reason to open a
  dialog on an elevated background process; cancelling the picker is likewise a detailed error and saves nothing.
- `DatabasePathRule`: a database path must be absolute, an existing **local** `.kdbx` file. UNC paths and
  network drives are refused outright -- for the saved value too, not only the picked one: the per-user file is
  writable by any process running as the user, and the elevated Service opening a UNC path would hand that
  server its NTLM credentials. UNC is rejected on the text alone, before anything touches the network.
- `Platform/{Windows,Linux,Neutral}/FilePicker.cs` (`IFilePicker`): Windows is P/Invoke `GetOpenFileNameW` (no
  COM, no WinForms) on its own STA thread, owned by a hidden topmost window so a background elevated process
  still gets a visible dialog; `*.kdbx` filter, `OFN_NOCHANGEDIR` (don't pin the directory the user browsed
  through) and `OFN_DONTADDTORECENT`. Linux/Neutral throw `AppError` (#51). The Windows tests drive the real
  dialog (tagged `Integration`, not parallelized): they type into its file-name box with posted `WM_CHAR`
  messages, because setting that auto-completing box's text from another thread crashed the process natively.
- `IProcessRunner`/`ProcessResult`/`ProcessRunner` live in **`src/Base`** (`Interfaces.cs` /
  `ProcessRunner.cs`), not here -- nothing about running a program is KeePass-specific, and
  `src/Secrets` consumes them as an ordinary reference. `ProcessRunner` reads stdout as raw bytes
  into pinned buffers zeroed as they are outgrown, never through a `StreamReader`, so a caller can
  zero what it was handed. Secrets are byte arrays the caller zeroes -- a value only becomes a
  `string` where it must, in the launched tool's environment block.
- `tests/Apps/Secrets` (`Secrets.App`, not shipped): a manual command-line driver. `Secrets.App
  [--picker] [--user-settings <file>] [databasePath] [entry] [key]` (all optional; defaults to the dummy
  database, `ntfy`, `desk-pager`) shows the real password dialog, reads the secret through the whole stack, and
  prints it -- on purpose, so it is for the dummy database. `--picker` takes the database from the saved
  `secrets.databasePath` setting or the file dialog instead of `databasePath`, and `--user-settings` redirects
  that saved setting to a scratch file so trying it doesn't touch your real per-user file. Meant to be set as the
  startup project in Visual Studio to step through the dialogs, retries, CLI call and parsing; it reaches the
  `internal` Secrets types through an `InternalsVisibleTo`. Windows only (exits 2 elsewhere).
- Tests: `tests/Secrets/Unit/` (offline, fakes) and `tests/Secrets/Integration/`
  (`[TestCategory("Integration")]`, real `keepassxc-cli` against the dummy
  `tests/Secrets/data/Secrets.kdbx`, master password `password`; Inconclusive when KeePassXC isn't
  installed machine-wide). No database is shipped: a known-password file in Program Files would be
  readable by exactly the processes the design keeps out.

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
