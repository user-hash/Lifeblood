# Architecture

Hexagonal framework. Two sides. Pure core.

```
LEFT SIDE                     CORE                    RIGHT SIDE
(Language Adapters)        (The Pipe)              (AI Connectors)

Roslyn (C#)       ──┐                           ┌──  MCP Server
TS Compiler       ──┤  ┌─────────────────────┐  ├──  Context Pack
go/types          ──┼→ │   Domain             │ →┤──  CLAUDE.md gen
Python ast+mypy   ──┤  │   (SemanticGraph)    │  ├──  LSP Bridge
rust-analyzer     ──┤  └─────────────────────┘  ├──  CLI / CI
Java JDT          ──┘         ↑                  └──  JSON / REST
                        Application
                      (ports + use cases)
```

## Three Layers of Truth

1. **Syntax truth.** What the parser proves from source text. Every adapter can provide this.
2. **Semantic truth.** What the compiler resolves. Type identity, overload resolution, call targets. Only compiler-grade adapters (Roslyn, TS Compiler) provide this at high confidence.
3. **Derived truth.** What Lifeblood computes from the graph. Coupling, blast radius, boundary violations. Pure core logic.

These layers must stay separate. Every edge carries Evidence saying which layer produced it and how confident the adapter is.

## Dependency Flow

```
CLI → Application → Domain (pure leaf, zero deps)
       ↑       ↑
   Adapters  Connectors
   (left)    (right)
```

Adapters and Connectors depend inward on Application ports. They never reference each other. Domain is always the leaf.

## Capability-Aware

Not all adapters are equal. Roslyn gives proven type resolution. Python ast gives best-effort guesses. Every adapter declares what it can do. Analysis results carry the confidence level. No fake authority.
