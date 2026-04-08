import * as ts from 'typescript';
import { GraphEdge, Evidence } from './types';

const SEMANTIC_EVIDENCE: Evidence = {
  kind: 'semantic',
  adapterName: 'TypeScript',
  confidence: 'proven',
};

/**
 * Extracts dependency edges from a TypeScript source file.
 * Only creates edges between source-defined symbols (no node_modules / lib).
 */
export function extractEdges(
  sourceFile: ts.SourceFile,
  checker: ts.TypeChecker,
  sourceFiles: Set<string>,
): GraphEdge[] {
  const edges: GraphEdge[] = [];
  const seen = new Set<string>();

  function addEdge(sourceId: string, targetId: string, kind: GraphEdge['kind']): void {
    const key = `${sourceId}|${targetId}|${kind}`;
    if (seen.has(key)) return;
    seen.add(key);
    edges.push({ sourceId, targetId, kind, evidence: SEMANTIC_EVIDENCE });
  }

  function isFromSource(symbol: ts.Symbol | undefined): boolean {
    if (!symbol) return false;
    const decls = symbol.getDeclarations();
    if (!decls || decls.length === 0) return false;
    return decls.some(d => sourceFiles.has(d.getSourceFile().fileName));
  }

  function getTypeFqn(symbol: ts.Symbol): string {
    return checker.getFullyQualifiedName(symbol).replace(/^"[^"]*"\./, '');
  }

  function getContainingTypeFqn(node: ts.Node): string | undefined {
    let current = node.parent;
    while (current) {
      if (ts.isClassDeclaration(current) || ts.isInterfaceDeclaration(current)) {
        if (current.name) {
          const sym = checker.getSymbolAtLocation(current.name);
          if (sym) return getTypeFqn(sym);
        }
      }
      current = current.parent;
    }
    return undefined;
  }

  function visit(node: ts.Node): void {
    // Heritage clauses: extends → Inherits, implements → Implements
    if (ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node)) {
      if (node.name) {
        const sym = checker.getSymbolAtLocation(node.name);
        if (sym) {
          const sourceFqn = getTypeFqn(sym);
          const sourceId = `type:${sourceFqn}`;

          if (node.heritageClauses) {
            for (const clause of node.heritageClauses) {
              for (const typeExpr of clause.types) {
                const targetType = checker.getTypeAtLocation(typeExpr);
                const targetSym = targetType.getSymbol();
                if (targetSym && isFromSource(targetSym)) {
                  const targetFqn = getTypeFqn(targetSym);
                  const targetId = `type:${targetFqn}`;
                  const edgeKind = clause.token === ts.SyntaxKind.ExtendsKeyword
                    ? 'inherits' as const
                    : 'implements' as const;
                  addEdge(sourceId, targetId, edgeKind);
                }
              }
            }
          }
        }
      }
    }

    // Call expressions → Calls edges
    if (ts.isCallExpression(node)) {
      const sig = checker.getResolvedSignature(node);
      const targetSym = sig?.declaration
        ? checker.getSymbolAtLocation(
            ts.isMethodDeclaration(sig.declaration) || ts.isFunctionDeclaration(sig.declaration)
              ? sig.declaration.name || sig.declaration
              : sig.declaration
          )
        : undefined;

      if (targetSym && isFromSource(targetSym)) {
        const containingType = getContainingTypeFqn(node);
        if (containingType) {
          // Find containing method
          let methodNode = node.parent;
          while (methodNode && !ts.isMethodDeclaration(methodNode) && !ts.isFunctionDeclaration(methodNode) && !ts.isConstructorDeclaration(methodNode)) {
            methodNode = methodNode.parent;
          }
          if (methodNode) {
            const methodName = ts.isConstructorDeclaration(methodNode)
              ? 'constructor'
              : (methodNode as ts.MethodDeclaration).name?.getText(sourceFile) || '?';
            const sourceId = `method:${containingType}.${methodName}`;
            const targetFqn = getTypeFqn(targetSym);
            addEdge(sourceId, `method:${targetFqn}`, 'calls');
          }
        }
      }
    }

    // Type references → References edges (type-level)
    if (ts.isTypeReferenceNode(node)) {
      const targetType = checker.getTypeAtLocation(node);
      const targetSym = targetType.getSymbol();
      if (targetSym && isFromSource(targetSym)) {
        const containingType = getContainingTypeFqn(node);
        if (containingType) {
          const targetFqn = getTypeFqn(targetSym);
          if (containingType !== targetFqn) {
            addEdge(`type:${containingType}`, `type:${targetFqn}`, 'references');
          }
        }
      }
    }

    // new X() → References edge to type
    if (ts.isNewExpression(node)) {
      const targetType = checker.getTypeAtLocation(node);
      const targetSym = targetType.getSymbol();
      if (targetSym && isFromSource(targetSym)) {
        const containingType = getContainingTypeFqn(node);
        if (containingType) {
          const targetFqn = getTypeFqn(targetSym);
          if (containingType !== targetFqn) {
            addEdge(`type:${containingType}`, `type:${targetFqn}`, 'references');
          }
        }
      }
    }

    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return edges;
}
