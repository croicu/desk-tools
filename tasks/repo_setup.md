# Repo Setup

## Status: Implementation

## Problem statement

This repo was generated from a template (`tpl-cs`). It still contains placeholder tokens that
need to be replaced with real values before the repo is usable, and this file itself needs to be
retired once that's done.

## Placeholder tokens

| Token | Meaning | Appears in |
|---|---|---|
| `__package_name__` | C# namespace / project identifier (PascalCase, e.g. `MyProject`) | `src/__package_name__/` and `tests/__package_name__/` directory names, their `.csproj` file names (`__package_name__.csproj`, `__package_name__.Tests.csproj`), every `namespace __package_name__;` / `namespace __package_name__.Base;` / `namespace __package_name__.Tests;` / `namespace __package_name__.Mocks;` declaration, `src/__package_name__/AssemblyInfo.cs`'s `InternalsVisibleTo`, `Program.cs`'s and `tests/Mocks/TestSettings.cs`'s `using __package_name__.Base;`, the test project's `ProjectReference` path, `installer/Package.wxs`'s `Package`/`Directory` `Name`, `installer/Setup.wixproj`'s `TargetName` and its publish `Exec`'s project path |
| `__project_name__` | CLI/solution name (kebab-case, e.g. `my-project`) | the `.slnx` file name, `README.md`, CLI usage/error-prefix strings in `Program.cs`, comments in `installer/Setup.vcxproj` |
| `__description__` | One-line description | `README.md` tagline, `Program.cs`'s `--help` text |
| `__mission__` | A paragraph describing what this repo builds and why | `CLAUDE.md`'s `## Mission` section |
| `__manufacturer__` | Installer manufacturer/company name shown in Windows' "Apps & Features" | `installer/Package.wxs`'s `Manufacturer` attribute |
| `__upgrade_code__` | **Freshly generated GUID** (see note below) | `installer/Setup.wixproj`'s `UpgradeCode`, written there as `{__upgrade_code__}` (braces already in the file) |
| `__vcxproj_guid__` | **Freshly generated GUID** (see note below) | `installer/Setup.vcxproj`'s `ProjectGuid` (written there as `{__vcxproj_guid__}`, braces already in the file) *and* the matching unbraced `Id` on that project's `<Project>` entry in `__project_name__.slnx` — both must carry the exact same GUID |

Unlike the Python template (`tpl-py`), no padding trick is needed here — C# identifiers can start
with an underscore, so `__package_name__`/`__project_name__` are valid tokens to replace in place,
and there's nothing equivalent to PEP 508's alnum-boundary restriction on a `.csproj`/`.slnx` name.

**`__upgrade_code__`/`__vcxproj_guid__` are a different kind of token from the rest**: every other
token above gets a value you supply (a name, a description); these two must instead be freshly
*generated* per instantiation (e.g. `[guid]::NewGuid()` in PowerShell, or `uuidgen` on Linux/macOS)
— never copied from this file, from another instantiation, or reused between the two of them.
`__upgrade_code__` has a real correctness consequence if you get this wrong: Windows Installer uses
`UpgradeCode` to identify a product family for upgrade/downgrade detection, so two unrelated
products sharing one would have Windows Installer treat them as competing versions of the same
product. If this instance doesn't want a Windows installer at all (e.g. it's not a Windows CLI, or
you just don't need one), delete `installer/` entirely, its `<Project>` entry in
`__project_name__.slnx`, *and* the `release-windows-msi` job in
`.github/workflows/cd.yaml` (it otherwise fails looking for `installer/Setup.wixproj` on every
tagged release) instead of tokenizing any of it — the MSI is optional, everything else in this
template isn't.

## Implementation plan

0. **If this folder is not already a git repo** (e.g. it was unzipped from the template rather
   than created via GitHub's "Use this template" button): ask the user for the SSH endpoint of
   the destination repo (their private git server). Also confirm whether the remote repo itself
   already exists there — it may not. If it doesn't exist yet, ask the user how repos get
   provisioned on their server (a bare `git init --bare <path>.git` over SSH, a Gitea/GitLab/etc.
   web UI or API, or something else) rather than assuming; don't guess at server-specific
   tooling. Once the remote exists, `git init`, `git remote add origin <ssh-endpoint>`, and push
   once the placeholder replacement below is done and committed. Skip this step entirely if
   `.git/` already exists — the GitHub-template path already has one.
1. Ask the user for the real values of `__package_name__`, `__project_name__`, `__description__`, and `__mission__` if they weren't already given. If they want to keep the MSI installer under `installer/`, also ask for `__manufacturer__` and generate fresh values for `__upgrade_code__`/`__vcxproj_guid__` (e.g. `[guid]::NewGuid()`) — don't ask the user for these two, generate them yourself. If they don't want an MSI, delete `installer/`, its `<Project>` entry in the `.slnx`, and the `release-windows-msi` job in `.github/workflows/cd.yaml` instead of tokenizing any of it, and skip every installer-related item in the rest of this plan.
2. Grep the whole repo case-sensitively for `__` to find every occurrence (this also catches any spot missed by the table above).
3. Replace each token with its real value. Also remove `README.md`'s `## Setup` section (the
   paragraph pointing at this file) — it becomes stale once the tokens are gone.
4. Rename directories and the files inside them (`git mv` if the repo is already tracked, to
   preserve history) — C# needs both, unlike `tpl-py`'s single directory rename, since a
   `.csproj`'s file name is independent of its containing directory's name:
   ```
   git mv src/__package_name__ src/<PackageName>
   git mv src/<PackageName>/__package_name__.csproj src/<PackageName>/<PackageName>.csproj
   git mv tests/__package_name__ tests/<PackageName>
   git mv tests/<PackageName>/__package_name__.Tests.csproj tests/<PackageName>/<PackageName>.Tests.csproj
   git mv __project_name__.slnx <project-name>.slnx
   ```
   Note the `tests/<PackageName>/` folder deliberately has no `.Tests` suffix -- it's already
   inside `tests/`, so repeating that there would be redundant; the `.csproj` file itself keeps the
   `.Tests` suffix for a distinct assembly identity from `src/<PackageName>/<PackageName>.csproj`.

   `src/Base/`/`Base.csproj`, `tests/Base/`/`Base.Tests.csproj`, and `tests/Mocks/`/`Mocks.csproj`
   are **not** renamed -- those project names are deliberately stable across instantiations (the
   reusable Logger/Settings/Errors/Interfaces scaffold, its own tests, and its shared test-fake
   project, none of them app-specific). Step 3's blanket find/replace already substitutes the
   `__package_name__` token *inside* `src/Base`'s and `tests/Mocks`'s own files (`namespace
   __package_name__.Base;` -> `namespace <PackageName>.Base;`, likewise `AssemblyInfo.cs`'s
   `InternalsVisibleTo`, `Program.cs`'s `using` line, and `TestSettings.cs`'s own `using
   __package_name__.Base;`) -- there's just no directory/file rename to do for any of these three.
5. Regenerate the solution's project references rather than hand-editing the renamed paths into
   the `.slnx` — simpler and less error-prone than fixing up the XML by hand:
   ```
   dotnet sln <project-name>.slnx remove src/<PackageName>/<PackageName>.csproj tests/<PackageName>/<PackageName>.Tests.csproj
   dotnet sln <project-name>.slnx add src/<PackageName>/<PackageName>.csproj
   dotnet sln <project-name>.slnx add tests/<PackageName>/<PackageName>.Tests.csproj
   ```
   (if step 4's `git mv` already broke the stale references enough that `remove` errors, skip
   straight to the two `add` calls — `dotnet sln add` is idempotent enough to just re-add.)
   `src/Base/Base.csproj`'s, `tests/Base/Base.Tests.csproj`'s, and `tests/Mocks/Mocks.csproj`'s own
   `.slnx` entries need no resync — none of those paths ever change, so all three are already
   correct as checked in. `dotnet sln add` nests the new test project under its own
   `/tests/<PackageName>/` folder rather than flattening it
   alongside `Base.Tests.csproj` in `/tests/` — cosmetic only (Solution Explorer grouping, not
   build behavior), but worth manually merging the two `<Folder Name="/tests/...">` blocks back
   into one flat `/tests/` folder for consistency with `/src/`'s style.
6. Run `dotnet build`, `dotnet format --verify-no-changes`, and `dotnet test` — all should pass
   clean on the renamed project. Sanity-run the CLI itself
   (`dotnet run --project src/<PackageName>`) to confirm it exits 0 — note `settings.json` (if you
   want to see non-default logging behavior) resolves relative to whatever directory you actually
   invoke the command from, same as `tpl-py`'s convention, not automatically the project directory.
   If `installer/` was kept (Windows only — this step doesn't run on the AlexWSL-style Linux
   verification host from CLAUDE.md's cross-platform convention, since WiX cannot build there at
   all): `dotnet build installer/Setup.wixproj` should also pass clean and produce
   `release/Debug/<PackageName>.msi`; a real install/uninstall (`msiexec /i` / `/x`, needs
   elevation) is worth doing at least once to confirm end to end.
7. Set the initial `Synced to` timestamp in `CLAUDE.md`'s `## Template Sync` section: fetch
   `tpl-cs`'s `ADDENDUM.md` (plain HTTPS, e.g. `WebFetch`) and use its latest entry's timestamp,
   or the current time if the addendum is empty — otherwise a brand-new instance would look
   "behind" on history that predates it.
8. If the GitHub issue-based task workflow in `CLAUDE.md` will be used, create the `status:brainstorm` / `status:implementation` / `status:testing` / `status:ready-to-submit` labels on the new repo (`gh label create`) — they don't exist on a fresh repo.
9. Delete this file (`tasks/repo_setup.md`) and its entry in `CLAUDE.md`'s `## Pending Tasks` section.

## Test results

Dry-run test-drive (2026-09-11): instantiated as `DemoTool`/`demo-tool` directly on the freshly
authored template (not yet a real GitHub-template instantiation, just the mechanical rename/build
steps). `dotnet build`, `dotnet format --verify-no-changes`, and `dotnet test` (1 test) all passed
clean immediately after the rename in step 4 and the solution resync in step 5, with zero template
changes needed. A direct `dotnet run --project src/<PackageName>` also exited 0 and, once
`settings.json` was placed in the invocation directory with a permissive `logLevel`, printed the
expected `[INFO][general]` lines — confirming step 6's CWD note isn't a guess.

Re-run after the `src/Base` split (2026-09-11): instantiated as `DryRun`/`dry-run`. Step 4's `git
mv` list needed no change for `src/Base` (its path is stable across instantiations, as noted
inline above); step 5's two `add` calls resynced only the app project, and `Base.csproj`'s existing
`.slnx` entry needed no touch. `dotnet build` produced both `Base.dll` and `DryRun.dll` as separate
assemblies; `dotnet format --verify-no-changes` and `dotnet test` (1 test) both passed clean. Manual
runs confirmed `--help` and an unrecognized-argument error both still print correctly through the
now-two-assembly `Logger` (`__package_name__.Base.Logger`).

Re-run after splitting tests into `tests/Base` + `tests/<PackageName>` (2026-09-11): instantiated
as `DryRun`/`dry-run` again. Step 4's renamed `git mv tests/__package_name__ tests/<PackageName>`
(no more `.Tests` folder suffix) worked as documented; `tests/Base/` needed no rename, matching
`src/Base/`. `dotnet sln add` nested the new test project under its own `/tests/DryRun/` folder as
noted inline in step 5 — merged back into one flat `/tests/` folder by hand. `dotnet build`
produced `Base.dll`, `DryRun.dll`, `Base.Tests.dll`, and `DryRun.Tests.dll` as four separate
assemblies; `dotnet format --verify-no-changes` and `dotnet test` (2 test files, 1 test each) both
passed clean, with `Base.Tests`'s `ConsoleLogSinkTests` exercising `ConsoleLogSink`'s
`Create()`/`Dispose()` guard successfully (both public; `InternalsVisibleTo("Base.Tests")` itself
remains for `Logger.Reset()`'s internal test-only access, not yet exercised by any test). A manual
`--help` run still printed correctly.

Re-run after adding the `installer/` MSI scaffold (2026-09-12): instantiated as `DryRun`/`dry-run`
again, generating fresh values for `__upgrade_code__`/`__vcxproj_guid__` (PowerShell
`[guid]::NewGuid()`, one call each) and `Dry Run Co` for `__manufacturer__`, per step 1's updated
guidance. No rename needed for anything under `installer/` — none of its own file/directory names
carry a token, only file *contents* do, so step 3's blanket find/replace was enough; step 5's
`dotnet sln remove`/`add` calls (scoped to the app/test csproj paths only) left the
`installer/Setup.vcxproj` entry in `dry-run.slnx` untouched, `Build Project="false"` and all.
`dotnet build` (bare, resolving `dry-run.slnx`) produced the same four assemblies as before and
confirmed it does *not* touch `installer/Setup.vcxproj` (that project's `Build Project="false"` is
respected by `dotnet build` itself, not just Visual Studio's own UI — checked explicitly since that
was the whole point of adding it that way rather than leaving `installer/` out of the `.slnx`
entirely). `dotnet format --verify-no-changes` and `dotnet test` (2 test files, 1 test each) both
still passed clean (format's console output included a "Warnings were encountered while loading the
workspace" line, from its workspace loader not knowing what to do with the non-C# `.vcxproj` in the
solution — harmless, confirmed by a clean `0` exit code on a repeat run).

Separately, `dotnet build installer/Setup.wixproj` produced `release/Debug/DryRun.msi` (`TargetName`
correctly tokenized, `OutputPath` landing at the repo-root `release/` as configured, no `Platform`
segment) and building the same thing through `installer/Setup.vcxproj` via real `MSBuild.exe`
(`dotnet build` itself cannot load a `.vcxproj` at all) produced an identical result, confirming the
`v143` `PlatformToolset` baseline resolves correctly here alongside this machine's newer `v145`.
Administrative extraction (`msiexec /a`) confirmed the exact expected file set (`Base.dll`,
`DryRun.dll`, `DryRun.exe`, `DryRun.deps.json`, `DryRun.runtimeconfig.json`, `settings.json`; no
`.pdb`) landing under `PFiles64\DryRun\` (the 64-bit path — `Platform=x64` in `Setup.wixproj` is
what makes `Package.wxs`'s `ProgramFiles6432Folder` resolve there instead of the 32-bit default),
a COM query against the built MSI's own `Property` table confirmed `Manufacturer` came through as
`Dry Run Co`, and the extracted `DryRun.exe --debug` ran correctly end to end.
