# ARCHITECTURE.md

Modules, data flow, and contracts for `desk-tools`.

## Modules

<!-- One entry per file under src/Base/ (reusable scaffold: Logger/Settings/Context/Errors/
     Interfaces, plus src/Base/Sinks/ for the ILoggingSink implementations, compiled to its own
     Base.dll), src/Service/ (the resident-process CLI, references Base.csproj), and src/Hello/ (a
     minimal stdio MCP server, references Base.csproj): what it owns, what it depends on. -->

Base.dll is designed to be safe inside a service hosting multiple heterogeneous clients in one
process, each "renting" its own `ExecutionContext` with independent settings/logging. See
CLAUDE.md's Architecture convention 10 -- all ambient state in `src/Base` (the registered-sinks
list, `ConsoleLog`'s/`DebugLog`'s instance guards, `Settings.Current`, `Context.Current`) is scoped
with `AsyncLocal<T>` rather than plain `static` fields, so one client's context can't see or disturb
another's.

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

`Context` (`src/Base/Context.cs`) is where a host's `Settings` gets fully wired into `Logger`, in
one call: applies `Settings.LogLevel`/`LogCategories`/`ExcludedCategories` to the console sink
(`Logger.ConfigureConsole`), and resolves `Settings.Debug` with a host's own CLI override (e.g.
Service's `--debug`) into one `Debug` value, installing a `DebugLog` alongside the console sink
when that resolves true -- so a host's `Program.cs` just calls
`Context.Create(settings, debugOverride: ...)` once and never has to remember either wiring step
itself. Deliberately not `Settings.Load()`'s job: that stays a pure, side-effect-free settings.json
parse (safe to call repeatedly, e.g. from tests), while installing a sink is host-level policy.
Takes a plain `bool` override rather than a host's own CLI-arguments type, since Architecture
convention 9 keeps `src/Base` from ever referencing a consuming app's namespace -- this keeps
`Context` reusable by any host, not coupled to one app's flag-parsing shape. Exposed via
`Context.Current`, `AsyncLocal`-scoped like `Settings.Current`, for the same multi-client reason as
the rest of `src/Base`'s ambient state.

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
parses CLI args and calls `Context.Start(console, "desk-tools", settingsPath, debug, Run)`; `Run()`
is the actual run body passed as that delegate, reading `Settings.Current`/`Context.Current` for
whatever it needs since both are already resolved by the time `Context.Start` invokes it.

`src/Service/Host.cs` (chunk 0 of the resident-process skeleton -- see
[issue #5](https://github.com/croicu/desk-tools/issues/5)): the resident process's bare hosting
mechanics, ahead of real JSON-RPC framing/dispatch. Owns a `TcpListener` on a placeholder port
(`Host.DefaultPort` -- not yet the real shared `say_hello` port), an accept loop on a dedicated
background thread that dispatches each connection to the thread pool via a named handler (accept,
then close immediately -- no payload handling yet), and a `System.Threading.Timer`-driven idle check
that exits the process once no connection has been accepted for `Settings.IdleTimeout` and
none is currently in flight. `Program.cs` constructs one and calls `Run()` after settings load.
Console attach/detach on Windows (`ServiceConsole` under `src/Service/Platform/`, implementing
`Base`'s `IConsole`) is unrelated prior work -- see the `wingui-console-poc` history.

`src/Hello/Program.cs`: a minimal, hand-rolled (no MCP SDK) MCP server over stdio -- see
`docs/PROTOCOL.md` for the exact methods/shapes it implements. Reads newline-delimited JSON-RPC
requests from stdin in a loop until EOF, dispatches `initialize`/`tools/list`/`tools/call`, and
writes at most one response line per request via `Logger.Print` (never a leveled `Logger.Info`/etc.
call, since the stdio transport requires stdout to carry only valid MCP messages). Exposes one tool,
`say_hello`, that returns the text "Hi from MCP". No Settings/persistent state -- everything it needs is a
handful of `const`s and one static tool definition.

## Data flow

<!-- How data enters, gets transformed, and leaves the system. -->

`Program.cs` loads `Settings` (see `docs/PROTOCOL.md`'s settings.json discovery order) before
constructing `Host`, so every knob `Host` reads (`IdleTimeout`, and eventually the real listen
port) is already resolved by the time it starts. At runtime, `Host` only reacts to two inputs: a
TCP connection being accepted (the interim activity signal) and the idle-check timer's own clock --
no data flows out of it yet beyond log lines, since there's no request/response payload until
framing/dispatch lands.

## Contracts

<!-- Interfaces.cs: public contracts -- persisted/shared data (plain classes/records, no behavior)
     plus behavioral interfaces meant for a consumer to implement/inject (e.g. ILoggingSink,
     already scaffolded there). Contracts.cs: behavioral interfaces that wire this project's own
     internals together -- never referenced by external consumers, unlike Interfaces.cs's. -->
