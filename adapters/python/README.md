# Python Adapter

**Status:** Implemented. Standalone `ast`-based analyzer with zero external dependencies.

## Usage

```bash
cd adapters/python
python -m lifeblood_python /path/to/python-project > graph.json
```

Then validate with the CLI:

```bash
lifeblood analyze --graph graph.json
```

## What It Extracts

**Symbols:**
- Modules (project root)
- Files (all `.py` files, excluding `__pycache__`, `.venv`, etc.)
- Classes → `type` symbols with visibility, abstract detection
- Methods/functions → `method` symbols with parameter signatures
- Class-level annotated fields → `field` symbols

**Edges:**
- Class inheritance (`class Foo(Bar)` → Inherits)
- Import references (`from x import Y` → References)
- Type annotation references (`field: SomeType` → References)
- Constructor calls to known types → References

## Capability Profile

```json
{
  "discoverSymbols": true,
  "typeResolution": "bestEffort",
  "callResolution": "bestEffort",
  "implementationResolution": "bestEffort",
  "crossModuleReferences": "bestEffort",
  "overrideResolution": "none"
}
```

All capabilities are `bestEffort` because Python's `ast` module provides syntax-level analysis without full type inference. For `proven` resolution, a mypy-based adapter would be needed.

## Self-Analysis

The adapter analyzes itself:

```bash
python -m lifeblood_python . > graph.json
```

## Architecture

- `__main__.py` — Entry point, orchestration, deduplication, JSON output
- `analyzer.py` — File discovery, AST-based symbol and edge extraction
- No external dependencies — uses only Python's built-in `ast` and `os` modules
