# Golden Repo Test Fixtures

Small, self-contained codebases with known characteristics. Every adapter gets tested against these to verify it produces correct results.

## HexagonalApp

A minimal hexagonal architecture app. 3 layers: Domain (pure), Application, Infrastructure.

Expected results:
- Domain types have 0 outgoing dependency edges to Infrastructure
- Application depends only on Domain
- Infrastructure implements Domain interfaces
- 0 architecture violations with hexagonal rules
- CouplingAnalyzer: Domain types are most stable (instability near 0)
- TierClassifier: Domain = Pure, Application = Boundary, Infrastructure = Runtime

## CycleRepo

Two services with intentional circular dependencies (A→B→A).

Expected results:
- CircularDependencyDetector finds exactly 1 cycle
- Cycle contains both ServiceA and ServiceB
- BlastRadiusAnalyzer: changing either affects the other

## How to Add a Golden Repo

1. Create a minimal project in a subdirectory
2. Write C# source files with known dependency patterns
3. Add tests in `tests/Lifeblood.Tests/` that build a graph and verify expectations
4. Document expected results in this README
