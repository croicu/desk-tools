# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Mission

Builds a service for running MCP servers. The service is a standalone process that can execute MCP code at a high privilege mode and can safely handle sensitive data.

## Template Sync

- **Source**: [croicu/tpl-cs](https://github.com/croicu/tpl-cs)
- **Synced to**: 2026-09-13T02:13:22Z (set by `tasks/repo_setup.md` at instantiation time; left
  unset in `tpl-cs`'s own master copy of this file, since the source has nothing to sync against)

This repo is either `tpl-cs` itself or was generated from it. `tpl-cs`'s `ADDENDUM.md` is a
curated, timestamped log of changes meant for downstream instances (new/changed rules,
base-module fixes, obsoleted patterns) — routine housekeeping doesn't get an entry. Which
protocol below applies depends on which repo you're in.

### Reading the addendum (applies in an instance)

1. Fetch `tpl-cs`'s `ADDENDUM.md` over plain HTTPS (e.g. `WebFetch` against the raw content
   URL) — no `gh` CLI, no `git clone`, no persistent git remote required.
2. Compare each row's timestamp against this repo's `Synced to` value above.
3. For rows newer than that, fetch only that entry's individual file under `addendum/` (not the
   whole history) and decide whether/how to apply it here.
4. After applying (or deliberately skipping) everything newer, bump `Synced to` above to the
   latest entry's timestamp.

### Writing an addendum entry (applies only in `tpl-cs` itself)

1. When making a change meant for downstream instances, add a new file under `addendum/`
   (filename prefixed with an ISO timestamp) describing what changed, why, and what an instance
   should do about it.
2. Append a row to `ADDENDUM.md`'s table (timestamp, title, filename).

### Porting an instance's improvement back to `tpl-cs` (applies in an instance)

The addendum protocol above is one-directional — `tpl-cs` pushing a change out to instances. This
is the reverse channel: when a change made *in an instance* touches a **template-inherited
artifact** (a class this repo started with from `tpl-cs`'s scaffolding, e.g. `Settings.cs`'s
`Settings` or `Diagnostics.cs`'s `Logger`; or a markdown file `tpl-cs` itself authors, e.g. this
file or `ADDENDUM.md`'s structure) and the change is generalizable rather than specific to this
repo's own domain, open an issue in `croicu/tpl-cs` (`gh issue create --repo croicu/tpl-cs`)
describing the change and cross-linking back to the commit/PR here, so `tpl-cs` can decide whether
to adopt it and, if so, write its own addendum entry per "Writing an addendum entry" above. Not
every edit to a template-inherited file qualifies — a one-off tweak that only makes sense in this
repo's own context doesn't need a backport issue, only a fix, convention, or reusable pattern that
would generalize to other instances too.

## Cross-Repo Coordination

Not every instance needs this section — add it (or an adapted version of it) once this repo has a
real data/API contract with another repo in your ecosystem (a producer/consumer relationship, not
just "both repos happen to exist"). Same wait-for-real-need judgment call as the DI
composition-root note under Coding Style — build this when the need is real, don't pre-build it.
Retrofit is cheap — this is process guidance, not code.

**Placement rule**: a cross-repo issue lives in whichever repo owns the actionable follow-up, not
necessarily where the need originated:
- **This repo ships a breaking or notable change** (a changed contract, a deprecated symbol, a
  schema migration) → open an issue in the consumer repo(s) announcing it, since that's where the
  reacting work happens.
- **A consumer needs something from this repo** (a new capability, a bug in what it returns) →
  open an issue here requesting it, since that's where the building work happens.

**Does a given change need one at all?** If this repo curates a public surface (Architecture
convention 7), use that boundary to decide cheaply instead of re-deriving it each time: touched
only internal (`internal`-visibility, not re-exported as `public`) implementation? No cross-repo
issue needed, *unless* the change alters externally observable behavior anyway (a bug fix that
changes what a public method returns still counts). Touched the actual public surface (a `public`
class/method signature, a CLI flag, a persisted file/schema format)? Default to assuming a
cross-repo issue is needed, then confirm.

**Conventions**:
- Label every cross-repo issue `cross-repo` (alongside the normal `status:*` label) so these
  threads are filterable apart from this repo's own internal work — create the label
  (`gh label create`) if it doesn't exist yet.
- Always cross-link: the issue body must reference the originating repo/issue/commit, so either
  side is navigable from the other.
- Use `gh issue create --repo <owner>/<repo>` to open a cross-repo issue directly from wherever
  you're working — no need to switch working directories first.

**Multiple consumers**: don't build a consumer registry, fan-out-on-breaking-changes, or a
rollout-tracking process ahead of a second real consumer — that's speculative process-building, the
same judgment call as the "don't build a DI composition-root prematurely" note under Coding
Style. When a second consumer repo actually arrives, that's the trigger to design that extension,
not before.

## Collaboration rules

- Before implementing any feature or non-trivial change, ask clarifying questions until the intent is unambiguous.
- If anything is unclear or could be interpreted multiple ways, ask — do not assume and implement.
- **Before running `git commit` or `git push`** (in this repo, or when porting a fix to
  `croicu/tpl-cs`), stop and wait for the user's explicit review and go-ahead — regardless of
  whether format/build/test all pass and the change is otherwise ready. Implement the change, run
  the verification steps, and present the diff/summary, then wait; don't commit or push until the
  user responds. This holds even mid-task (e.g. after opening a tracking issue but before the code
  itself is committed) and even for a change the user directly asked for — asking for a change
  isn't the same as approving the commit.

### Task workflow

Tasks are tracked as GitHub issues in this repo, status via labels: `status:brainstorm`,
`status:implementation`, `status:testing`, `status:ready-to-submit`, `status:ready-for-integration`
(only needed once this repo has a `cross-repo` relationship with another — see "Ready for
Integration" below). There is no `status:done` label — reaching Done means closing the issue.
(These labels don't exist on a freshly-created repo — create them with `gh label create` before
the first task needs one.)

Tasks come in two flavors, which affects whether step 1 below applies:

- **Planned tasks** — a `tasks/<task-name>.md` already exists (or is being freshly authored as a
  deliverable in its own right) before implementation discussion starts, e.g. dropped in by the
  user ahead of time. Follow all stages below, starting with Brainstorm.
- **Ad-hoc tasks** — the task emerges organically from conversation (no pre-existing or
  deliberately-authored task file). Skip straight to Implementation: no `tasks/<task-name>.md` gets
  created at all, just open the GitHub issue directly once the discussion has converged. Don't
  create a task file first just to immediately trim/delete it — that's churn, not documentation.

For any non-trivial feature or change, follow these stages:

1. **Brainstorm** (planned tasks only) — copy `tasks/new_task.md` to `tasks/<task-name>.md` with the problem statement; update it with conclusions as the design discussion progresses. This is scratch space for live back-and-forth — an issue isn't required at this stage, but a lightweight tracking issue labeled `status:brainstorm` can be opened for backlog visibility if wanted; either way, `tasks/<task-name>.md` (not the issue) stays the working document until the design converges.
2. **Implementation** — open a GitHub issue (`gh issue create`) with the converged problem statement + conclusions as the body, labeled `status:implementation`. Write the code. For a planned task, `tasks/<task-name>.md` is no longer the source of truth once the issue exists — trim it to a one-line pointer at the issue (or delete it) rather than maintaining both. For an ad-hoc task, there's no file to trim — the issue was the first artifact.
3. **Testing** — relabel the issue `status:testing`. Verify correctness; post test results and any open issues as an issue comment. **For a `cross-repo` issue that originated from a consumer repo's own testing/diagnosis**, this repo's own verification — even a live check against a real external dependency — confirms the fix works in isolation, but isn't the same as confirming the originally reported symptom is actually resolved: that requires the consumer to pull the updated code and re-test in its own context. Say so explicitly in the comment rather than implying it's fully confirmed.
4. **Ready to Submit** — relabel `status:ready-to-submit`. Run format + build + tests; confirm docs are up to date; post a summary comment. This is as far as *this* repo's own work can confirm the issue.
5. **Ready for Integration** (`cross-repo` issues that need consumer-side verification only — see
   "Who closes an issue" below for which issues that is) — once the fix is actually merged/pushed,
   relabel `status:ready-for-integration` instead of leaving it at `status:ready-to-submit`. This
   is the label that actually names the gap: this repo's own checks can confirm the fix works in
   isolation, but not that the originally reported symptom is resolved — that needs the consumer to
   pull the update and re-test in its own context. An issue with no such downstream dependency (a
   same-repo bug, nothing cross-repo) skips this stage entirely — `status:ready-to-submit` is
   already its terminal pre-close state.
6. **Done** — close the issue after merge. For a planned task, delete `tasks/<task-name>.md` once the issue is closed — the issue (body + comments) is the sole source of truth from that point on, so there's no reason to keep a stale duplicate on disk. (Only applies when a real issue holds the full history; a Done task with no issue keeps its local file.) Ad-hoc tasks have nothing to delete.

**Who closes an issue**: applies to issues opened "in the family" — by the repo owner themselves
(directly, or via a cross-repo issue from one of their own other repos) — the normal case before
this project has any external contributors. In that case, whoever opened it is the one who closes
it, not automatically whoever did the implementation work: leave it open (at
`status:ready-for-integration` once pushed, if it needed that stage; otherwise
`status:ready-to-submit`) and say so; don't close it, and don't use GitHub's auto-closing
commit-message keywords (`Closes #N`, `Fixes #N`, `Resolves #N`) for it, since those close on push
regardless of who's supposed to have that call — use a non-closing reference instead (`Ref #N`,
`Part of #N`, `Addresses #N`). This matters most for `cross-repo` issues diagnosed from a
consumer's own testing: the opener is the one positioned to actually verify the fix in that
original context, so closing is their call, not a mechanical side effect of merging. The one
exception even within the family: an issue Claude opened itself mid-task (e.g. a
`status:implementation` issue opened while executing a planned/ad-hoc task in the same session)
can be closed directly, since Claude is the opener there.

**If an issue ever comes from a genuine external contributor** (not the repo owner or one of their
own other repos), this whole rule doesn't apply — follow normal GitHub OSS etiquette instead
(auto-close via a merged PR's `Closes #N` is fine, maintainer discretion applies). Revisit this
section if/when that actually happens; it's not a case worth designing for speculatively before a
real external contributor shows up.

## Before committing

Run these before every commit:

```bash
dotnet format
dotnet build
dotnet test
```

Then stop and wait for the user's review before actually running `git commit`/`git push` — see
"Collaboration rules" above.

## Documentation rule

After any change that affects the public interface, CLI, or file formats, update the relevant docs:

- `CLAUDE.md` — commands, architecture notes
- `docs/ARCHITECTURE.md` — modules, data flow, contracts
- `docs/PROTOCOL.md` — CLI signature, file format schemas

## Commands

```bash
# Build
dotnet build

# Run
dotnet run --project src/Service

# Lint / format
dotnet format

# Test
dotnet test
dotnet test --filter "FullyQualifiedName=Croicu.Desk.Tools.Service.Tests.Unit.ProgramTests.Main_RunsClean"   # single test

# Build the Windows MSI installer (Windows-only, WiX cannot build on non-Windows hosts at all --
# see installer/Setup.wixproj). Not part of the `dotnet build`/`test` commands above;
# Service.slnx does reference installer/Setup.vcxproj (a Solution Explorer-only shim
# around the command below, see its own header comment) but with Build Project="false", so it's
# excluded from `dotnet build Service.slnx` and from a plain `dotnet build` too. Delete
# `installer/` and its <Project> entry in the .slnx entirely if this instance doesn't want an MSI.
dotnet build installer/Setup.wixproj
```

## Architecture conventions

1. Internal processing uses strongly typed records (immutable value data) or classes (behavior).
2. `Interfaces.cs` contains public contracts: persisted/shared data (records/classes with no
   behavior) *and* behavioral interfaces meant for a consumer to actually implement/inject (e.g.
   `ILoggingSink`, already scaffolded here — see the Logging section below). The distinction from
   `Contracts.cs` isn't data-vs-behavior, it's "does an external consumer implement this" vs. "does
   this only wire this project's own internals together": a behavioral interface belongs in
   `Interfaces.cs` specifically when a host application is expected to supply its own
   implementation of it (most relevant once this project is consumed as a library by another
   project — see Architecture convention 7), not just when there's some internal data type to
   describe. Behavior that merely *operates on* a data contract still belongs in a dedicated
   entity/service class, not on the record/class itself. Keep any behavioral interface placed here
   leaf-safe (convention 8) — default parameter values like a category string should be literals,
   not references to constants in `Logger.cs`, even where `Logger.cs` already defines the
   same constant.
3. `Contracts.cs` contains runtime behavioral interfaces (for things like workers/executors) that
   wire this project's *own* internals together — never referenced by external consumers, unlike
   `Interfaces.cs`'s behavioral interfaces above.
4. Unit tests (`tests/Base/Unit/`, `tests/Service/Unit/` — one test project per `src/`
   project, mirroring the `Base`/app split) must run offline. Integration tests
   (`tests/Base/Integration/`, `tests/Service/Integration/`), if a project has them, may
   hit real external services — that's a deliberate scope split, not a loophole in rule 4.
   `dotnet test` (or `dotnet test <project-name>.slnx`) runs every test project's assembly by
   default regardless of folder, so adding an integration suite means accepting network calls in
   the default invocation unless you also tag it with `[TestCategory("Integration")]` and run
   `dotnet test --filter TestCategory!=Integration` as the default/CI command instead — that filter
   applies uniformly across every test project in the solution in one invocation, not per-project.
5. Prefer explicit, readable C# over clever abstractions.
6. Prefer constructor/parameter injection over mocking this project's own internal classes
   (reflection-based fakes, e.g. via Moq/NSubstitute against a concrete type) in tests — e.g. a
   component that talks to the outside world (network, filesystem, clock) should take that
   dependency as a constructor parameter (typed as an interface from `Interfaces.cs`/`Contracts.cs`,
   defaulting to the real implementation), so tests can pass a fake object instead of mocking a
   concrete class from the module under test. Mocking is still the right tool for faking a
   *third-party* library's own internals (e.g. an HTTP client type you don't own) — the distinction
   is whether the thing being faked is your code or someone else's.
7. **Curate a public API surface, separate from internal implementation.** Even a single-project
   repo benefits from distinguishing "what's safe for another project to reference" from "internal
   implementation, free to change." Default every type/member to `internal` unless it's part of
   the deliberate public surface — `public` is the opt-in, not the default, unlike loose
   file-per-script code where everything ends up accidentally public. If the internal
   implementation is substantial enough that `internal` alone isn't a strong enough signal, nest it
   under a dedicated sub-namespace (e.g. `Croicu.Desk.Tools.Service.Internal`) rather than spreading it flat
   across the project root.
9. **Keep the internal dependency graph acyclic — break cycles with an interface, not a runtime
   workaround.** If two concrete classes would otherwise need each other, introduce an interface
   (per rule 3's `Contracts.cs` convention) that one side depends on instead of the other's
   concrete type — this is the same seam rule 6's constructor-injection convention already creates,
   just framed as a graph property: depending on an abstraction instead of a concretion is what
   keeps the graph from looping back on itself. Verify this mechanically when it matters, not by
   feel: list every file's `using Croicu.Desk.Tools.Service...;` directives (`grep -E "^using
   Croicu\.Desk\.Tools\.Service" -r src/`)
   and confirm no file is reachable from itself by following them — this now spans a real project
   boundary too: `src/Base/` (`Base.csproj`) must never reference `Croicu.Desk.Tools.Service` (the
   app's own namespace), since `src/Service/` (`Service.csproj`) already depends on
   `Base.csproj` the other way. A passing test suite is not
   proof the graph is acyclic, since load-order luck can mask a real cycle. C# doesn't have
   Python's lazy-import escape hatch for masking a cycle at the language level (a circular
   `using`/namespace reference is a compile error, full stop, not a runtime-deferred one) — which
   means this rule is more of a hard constraint here than an easily-fudged one, but the interface
   seam is still the right fix, not restructuring types into one file just to dodge the compiler.
10. **`src/Base` must stay safe to host multiple heterogeneous clients in one process.** Base.dll
    is designed to be usable inside a service where independent clients — each "renting" its own
    `ExecutionContext` — can be live at the same time, each with its own settings and logging
    configuration, none of them able to see or disturb another's. Concretely: never add plain
    `static` mutable state to `src/Base` for anything that legitimately differs per client (a sink
    list, a sink's configured level/categories, "the current settings") — scope it with
    `AsyncLocal<T>` instead, the way `Logger`'s sink list, `ConsoleLog`'s/`DebugLog`'s
    `InstanceActive` guards, and `Settings.Current`/`Context.Current` all already do (see
    `Logger.cs`/`Sinks/`/`Settings.cs`/`Context.cs`) — each sink's own pending buffer (used by
    `Flush`/`Clear`/`Drain`) is plain per-instance state rather than `AsyncLocal` itself, since
    instance-scoping is already enough once the *list* holding those instances is `AsyncLocal` and
    a sink instance is never shared across clients. A plain process-wide
    `static` field in `src/Base` is only correct for state that's genuinely meant to be shared by
    every client regardless of context (rare, and worth a comment explaining why when it happens) —
    default to `AsyncLocal` for anything else, even if `src/Service` (typically a
    single-tenant CLI) never itself exercises the multi-client scenario.

    **Known gap, deliberately deferred**: `ConsoleLog` still writes straight to the actual OS
    console (under `ConsoleLock`) from whichever client's context is logging — correct (no
    interleaved/corrupted output, since every write is serialized through that lock) but not the
    same as giving the console a single owning thread, which a real multi-client *server* host would
    likely want. The intended eventual shape: an `IConsoleWriter` seam injected into
    `ConsoleLog` (default implementation: write straight to `Console`, today's behavior; this is
    the "rare, genuinely shared" exception noted above, since the queue feeding a real console has
    to be one real shared instance handed to every client's sink, not `AsyncLocal`), with an
    alternative implementation that enqueues formatted lines for a designated main thread to drain
    and print via an explicit `Pump()` call the host makes at its own natural points (not a
    background thread/timer inside `Base` — matches this project's explicit-over-magic style, see
    "Explicit DI First" under Coding Style). **Not implemented** — noted here only so the idea isn't
    lost, not as a rule to follow yet; revisit when a real multi-client host actually exists.
11. **`src/Base` must work correctly on every platform .NET runs on (Windows, Linux, macOS), not
    just whichever one you happen to be developing on.** Base.dll is the reusable library layer any
    future host builds on (convention 7), so a platform-specific bug there blocks every consumer,
    not just this project's own current single-tenant CLI. Concretely: never hardcode a path
    separator (`\` or `/`) — use `Path.Combine`/`Path.DirectorySeparatorChar`, or rely on .NET's own
    forward-slash handling; never assume filesystem paths are case-insensitive (Linux's is
    case-sensitive; Windows/macOS by default aren't); never call a Windows-only API from `src/Base`
    (registry access, `System.Drawing`, COM interop); don't assume a specific line ending
    (`Environment.NewLine`, not a hardcoded `\r\n`/`\n`). Don't just reason about this by inspection
    when a `src/Base` change is nontrivial enough to plausibly be platform-sensitive (new file I/O, a
    new OS-specific API, a new external dependency) — actually run `dotnet format
    --verify-no-changes`/`dotnet build`/`dotnet test` on a second OS (e.g. a Linux machine/WSL
    instance alongside a Windows dev box) before considering the change done.

## Logging

- **Use `Logger`** (`Croicu.Desk.Tools.Base.Logger` — lives in the `src/Base` project, not
  the app's own namespace) — not bare `Console.WriteLine`.
- **`Console.*` is confined to `src/Base/Sinks/`** — the Logger's sink implementations
  (`DiagnosticsLog`/`ConsoleLog`) are the only place allowed to call
  `Console.Write`/`Console.WriteLine`/`Console.Error.WriteLine` directly; everywhere else
  (`Program.cs`, etc.) must go through `Logger`, since the Logger owns the console. Install a sink
  (`Logger.SetLogger(...)`) as the very first thing `Main`/`Run` does, before anything that might
  log (CLI-arg parsing, config loading) — otherwise those early log calls hit the always-buffering,
  never-printing default sink and vanish silently. For output that isn't really a leveled log
  message but must always reach the user regardless of the configured `logLevel` (e.g. `--help`
  text), use `Logger.Print(message)` rather than bypassing the Logger with a raw `Console` call —
  it writes unconditionally, with no level/category filtering and no `[LEVEL][category]` prefix.
- **All features log success and errors** — no silent success, no swallowed errors.
- **New components are encouraged to introduce their own log category** (see Categories below)
  **and log the assumptions their code validates, not only the exceptions it throws.** An
  assumption holding on the normal, expected path (e.g. "the config file's shape matched what we
  expected") logs at `Verbose` — see the Level guide's `Logger.Diagnostic` entry. An assumption
  failing under some unexpected-but-recoverable condition (e.g. the config file wasn't found, so
  defaults were substituted) logs at `Info` or `Warning` depending on how much the fallback
  mitigation affects functionality — `Info` when the fallback fully preserves behavior, `Warning`
  when it only partially does (the caller should still notice).
- **Message length by severity**:
  - **Success (info)** — short: feature started, feature ended.
  - **Recoverable issues (warning)** — medium: enough context to understand what went wrong and why it was non-fatal.
  - **Errors (error/fatal)** — detailed: full context needed to reproduce and diagnose.
- **Level guide**:
  - `Logger.Diagnostic` (`Verbose`) — one message per chunk of work, so a run's progress is visible and a hang is distinguishable from silence (e.g. a batch-processing loop logging one `Verbose` line per item it starts)
  - `Logger.Info` — normal notable events (start, end, success, counts)
  - `Logger.Warning` — recoverable problems (retries, skipped items)
  - `Logger.Error` / `Logger.Fatal` — unrecoverable failures
  - `Logger.Print(message)` — not a level at all: raw, unconditional output for text that must
    always reach the user regardless of `logLevel` (e.g. `--help` text) — see the `Console.*` bullet
    above.
  - `Logger.Perf(description, elapsedSeconds)` — duration markers for timing-sensitive spans
    (a network call, a slow query, anything worth measuring), always logged at `Info` under a
    fixed category `perf` (`DiagnosticsCategories.Perf`, not the caller's choice — unlike every
    other `Logger` method). Message shape is `"duration: {elapsed:F3}s - {description}"`. If this
    project is ever consumed as a library by another project, its own perf markers become visible
    to the host via the injectable `ILoggingSink` interface (see `Interfaces.cs` and the "Explicit
    DI First" rule under Coding Style) rather than a bridge onto `Microsoft.Extensions.Logging`.
- **Categories** — every `Logger` method takes an optional `category = "general"`, filterable via
  `settings.json`'s `logCategories` (an open string, not a closed enum — `Logger.cs` only
  defines `DiagnosticsCategories.General` as a starting constant). Console output is
  `[LEVEL][category] message`. **Effective default depends on whether `logLevel` is explicit** (see
  "Specific settings override generic ones on scope overlap" under Coding Style — this is that
  rule's origin case): if `settings.json`'s `logCategories` is left empty/absent, an explicit
  `logLevel` decides it outright — permissive (`verbose`/`info`/`warning`) resolves to `[]`
  (unfiltered), restrictive (`error`/`critical`) resolves to `["general"]` — regardless of `debug`.
  Only when `logLevel` is left at its implicit default does `debug` get consulted as the fallback
  (`debug: false` -> `["general"]`, `debug: true` -> `[]`). An explicit non-empty `logCategories`
  always overrides all of this outright. **`excludedCategories`** is a complementary deny-list,
  only in effect when the resolved `logCategories` is `[]` (the true unfiltered state) — inert
  against an explicit non-empty `logCategories` or the restrictive `["general"]` default.

## Coding Style

- **`Interfaces.cs` holds public contracts, not implementations** — data (records/classes, no
  methods) plus behavioral interfaces meant for a consumer to implement/inject (e.g.
  `ILoggingSink`). Either way, no concrete logic lives here — a data record has no behavior of its
  own (that lives in a separate entity/service class), and an interface's methods are signatures
  only, never an implementation.
- **Explicit DI First** — when choosing between (a) integrating with an ambient/shared mechanism
  (`Microsoft.Extensions.Logging.ILogger`, a service locator, a static singleton) and (b) explicit
  constructor/parameter injection of an object matching an interface from `Interfaces.cs`, default
  to (b), especially when the injected object can carry more capability through unmodified than
  the ambient mechanism would let you reconstruct on the other side. Concrete case: `ILoggingSink`
  (see `Interfaces.cs`) is an explicitly injected interface, not a bridge onto
  `Microsoft.Extensions.Logging.ILogger` — a host's own `Logger` already has real behavior
  (category filtering, `excludedCategories`, level thresholds), and handing it through directly via
  DI preserves all of that with zero glue code, whereas bridging onto `ILogger` would force
  reconstructing that behavior from scopes/structured-state and a category-naming convention on the
  consuming side. Don't assume "the idiomatic framework pattern" (e.g. "real .NET libraries take an
  `ILogger<T>`") automatically wins over this — capability-preserving DI is the default here even
  when a framework-level integration point already exists and would technically work.
- **Explicit over brief** — if two implementations are equivalent, choose the one that is easier to read and debug, even if it is longer.
- **No LINQ method chains for multi-step logic** — use explicit `foreach` loops. A chained `.Where().Select().Aggregate()` obscures control flow and makes multi-step logic harder to step through in a debugger, the same way the Python template this was ported from avoids comprehensions. A single, trivial operation (`.Any()`, `.Count()`, `.FirstOrDefault()` used plainly) isn't what this is aimed at — the line is "does stepping through this in a debugger actually show you what's happening."
- **No lambdas for logic** — use named local functions or plain `foreach` loops instead of a lambda that does real work (a multi-line predicate, a callback with branching). Lambdas hide intent and cannot be stepped through as cleanly as a named method. This doesn't forbid trivial single-expression uses that are already idiomatic and inert (e.g. `default!`-style factory delegates with no branching).
- **`using`-directive count as SRP signal** — more than 5–10 `using` directives in a file is a hint that the file may be doing too much. Not a hard rule, but worth pausing to consider whether responsibilities should be split.
- **Don't build a DI composition-root/factory prematurely** — the same wait-for-evidence judgment as the using-count signal applies to DI wiring. A constructor picking up its second or third injectable parameter (e.g. `Program(string[] argv, ISettingsProvider? provider = null)`) is not yet a smell; extracting a shared factory/helper from a single data point risks guessing at the wrong abstraction shape. Wait for real duplication — a second call site needing the same wiring, or a constructor parameter list that's genuinely grown unwieldy — before extracting one.
- **Specific settings override generic ones on scope overlap** — when two configuration knobs can both influence the same outcome, the more specific/targeted one wins wherever they'd otherwise disagree, not the more generic/blanket one; the generic one only falls back into play when the specific one was left at its implicit default. Origin case: `settings.json`'s `logLevel` (a targeted verbosity control) vs. `debug` (a blanket flag) both used to influence the console log-category default, with `debug` winning outright — so setting `logLevel: "verbose"` alone did nothing, silently muted by `debug`'s separate default (see the Logging section above for the resulting behavior). Apply this whenever a new settings key's effect could overlap with an existing broader flag's — don't let a coarse toggle silently override an explicit, narrower setting the user actually configured.

## New Task

## Pending Tasks

## Completed Tasks
- **Port `tpl-py`'s template scaffold to C#** — [issue #1](https://github.com/croicu/tpl-cs/issues/1).
  Founding commit of this repo: the addendum/backport sync protocol, GitHub-issue task workflow,
  and Logger/Settings/AppError scaffold, all ported from `croicu/tpl-py` and adapted to C# idioms
  (`record`/`interface` instead of `dataclass`/`Protocol`, `dotnet format`/`build`/`test` instead
  of `ruff`/`pytest`, a `.slnx`-tied `src`+`tests` project layout). See the issue for the full list
  of adaptation decisions and what was verified (a real dry-run instantiation, not just a read of
  the code) before this was pushed.
