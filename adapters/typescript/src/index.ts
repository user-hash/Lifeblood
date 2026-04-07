#!/usr/bin/env node

import * as ts from 'typescript';
import * as path from 'path';
import * as fs from 'fs';
import { GraphDocument, GraphSymbol, GraphEdge } from './types';
import { extractSymbols } from './symbol-extractor';
import { extractEdges } from './edge-extractor';

const ADAPTER_VERSION = '1.0.0';

function main(): void {
  const projectRoot = process.argv[2];
  if (!projectRoot) {
    console.error('Usage: lifeblood-ts <project-root>');
    console.error('  Analyzes a TypeScript project and outputs a Lifeblood graph.json to stdout.');
    process.exit(1);
  }

  const absRoot = path.resolve(projectRoot);
  const tsconfigPath = ts.findConfigFile(absRoot, ts.sys.fileExists, 'tsconfig.json');
  if (!tsconfigPath) {
    console.error(`No tsconfig.json found in ${absRoot}`);
    process.exit(1);
  }

  // Parse tsconfig
  const configFile = ts.readConfigFile(tsconfigPath, ts.sys.readFile);
  if (configFile.error) {
    console.error(`Error reading tsconfig: ${configFile.error.messageText}`);
    process.exit(1);
  }

  const parsed = ts.parseJsonConfigFileContent(configFile.config, ts.sys, path.dirname(tsconfigPath));
  const program = ts.createProgram(parsed.fileNames, parsed.options);
  const checker = program.getTypeChecker();

  // Collect source file paths (exclude node_modules and declaration files)
  const sourceFiles = new Set<string>();
  for (const sf of program.getSourceFiles()) {
    if (!sf.isDeclarationFile && !sf.fileName.includes('node_modules')) {
      sourceFiles.add(sf.fileName);
    }
  }

  const symbols: GraphSymbol[] = [];
  const edges: GraphEdge[] = [];

  // Module symbol (the project itself)
  const moduleName = path.basename(absRoot);
  const moduleId = `mod:${moduleName}`;
  symbols.push({
    id: moduleId,
    name: moduleName,
    qualifiedName: moduleName,
    kind: 'module',
  });

  // Process each source file
  for (const sf of program.getSourceFiles()) {
    if (sf.isDeclarationFile || sf.fileName.includes('node_modules')) continue;

    const relPath = path.relative(absRoot, sf.fileName).replace(/\\/g, '/');
    const fileId = `file:${relPath}`;

    symbols.push({
      id: fileId,
      name: path.basename(sf.fileName),
      qualifiedName: `${moduleName}/${relPath}`,
      kind: 'file',
      filePath: relPath,
      parentId: moduleId,
    });

    // Extract symbols and edges
    const fileSymbols = extractSymbols(sf, checker, relPath, fileId);
    symbols.push(...fileSymbols);

    const fileEdges = extractEdges(sf, checker, sourceFiles);
    edges.push(...fileEdges);
  }

  // Deduplicate symbols by ID (last-write-wins, matching Lifeblood's policy)
  const symbolMap = new Map<string, GraphSymbol>();
  for (const sym of symbols) {
    symbolMap.set(sym.id, sym);
  }

  // Deduplicate edges
  const edgeSet = new Set<string>();
  const dedupedEdges: GraphEdge[] = [];
  for (const edge of edges) {
    const key = `${edge.sourceId}|${edge.targetId}|${edge.kind}`;
    if (!edgeSet.has(key)) {
      edgeSet.add(key);
      dedupedEdges.push(edge);
    }
  }

  // Sort deterministically (INV-PIPE-001)
  const sortedSymbols = [...symbolMap.values()].sort((a, b) => a.id.localeCompare(b.id));
  const sortedEdges = dedupedEdges.sort((a, b) =>
    a.sourceId.localeCompare(b.sourceId) || a.targetId.localeCompare(b.targetId) || a.kind.localeCompare(b.kind)
  );

  // Synthesize Contains edges from parentId
  const containsPairs = new Set(sortedEdges.filter(e => e.kind === 'contains').map(e => `${e.sourceId}|${e.targetId}`));
  for (const sym of sortedSymbols) {
    if (sym.parentId && symbolMap.has(sym.parentId)) {
      const key = `${sym.parentId}|${sym.id}`;
      if (!containsPairs.has(key)) {
        sortedEdges.push({
          sourceId: sym.parentId,
          targetId: sym.id,
          kind: 'contains',
          evidence: { kind: 'inferred', adapterName: 'TypeScript', confidence: 'proven' },
        });
        containsPairs.add(key);
      }
    }
  }

  // Re-sort after adding contains edges
  sortedEdges.sort((a, b) =>
    a.sourceId.localeCompare(b.sourceId) || a.targetId.localeCompare(b.targetId) || a.kind.localeCompare(b.kind)
  );

  const document: GraphDocument = {
    version: '1.0',
    language: 'typescript',
    adapter: {
      name: 'TypeScript',
      version: ADAPTER_VERSION,
      capabilities: {
        discoverSymbols: true,
        typeResolution: 'high',
        callResolution: 'high',
        implementationResolution: 'high',
        crossModuleReferences: 'high',
        overrideResolution: 'none',
      },
    },
    symbols: sortedSymbols,
    edges: sortedEdges,
  };

  console.log(JSON.stringify(document, null, 2));
}

main();
