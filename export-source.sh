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

python tools/prepare-public-source.py --revision HEAD --output "$OUTPUT"
