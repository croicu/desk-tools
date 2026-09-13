# WinGUI Console Proof-of-Concept

## Status: Implementation

## Problem statement

Give `src/Service` a `WinExe` subsystem on Windows (no console, no taskbar entry, ever created
automatically) while still letting it report to whatever console its parent process already has,
if any — e.g. launched directly from an interactive PowerShell/cmd prompt. If there's no parent
console (double-click, a detached launch, a real background/service start), the process just runs
with no console at all and no output is visible — accepted by design, not a bug: this is a
background service, not a CLI tool that must always be able to report to *some* console.

This lands directly inside the real `src/Service` project (not a standalone throwaway PoC), which
already has real CLI-arg parsing, `Settings`, and `Logger`/`ConsoleLogSink` integration (see
`Program.cs`) — so console attach/release has to run before and after everything else in `Run()`
that touches the console, not in an isolated `Main` with no other concerns.

Structured using folder-conditional platform compilation, since this is exactly the kind of
platform-specific code that pattern is meant for: one project, three compiled shapes — a Linux
binary that's a normal console app with a no-op stub, a Windows binary that's a `WinExe`
GUI-subsystem app that best-effort attaches to its parent's console, and a RID-less "Neutral" shape
(plain `dotnet build`/`test`/`run`, and the IDE's own design-time build) that also no-ops, so the
everyday dev loop and IntelliSense never need a RID just to compile.

**Superseded direction, kept for history**: earlier passes of this task had `Platform/Windows` call
`AllocConsole()` — creating a brand-new console window when there was no parent one to attach to.
That was dropped in favor of the simpler attach-only design above: a background service popping up
its own console window on every launch (then having nothing keep it alive, so it would flash open
and closed immediately — a real, previously-documented rough edge) wasn't actually the behavior
wanted. `AllocConsole()` is no longer called anywhere in this codebase.

## Design decisions

### Directory structure

Added/changed inside the existing `src/Service/` project — no new project, no new `.csproj`:

```
src/Service/
  Service.csproj              # existing -- gains the OutputType split + Compile Remove/Include blocks
  Program.cs                  # existing -- Run() calls EnsureConsole()/ReleaseConsole() around its body
  IConsole.cs                 # new, platform-neutral
  Platform/
    Windows/
      ServiceConsole.cs       # new -- AttachConsole/FreeConsole via P/Invoke
    Linux/
      ServiceConsole.cs       # new -- no-op stub, same class/namespace
    Neutral/
      ServiceConsole.cs       # new -- no-op stub for the no-RID case (default builds, IDE IntelliSense)
```

Exactly one of the three `Platform/*/ServiceConsole.cs` files is ever compiled into a given build —
selected by target RID (or its absence), not by any runtime check.

**Resolved (namespace root)**: the repo's namespace root is `Croicu.Desk.Tools`, with `Service`,
`Base`, and `Mocks` as top-level siblings beneath it (mirroring the actual project-reference graph —
`Base` has no dependency on `Service`, so it isn't nested under it; same for `Mocks`, which only
depends on `Base`). `Program.cs` is flat `namespace Croicu.Desk.Tools.Service;`. The new files
follow the same flat namespace — `IConsole.cs` and *all three* `Platform/*/ServiceConsole.cs` files
live in `namespace Croicu.Desk.Tools.Service;`, with no `.Platform`/`.Platform.Windows`/etc. segment
at all: the three `ServiceConsole.cs` files are mutually-exclusive alternatives for the exact same
compiled slot (per the `Compile Remove`/`Compile Include` blocks below), so giving them
platform-distinct namespaces would force conditional-compilation-aware `using` directives at any
call site that needs to pick between them — the same reason they already share one class name.

**Resolved (type names)**: `IPlatformConsole`/`PlatformConsole` were first renamed to
`IConsole`/`Console` to avoid repeating "Platform" in a name that already lives under a `Platform/`
folder — but `Console` shadowed `System.Console` within `Croicu.Desk.Tools.Service` (a
same-namespace type declaration wins over a name brought in by `using System;`, including the
implicit global one from `ImplicitUsings`). Even though that was technically safe here (this
project already confines direct `System.Console.*` calls to `Base/Diagnostics.cs`, so nothing in
`Service`'s own namespace needed the BCL type), it was an unnecessary trap for later — so the
concrete implementations are named **`ServiceConsole`** instead, leaving `IConsole` as the
interface name (no collision risk there; there's no `System.IConsole`).

### `.csproj` resolution

Three mutually-exclusive conditions, all keyed off `$(RuntimeIdentifier)` (never `$(OS)` for an
actual cross-target publish — only the *target* RID determines the output shape; the "unset RID"
case is handled by its own Neutral branch below, not by inspecting the host OS):

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>

  <OutputType Condition="'$(RuntimeIdentifier)' == 'win-x64'">WinExe</OutputType>
  <OutputType Condition="'$(RuntimeIdentifier)' != 'win-x64'">Exe</OutputType>
</PropertyGroup>

<ItemGroup>
  <Compile Remove="Platform/**/*.cs" />
</ItemGroup>

<ItemGroup Condition="'$(RuntimeIdentifier)' == 'win-x64'">
  <Compile Include="Platform/Windows/**/*.cs" />
</ItemGroup>

<ItemGroup Condition="'$(RuntimeIdentifier)' == 'linux-x64'">
  <Compile Include="Platform/Linux/**/*.cs" />
</ItemGroup>

<ItemGroup Condition="'$(RuntimeIdentifier)' != 'win-x64' AND '$(RuntimeIdentifier)' != 'linux-x64'">
  <Compile Include="Platform/Neutral/**/*.cs" />
</ItemGroup>
```

### RID is required only for the platform-specific console behavior, not to build at all

**Superseded finding, kept for history**: an earlier pass of this task had only `Platform/Windows`
and `Platform/Linux`, with no fallback — meaning a plain `dotnet build`/`test`/`run` (no RID) failed
outright (`CS0712: Cannot create an instance of the static class 'Console'`, since `Console` fell
back to resolving `System.Console` once neither platform folder compiled in). That also broke the
IDE's own design-time build (IntelliSense never passes a RID), showing the same error live in the
editor on every open of `Program.cs`. Adding `Platform/Neutral/ServiceConsole.cs` (a third no-op
implementation, compiled in whenever the RID is neither `win-x64` nor `linux-x64` — which includes
unset entirely) fixes both: plain `dotnet build`/`test`/`run` and the IDE's design-time build all
compile and run cleanly again, with `EnsureConsole()`/`ReleaseConsole()` simply doing nothing,
matching how the app behaved before this task started. **A RID is now only needed to opt into a
platform's specific behavior** (the real `WinExe` shape and console attach/release for `win-x64`,
or to explicitly build a `linux-x64` artifact) — not a hard requirement for the solution to build
or test at all.

**Correction found during implementation, still relevant**: `dotnet build -r <rid>` (or
`test`/`publish`) **fails at the solution level** — `NETSDK1134: Building a solution with a specific
RuntimeIdentifier is not supported`. The RID must be passed to an individual project invocation
instead (`dotnet build src/Service -r win-x64`, `dotnet test tests/Service -r linux-x64`, etc.) —
MSBuild then propagates it through the `ProjectReference` to `Base.csproj` automatically, confirmed
working. This only matters when you deliberately want the `win-x64`/`linux-x64` shape (e.g. to
verify the attach/release behavior, or to publish a real artifact) — the default solution-wide
`dotnet build`/`dotnet test` (no RID) needs no such scoping any more, since `Neutral` covers it.

Because of the above, **CI/CD and the docs no longer strictly need updating for this task to be
correct** — `.github/workflows/ci.yaml`'s `lint-and-test` and `.github/workflows/cd.yaml`'s
`release-linux` can keep running solution-wide with no RID, same as today, and will keep passing.
Whether it's still worth *additionally* exercising the real `win-x64`/`linux-x64` shapes in CI (to
catch a regression in the platform-specific paths themselves, e.g. a P/Invoke signature change) is
a separate, lower-priority enhancement — see Open questions.

### Producing each shape

```bash
# Windows GUI-subsystem binary, attaches to a parent console if one exists
dotnet publish src/Service -r win-x64 --self-contained

# Linux console binary, stub does nothing (terminal's own console is already there)
dotnet publish src/Service -r linux-x64 --self-contained

# Neutral (no RID): today's default dev loop, also a no-op
dotnet build src/Service
dotnet test tests/Service
```

- **Linux resolution**: `RuntimeIdentifier=linux-x64` → `OutputType=Exe` (no-op for the binary
  itself, but keeps the property semantically correct) → `Platform/Linux/**/*.cs` compiled in →
  `ServiceConsole.EnsureConsole()`/`ReleaseConsole()` do nothing → the process behaves exactly like
  any normal console app run from a terminal.
- **Windows resolution**: `RuntimeIdentifier=win-x64` → `OutputType=WinExe` → no console at process
  start, no taskbar entry → `Platform/Windows/**/*.cs` compiled in →
  `ServiceConsole.EnsureConsole()` calls `AttachConsole(ATTACH_PARENT_PROCESS)` (via classic
  `[DllImport]`, not `[LibraryImport]` — the source-generated marshaling for a `bool`-returning
  P/Invoke signature needs `AllowUnsafeBlocks` project-wide, not worth enabling unsafe code for
  this) — if the immediate parent process has a console, this attaches to it and `Logger` output
  flows there; if not (no parent console at all — double-click, a detached launch, a real service
  start), the call fails and the process simply runs with **no console at all**, silently, by
  design.
- **Neutral resolution**: `RuntimeIdentifier` unset (or anything other than `win-x64`/`linux-x64`)
  → `OutputType=Exe` → `Platform/Neutral/**/*.cs` compiled in → `ServiceConsole.EnsureConsole()`/
  `ReleaseConsole()` do nothing → identical behavior to today's app before this task, for both the
  default dev loop and the IDE's design-time build.

- **Shared interface**: `IConsole` — `void EnsureConsole();` and `void ReleaseConsole();`. All
  three platform implementations use the same class name and namespace
  (`Croicu.Desk.Tools.Service.ServiceConsole`) so the call site has no `#if` or
  `OperatingSystem.IsWindows()` branch.
- **Call site**: `Run()` in `Program.cs` constructs one `ServiceConsole` instance, calls
  `EnsureConsole()` as its **first statement** (before `ParseArgs`, which can emit `--help` text via
  `Logger.Print`, and before `Settings.Load`'s error path, which logs via `Logger.Error` — both
  write straight to `Console` through `ConsoleLogSink`, and if there's no console at all, nothing
  written before `EnsureConsole()` runs would ever be visible), then wraps the rest of the method
  body in `try`/`finally` so `ReleaseConsole()` runs on every return path, including the `debug`
  rethrow. `ParseArgs`'s `--help`/unrecognized-argument paths call `Environment.Exit()` directly,
  which bypasses the `finally` entirely — that's fine, since abrupt process termination already
  triggers Windows' own console-detach cleanup regardless.
- **`ReleaseConsole()` only frees a console we actually attached to**: `Windows/ServiceConsole.cs`
  tracks whether `AttachConsole` succeeded in an instance field (`_attached`), and only calls
  `FreeConsole()` when it did. Freeing the *parent's* shared console promptly lets that parent
  shell's prompt return/repaint correctly rather than looking stuck. There's no "we allocated our
  own console" case to reason about any more, now that `AllocConsole()` is gone — `_attached ==
  false` just means there was never a console to free in the first place.
- **Console inheritance caveat (Windows)**: `AttachConsole`'s whole reason to exist is that simple
  process-launch handle inheritance doesn't always happen — e.g. `Start-Process` in PowerShell
  deliberately launches into a new process group without inheriting console handles, even though
  the parent PowerShell session does have a console. `AttachConsole(ATTACH_PARENT_PROCESS)` lets
  `Service.exe` opportunistically reconnect to that console anyway rather than requiring handle
  inheritance to have already happened automatically.

## Open questions

- Now optional, not blocking: whether to also exercise the real `win-x64`/`linux-x64` shapes in CI
  (separate from the solution-wide no-RID job, which already passes) — and if so, whether
  `release-linux`'s `dotnet publish` should gain `-r linux-x64 --self-contained false` explicitly
  (today's build is portable/framework-dependent with no RID at all; adding a bare `-r` without
  pinning `--self-contained` would silently flip it to self-contained).
- Whether `README.md`/`CLAUDE.md` should document the `-r win-x64`/`-r linux-x64` commands at all,
  now that they're optional rather than required for a working dev loop.

## Implementation plan

1. ~~Decide the open questions above~~ — still open, now explicitly non-blocking (see above).
2. **Done** — `src/Service/IConsole.cs` (platform-neutral) — `EnsureConsole()`/`ReleaseConsole()`.
3. **Done** — `src/Service/Platform/Windows/ServiceConsole.cs` — `[DllImport]` declarations for
   `AttachConsole`/`FreeConsole`; `EnsureConsole()` best-effort attaches to the parent's console
   (no `AllocConsole` fallback), `ReleaseConsole()` frees it only if attach succeeded.
4. **Done** — `src/Service/Platform/Linux/ServiceConsole.cs` — both methods empty (with a comment
   explaining why).
5. **Done** — `src/Service/Platform/Neutral/ServiceConsole.cs` — same no-op shape as Linux's, for
   the RID-unset case (default dev loop, IDE design-time build).
6. **Done** — `Service.csproj` — added the `OutputType` conditions and the three-way `Compile
   Remove`/`Compile Include` blocks above.
7. **Done** — `Program.cs`'s `Run()` — constructs one `ServiceConsole`, calls `EnsureConsole()` as
   the first statement, wraps the rest of the method in `try`/`finally` calling
   `ReleaseConsole()`.
8. Not done, now optional — update CI/CD to additionally exercise `win-x64`/`linux-x64` (see Open
   questions); not required for correctness since the solution-wide no-RID job already passes.
9. Not done, now optional — document `-r win-x64`/`-r linux-x64` commands in README.md/CLAUDE.md
   (see Open questions).
10. Verify:
    - Plain `dotnet build`/`dotnet test` (no RID, matching today's default commands and the IDE's
      design-time build) pass clean.
    - `dotnet build src/Service -r win-x64 --self-contained` and `-r linux-x64 --self-contained`
      both succeed cleanly.
    - `dotnet test tests/Service -r linux-x64` passes, exercising the Linux no-op stub through
      `Program.Run()` end to end.
    - Launched interactively from an already-open PowerShell/cmd prompt, a `win-x64` build attaches
      to that console and prints normally. Launched with no console ancestor at all (double-click,
      or `Start-Process .\Service.exe` from PowerShell, which deliberately doesn't inherit console
      handles), it runs silently with no console and no visible output — expected, not a bug.

## Test results

Steps 2–7 implemented and verified (2026-09-13):

- Plain `dotnet build`/`dotnet test` (no RID) pass clean end to end via `Platform/Neutral` — fixed
  both the `CS0712` compile failure and the equivalent live IDE error on `Program.cs` (the
  design-time build also has no RID, hits the same fallback).
- `dotnet build src/Service -r win-x64 --self-contained` and `-r linux-x64 --self-contained` both
  succeed cleanly; `dotnet build -r win-x64` **at the solution level** confirmed failing with
  `NETSDK1134` (a correction to the original plan, not a regression).
- `dotnet test tests/Service -r linux-x64` passes (1/1, `Main_RunsClean`).
- `dotnet format --verify-no-changes` and `dotnet test tests/Base` (36/36, unaffected/no RID
  needed) both pass clean.
- `AttachConsole`/`FreeConsole` confirmed against a real interactive PowerShell session: attaching
  to the parent console and reporting `Logger` output there works as designed.
- Earlier design (since superseded): `AllocConsole` + a raw PE-header read confirmed
  `Subsystem: 2` (GUI/`WinExe`) on the published `Service.exe`, and a genuinely detached
  `Start-Process` launch exited 0. `AllocConsole` no longer exists in this codebase, but the
  underlying `OutputType=WinExe` mechanism this relied on (verified at the binary level, not just
  inferred from the `.csproj` condition) is unchanged and still what makes "no console unless we
  attach to one" possible.
- `dotnet publish src/Service -r linux-x64 --self-contained` → `file` on the published `Service`
  binary confirms a real stripped ELF 64-bit `x86-64` executable.
