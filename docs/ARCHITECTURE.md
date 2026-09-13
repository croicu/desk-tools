# ARCHITECTURE.md

Modules, data flow, and contracts for `__project_name__`.

## Modules

<!-- One entry per file under src/Base/ (reusable scaffold: Logger/Settings/Errors/Interfaces,
     compiled to its own Base.dll) and src/__package_name__/ (the CLI itself, references
     Base.csproj): what it owns, what it depends on. -->

Base.dll is designed to be safe inside a service hosting multiple heterogeneous clients in one
process, each "renting" its own `ExecutionContext` with independent settings/logging. See
CLAUDE.md's Architecture convention 10 -- all ambient state in `src/Base` (the active sink stack,
its pending buffer, `ConsoleLogSink`'s instance guard, `Settings.Current`) is scoped with
`AsyncLocal<T>` rather than plain `static` fields, so one client's context can't see or disturb
another's.

## Data flow

<!-- How data enters, gets transformed, and leaves the system. -->

## Contracts

<!-- Interfaces.cs: public contracts -- persisted/shared data (plain classes/records, no behavior)
     plus behavioral interfaces meant for a consumer to implement/inject (e.g. ILoggingSink,
     already scaffolded there). Contracts.cs: behavioral interfaces that wire this project's own
     internals together -- never referenced by external consumers, unlike Interfaces.cs's. -->
