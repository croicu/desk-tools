# desk-tools

Authoring repo for Claude Code MCP servers and tools used by the ecosystem and distributed by desk-organizer. Produces artifacts; does not run them.

---

## Build

```bash
dotnet build
```

## Run

```bash
dotnet run --project src/Service
```

## Lint

```bash
dotnet format
```

## Test

```bash
dotnet test
dotnet test --filter "FullyQualifiedName=Croicu.Desk.Tools.Service.Tests.Unit.ProgramTests.Main_RunsClean"   # single test
```
