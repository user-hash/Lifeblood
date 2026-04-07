# Lifeblood

See the lifeblood of any codebase. Language-agnostic semantic code analysis.

Grep finds strings. Lifeblood sees meaning. What depends on what, what is alive, what is dead, what violates your architecture, and what breaks if you change something.

Born from building a [400k LOC project with AI assistance](https://github.com/user-hash/LivingDocFramework/blob/main/docs/CASE_STUDY.md) and realizing that [Roslyn](https://github.com/dotnet/roslyn) (C#/.NET compiler platform) is the single most underused tool in AI-assisted development. Every language deserves the same capability. This is our attempt to make that happen.

---

## What It Does

Feed it a codebase. Get back a semantic graph. Run analysis on the graph.

```
Source Code  →  [Language Adapter]  →  Semantic Graph  →  [Analysis]  →  Results
   any              ICodeParser           universal        pure core      actionable
language            or JSON                                stateless
```

**Analysis capabilities:**

| Analysis | What it tells you |
|----------|-------------------|
| Coupling | Fan-in, fan-out, instability per node |
| Blast radius | What breaks if you change this file/type/method |
| Dead code | What is unreachable from entry points |
| Architecture rules | "Domain must not reference Infrastructure" violations |
| Tier classification | Pure / Boundary / Runtime / Tooling per file |
| Circular dependencies | Cycles in the dependency graph |
| Hub detection | God classes and bottleneck nodes (betweenness centrality) |
| Invariant verification | Check if INV-xxx rules hold in actual code |

---

## Architecture

Hexagonal from day one. The core is pure.

```
Lifeblood.Core              # Zero language dependencies. All analysis here.
  ├── Graph/                # Universal: Symbol, Edge, SemanticGraph
  ├── Analysis/             # Stateless analyzers
  ├── Rules/                # Architecture rule validation
  └── Ports/                # ICodeParser, IProjectDiscovery

Lifeblood.Adapters.CSharp   # Reference adapter. Wraps Roslyn.
Lifeblood.Reporters          # JSON, HTML, CI output
Lifeblood.CLI                # Entry point
```

**Core never knows which language it is analyzing.** It receives a graph and runs analysis. That is all it does.

---

## Two Ways to Add a Language

### Option A: Write a C# adapter

Implement `ICodeParser`. Get full integration with the CLI and analysis pipeline.

```csharp
public class PythonParser : ICodeParser
{
    public ParseResult Parse(string filePath, string sourceText) { ... }
}
```

### Option B: Write a parser in any language, output JSON

Write your parser in Python, Go, Rust, whatever feels natural. Output a JSON file conforming to `schemas/graph.schema.json`. The CLI reads it and runs all analysis.

```bash
# Your Python parser outputs the graph
python parse_project.py ./my-project > graph.json

# Lifeblood analyzes it
lifeblood analyze --graph graph.json --rules rules.json
```

**You do not need C# to add a language.** JSON is the universal adapter protocol.

---

## The Graph Model

Everything is symbols and edges. Language-agnostic.

**Symbols** (nodes):

| Kind | What it represents |
|------|-------------------|
| Module | Assembly, package, crate, npm package |
| File | Source file |
| Namespace | Namespace, package, module path |
| Type | Class, struct, interface, enum, trait |
| Method | Method, function, property accessor |
| Field | Field, constant, variable |

**Edges** (relationships):

| Kind | Meaning |
|------|---------|
| Contains | Parent holds child (file contains type, type contains method) |
| DependsOn | Import/using dependency |
| Implements | Type implements interface/trait |
| Inherits | Type extends base type |
| Calls | Method invokes method |
| References | Code uses a type (new, cast, generic argument, field type) |
| Overrides | Method overrides base method |

Language-specific metadata goes in `Symbol.Properties` (dictionary), never in new fields.

---

## Architecture Rules

Define rules in JSON. Same format as [X-Ray PRO](https://github.com/user-hash/LivingDocFramework/blob/main/docs/ROSLYN.md):

```json
{
  "rules": [
    { "source": "MyApp.Domain", "must_not_reference": "MyApp.Infrastructure" },
    { "source": "MyApp.Domain", "must_not_reference": "UnityEngine" },
    { "source": "*.Tests", "may_reference": "*" }
  ]
}
```

Violations are machine-readable: source file, target file, source namespace, target namespace, rule broken.

---

## Status

**Early stage.** Architecture defined, core model implemented, C# adapter in progress.

What exists:
- Graph model (Symbol, Edge, SemanticGraph)
- Port interfaces (ICodeParser, IProjectDiscovery)
- JSON schema for cross-language adapters
- Architecture documentation
- CLAUDE.md for AI-assisted development

What is needed:
- [ ] Core analysis algorithms (coupling, blast radius, dead code, tiers)
- [ ] C# adapter (Roslyn integration)
- [ ] CLI tool
- [ ] JSON reporter
- [ ] First community adapter (TypeScript? Python?)

---

## Why This Matters

Most AI coding tools work at the text level. They grep, they pattern-match, they guess. When they claim something is "unused," they miss indirect callers. When they refactor, they break boundaries they cannot see.

Roslyn proved that semantic understanding changes everything for C#/.NET. We spent a week chasing a rogue frequency in a DSP pipeline. Roslyn found it in seconds by tracing the actual signal flow. Three times an AI claimed code was dead. Three times it broke callers grep could not see.

Every language deserves this. Lifeblood is the framework that makes it possible.

---

## Getting Started

```bash
# Clone
git clone https://github.com/user-hash/Lifeblood.git
cd Lifeblood

# Build
dotnet build

# Run (once CLI is ready)
lifeblood analyze --project ./my-csharp-project --rules rules.json
```

---

## Contributing

The highest-value contributions right now:

1. **Language adapters.** Pick a language, implement `ICodeParser` or build a JSON-outputting parser.
2. **Analysis algorithms.** Port concepts from the [case study](https://github.com/user-hash/LivingDocFramework/blob/main/docs/CASE_STUDY.md).
3. **Testing.** Real-world codebases as test fixtures.

Read [CLAUDE.md](CLAUDE.md) first. It has all the invariants and architecture rules.

---

## Related

- [LivingDocFramework](https://github.com/user-hash/LivingDocFramework) — The methodology that led to this tool
- [Roslyn](https://github.com/dotnet/roslyn) — The C#/.NET compiler platform that inspired everything
- [DAWG](https://dawgtools.org) — The 400k LOC project where we proved these ideas

---

## License

AGPL v3
