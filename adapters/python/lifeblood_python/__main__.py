"""
Lifeblood Python adapter entry point.
Usage: python -m lifeblood_python <project-root>
Outputs graph.json to stdout conforming to schemas/graph.schema.json.
"""

import sys
import os
import json
from .analyzer import analyze_project

ADAPTER_VERSION = "1.0.0"


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: python -m lifeblood_python <project-root>", file=sys.stderr)
        print("  Analyzes a Python project and outputs a Lifeblood graph.json to stdout.", file=sys.stderr)
        sys.exit(1)

    project_root = os.path.abspath(sys.argv[1])
    if not os.path.isdir(project_root):
        print(f"Not a directory: {project_root}", file=sys.stderr)
        sys.exit(1)

    symbols, edges = analyze_project(project_root)

    # Deduplicate symbols by ID (last-write-wins)
    symbol_map = {}
    for sym in symbols:
        symbol_map[sym["id"]] = sym
    sorted_symbols = sorted(symbol_map.values(), key=lambda s: s["id"])

    # Deduplicate edges
    edge_set = set()
    deduped_edges = []
    for edge in edges:
        key = (edge["sourceId"], edge["targetId"], edge["kind"])
        if key not in edge_set:
            edge_set.add(key)
            deduped_edges.append(edge)

    # Synthesize Contains edges from parentId
    contains_pairs = {
        (e["sourceId"], e["targetId"])
        for e in deduped_edges
        if e["kind"] == "contains"
    }
    for sym in sorted_symbols:
        parent_id = sym.get("parentId")
        if parent_id and parent_id in symbol_map:
            pair = (parent_id, sym["id"])
            if pair not in contains_pairs:
                deduped_edges.append({
                    "sourceId": parent_id,
                    "targetId": sym["id"],
                    "kind": "contains",
                    "evidence": {
                        "kind": "inferred",
                        "adapterName": "Python",
                        "confidence": "proven",
                    },
                })
                contains_pairs.add(pair)

    sorted_edges = sorted(
        deduped_edges,
        key=lambda e: (e["sourceId"], e["targetId"], e["kind"]),
    )

    document = {
        "version": "1.0",
        "language": "python",
        "adapter": {
            "name": "Python",
            "version": ADAPTER_VERSION,
            "capabilities": {
                "discoverSymbols": True,
                "typeResolution": "bestEffort",
                "callResolution": "bestEffort",
                "implementationResolution": "bestEffort",
                "crossModuleReferences": "bestEffort",
                "overrideResolution": "none",
            },
        },
        "symbols": sorted_symbols,
        "edges": sorted_edges,
    }

    json.dump(document, sys.stdout, indent=2)
    print()  # trailing newline


if __name__ == "__main__":
    main()
