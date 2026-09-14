# ARCHITECTURE.md

Modules, data flow, and contracts for `desk-tools`.

## Modules

<!-- One entry per file under src/Base/ (reusable scaffold: Logger/Settings/Errors/Interfaces,
     compiled to its own Base.dll), src/Service/ (the resident-process CLI, references
     Base.csproj), and src/Hello/ (a minimal stdio MCP server, references Base.csproj): what it
     owns, what it depends on. -->

Base.dll is designed to be safe inside a service hosting multiple heterogeneous clients in one
process, each "renting" its own `ExecutionContext` with independent settings/logging. See
CLAUDE.md's Architecture convention 10 -- all ambient state in `src/Base` (the active sink stack,
its pending buffer, `ConsoleLogSink`'s instance guard, `Settings.Current`) is scoped with
`AsyncLocal<T>` rather than plain `static` fields, so one client's context can't see or disturb
another's.

`src/Service/Host.cs` (chunk 0 of the resident-process skeleton -- see
[issue #5](https://github.com/croicu/desk-tools/issues/5)): the resident process's bare hosting
mechanics, ahead of real JSON-RPC framing/dispatch. Owns a `TcpListener` on a placeholder port
(`Host.DefaultPort` -- not yet the real shared `say_hello` port), an accept loop on a dedicated
background thread that dispatches each connection to the thread pool via a named handler (accept,
then close immediately -- no payload handling yet), and a `System.Threading.Timer`-driven idle check
that exits the process once no connection has been accepted for `Settings.IdleTimeout` and
none is currently in flight. `Program.cs` constructs one and calls `Run()` after settings load.
Console attach/detach on Windows (`ServiceConsole` under `src/Service/Platform/`) is unrelated prior
work -- see the `wingui-console-poc` history.

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
