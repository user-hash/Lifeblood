#!/bin/bash
# Export a clean source-only zip of the Lifeblood codebase.
# Excludes build outputs, vendored deps, git metadata, and planning docs.
#
# Usage: ./export-source.sh [output-path]
# Default output: Lifeblood-src.zip in the repo root

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT="${1:-$SCRIPT_DIR/Lifeblood-src.zip}"

cd "$SCRIPT_DIR"

# Use git archive for tracked files only (clean, no build artifacts)
git archive --format=zip --prefix=Lifeblood/ HEAD \
  -o "$OUTPUT"

SIZE=$(du -h "$OUTPUT" | cut -f1)
echo "Exported: $OUTPUT ($SIZE)"
echo "Contains only tracked source files from HEAD — no bin/, obj/, node_modules/, or artifacts."
