# Golden Repo Test Fixtures

Small, self-contained codebases with known characteristics. Every adapter gets tested against these to verify it produces correct results.

## TinyHexagonalApp

A minimal hexagonal architecture app. 3 modules: Domain (pure), App, Infrastructure.

Expected results:
- Domain module classified as Pure
- 0 architecture violations with hexagonal rules
- Domain has 0 outgoing dependency edges to Infrastructure
- Coupling: Domain is most stable (instability near 0)

## CircularDependencyRepo

A project with intentional circular dependencies between modules.

Expected results:
- CircularDependencyDetector finds at least 1 cycle
- Cycle involves the known circular modules

## How to Add a Golden Repo

1. Create a minimal project in a subdirectory
2. Add a `expected.json` file describing what analysis should find
3. Add the repo to the adapter contract test suite
