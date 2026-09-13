# WinGUI Console Proof-of-Concept

## Status: Implementation

## Problem statement

Prove that `AllocConsole()` works as expected in a `WinExe`-subsystem process, applied directly
to the real `src/Service` project (not a standalone throwaway PoC project) — before folding this
into the resident-process-skeleton task. A `WinExe` app starts with no console and no taskbar
entry; this task verifies a console can be allocated on demand, written to, and read from.

Unlike the original standalone-PoC framing, this **does** flip `src/Service`'s `OutputType` to
`WinExe` on Windows now, and it lands inside the project that already has real CLI-arg parsing,
`Settings`, and `Logger`/`ConsoleLogSink` integration (see `Program.cs`) — so `EnsureConsole()` has
to run before anything else in `Run()` touches the console, not in an isolated `Main` with no other
concerns.

Structured from the start using folder-conditional platform compilation, since console creation is
exactly the kind of platform-specific code that pattern is meant for: one project, two compiled
shapes — a Linux binary that's a normal console app with a no-op stub, and a Windows binary that's
a `WinExe` GUI-subsystem app that allocates its own console on demand.

## Design decisions

### Directory structure

Added/changed inside the existing `src/Service/` project — no new project, no new `.csproj`:

```
src/Service/
  Service.csproj           # existing -- gains the OutputType split + Compile Remove/Include blocks
  Program.cs                # existing -- Run() calls EnsureConsole() as its first statement
  IPlatformConsole.cs       # new, platform-neutral
  Platform/
    Windows/
      PlatformConsole.cs    # new -- AllocConsole via P/Invoke
    Linux/
      PlatformConsole.cs    # new -- no-op stub, same class/namespace
```

Only one of the two `Platform/*/PlatformConsole.cs` files is ever compiled into a given build —
selected by target RID, not by any runtime check.

**Resolved**: the repo's namespace root is now `Croicu.Desk.Tools`, with `Service`, `Base`, and
`Mocks` as top-level siblings beneath it (mirroring the actual project-reference graph — `Base` has
no dependency on `Service`, so it isn't nested under it; same for `Mocks`, which only depends on
`Base`). `Program.cs` is flat `namespace Croicu.Desk.Tools.Service;`. The new PoC files follow the
same flat namespace — `IPlatformConsole.cs` and *both* `Platform/Windows/PlatformConsole.cs` and
`Platform/Linux/PlatformConsole.cs` live in `namespace Croicu.Desk.Tools.Service;`, with no
`.Platform`/`.Platform.Windows`/`.Platform.Linux` segment at all: the two `PlatformConsole.cs`
files are mutually-exclusive alternatives for the exact same compiled slot (per the `Compile
Remove`/`Compile Include` blocks below), so giving them platform-distinct namespaces would force
conditional-compilation-aware `using` directives at any call site that needs to pick between them —
the same reason they already share one class name.

### `.csproj` resolution

Two independent conditions, both keyed off `$(RuntimeIdentifier)` (never `$(OS)` for an actual
cross-target publish — only the *target* RID determines the output shape):

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework> <!-- matches Service.csproj's actual TFM, not net8.0 -->

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
```

### RID is now required everywhere — no `$(OS)` fallback for unset RID

This is the one place the real integration differs sharply from the original isolated-PoC plan,
and it's load-bearing: **every** `dotnet build`/`test`/`run`/`publish` invocation against this
solution must now pass an explicit `-r win-x64` or `-r linux-x64`, with no fallback. Without a RID,
neither `Compile Include` condition matches, `PlatformConsole` doesn't exist, and `Program.cs`
(which references it unconditionally) fails to compile. This was a non-issue for the original
throwaway PoC (its own plain `dotnet build`/`run` was explicitly "fine for quick local iteration,
not how the PoC gets verified" — nothing downstream depended on it compiling). It's a hard
blocker here, because `src/Service`'s plain `dotnet build`/`test` *is* the real CI/dev-loop path
today. Concretely, these all currently build/test/publish with no RID and must be updated to pass
one:

- `.github/workflows/ci.yaml`'s `lint-and-test` job (runs on `ubuntu-latest`) — add `-r linux-x64`
  to the `Build`/`Test` steps (`dotnet restore` itself doesn't need it).
- `.github/workflows/cd.yaml`'s `release-linux` job — add `-r linux-x64` to the `dotnet publish`
  step. **Open question**: today's publish has no RID at all, i.e. it's a portable,
  framework-dependent build with no OS/arch tied to it. Adding `-r linux-x64` without also pinning
  `--self-contained false` would silently flip it to self-contained (the .NET 6+ SDK default for an
  executable once a RID is present without an explicit `--self-contained` flag), changing the
  release artifact's size and deployment footprint. Decide explicitly — don't let this happen by
  omission.
- `README.md`'s and `CLAUDE.md`'s `## Commands`/`## Run`/`## Test` sections — `dotnet run
  --project src/Service` and the single-test `dotnet test --filter ...` examples need an explicit
  `-r <rid>` added (pick one consistently — probably the platform the instructions are demonstrating
  on — and say so in prose, since a reader can't infer which RID a bare command now implicitly
  requires).
- `installer/Setup.wixproj` already publishes with `-r win-x64` internally — no change needed
  there.

### Producing each shape

```bash
# Windows GUI-subsystem binary, AllocConsole-based console
dotnet publish src/Service -r win-x64 --self-contained

# Linux console binary, stub does nothing (terminal's own console is already there)
dotnet publish src/Service -r linux-x64 --self-contained
```

- **Linux resolution**: `RuntimeIdentifier=linux-x64` → `OutputType=Exe` (no-op for the binary
  itself, but keeps the property semantically correct) → `Platform/Linux/**/*.cs` compiled in →
  `PlatformConsole.EnsureConsole()` does nothing → the process behaves exactly like any normal
  console app run from a terminal.
- **Windows resolution**: `RuntimeIdentifier=win-x64` → `OutputType=WinExe` → no console at process
  start, no taskbar entry → `Platform/Windows/**/*.cs` compiled in →
  `PlatformConsole.EnsureConsole()` calls `AllocConsole()` → a console appears on demand and
  `Console.WriteLine`/`Logger` output works normally from that point on.

- **Shared interface**: `IPlatformConsole` — single method `void EnsureConsole();`. Both platform
  implementations use the same class name and namespace (`Croicu.Desk.Tools.Service.PlatformConsole`)
  so the call site has no `#if` or `OperatingSystem.IsWindows()` branch.
- **Call site**: `Run()` in `Program.cs` must call `new PlatformConsole().EnsureConsole()` as its
  **first statement**, before `ParseArgs` (which can emit `--help` text via `Logger.Print`) and
  before `Settings.Load`'s error path (which logs via `Logger.Error`) — both write straight to
  `Console` through `ConsoleLogSink`, and on a `WinExe` launch with no console ancestor, nothing
  written before `EnsureConsole()` runs would be visible at all.
- **No blocking read/exit change**: the original isolated PoC ended `Main` with `Console.ReadKey()`
  to keep the console open for a human to read. That does **not** carry over here —
  `tests/Service/Unit/ProgramTests.cs` calls `Program.Run(...)` directly and asserts on its return
  value; a blocking `ReadKey()` would hang the test suite. `Run()` keeps returning immediately after
  logging started/completed, same as today.
- **Known accepted rough edge**: until the resident-process-skeleton work actually lands, launching
  the Windows build with no console ancestor (double-click, or a detached `Start-Process`) will
  allocate a console, print the started/completed log lines, and then the process exits —
  i.e. the console window will flash open and closed immediately, since nothing yet keeps the
  process alive to let a human read it. That's expected here, not a bug to fix in this task.
- **Console inheritance caveat (Windows)**: subsystem only governs what happens when a process has
  *no console ancestor at all*. Launched from an already-open terminal with no special
  `CreateProcess` flags, a child process — `WinExe` or not — inherits that terminal's real
  `stdin`/`stdout`/`stderr` and attaches directly, same as any console app; `AllocConsole()` in that
  situation typically fails quietly (already attached) — a no-op in practice, not a bug. See the
  verification step below for how to actually exercise the no-ancestor case.

## Open questions

- `release-linux`'s self-contained-ness once it gains an explicit RID — see the RID section above.
- Exactly which RID the README/CLAUDE.md dev-loop examples should demonstrate (and whether to show
  both).

## Implementation plan

1. Decide the two open questions above (`release-linux` self-contained choice; which RID the docs
   demonstrate).
2. `src/Service/IPlatformConsole.cs` (platform-neutral) — the one-method interface.
3. `src/Service/Platform/Windows/PlatformConsole.cs` — P/Invoke declaration for `AllocConsole`,
   implements `EnsureConsole()` by calling it.
4. `src/Service/Platform/Linux/PlatformConsole.cs` — implements `EnsureConsole()` as an empty
   method body.
5. `Service.csproj` — add the `OutputType` conditions and `Compile Remove`/`Compile Include` blocks
   above.
6. `Program.cs`'s `Run()` — insert `new PlatformConsole().EnsureConsole();` as the first statement,
   before `ParseArgs`. No other control flow changes.
7. Update `.github/workflows/ci.yaml` (`lint-and-test`'s Build/Test steps) and
   `.github/workflows/cd.yaml` (`release-linux`'s publish step) to pass an explicit RID, per the RID
   section above.
8. Update `README.md`'s and `CLAUDE.md`'s `dotnet run`/`dotnet test --filter` examples to include
   an explicit RID.
9. Verify with explicit RID publishes, not plain `dotnet build`:
   - `dotnet publish src/Service -r win-x64 --self-contained` → **do not run it by typing its name
     at an already-open PowerShell/cmd prompt** — that inherits the terminal's console and gives a
     false negative (message appears immediately, `AllocConsole()` silently no-ops, indistinguishable
     from a normal console app). Launch it the way a real deployment would — double-click from
     Explorer, or `Start-Process .\Service.exe` from PowerShell (detaches into its own process group
     rather than sharing the console) — so there's genuinely no console ancestor to inherit. Confirm:
     no console at launch, no taskbar entry, then the console appears and the started/completed
     messages print, then the process exits (see the "known accepted rough edge" note above).
   - `dotnet publish src/Service -r linux-x64 --self-contained` → run from a terminal: builds and
     runs normally with the stub compiled in, output appears in the existing terminal as usual.
   - `dotnet build -r linux-x64` and `dotnet test -r linux-x64 --settings mstest.runsettings
     --filter TestCategory!=Integration` (mirroring the updated CI step) still pass clean.

## Test results

<!-- Added at Testing / Ready to Submit. -->
