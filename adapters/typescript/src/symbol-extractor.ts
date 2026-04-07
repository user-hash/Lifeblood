import * as ts from 'typescript';
import { GraphSymbol, Visibility, Evidence } from './types';

const SEMANTIC_EVIDENCE: Evidence = {
  kind: 'semantic',
  adapterName: 'TypeScript',
  confidence: 'high',
};

/**
 * Extracts Lifeblood symbols from a TypeScript source file using the type checker.
 */
export function extractSymbols(
  sourceFile: ts.SourceFile,
  checker: ts.TypeChecker,
  relPath: string,
  fileId: string,
): GraphSymbol[] {
  const symbols: GraphSymbol[] = [];

  function visit(node: ts.Node, parentId: string): void {
    if (ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node)) {
      const sym = node.name ? checker.getSymbolAtLocation(node.name) : undefined;
      if (!sym) return;

      const fqn = checker.getFullyQualifiedName(sym).replace(/^"[^"]*"\./, '');
      const typeId = `type:${fqn}`;
      const isAbstract = !!(ts.getCombinedModifierFlags(node) & ts.ModifierFlags.Abstract);

      symbols.push({
        id: typeId,
        name: sym.getName(),
        qualifiedName: fqn,
        kind: 'type',
        filePath: relPath,
        line: sourceFile.getLineAndCharacterOfPosition(node.getStart()).line + 1,
        parentId,
        visibility: getVisibility(node),
        isAbstract,
        properties: {
          typeKind: ts.isClassDeclaration(node) ? 'class' : 'interface',
        },
      });

      // Extract members
      node.members.forEach(member => visitMember(member, typeId, fqn));
    }

    if (ts.isEnumDeclaration(node)) {
      const sym = node.name ? checker.getSymbolAtLocation(node.name) : undefined;
      if (!sym) return;

      const fqn = checker.getFullyQualifiedName(sym).replace(/^"[^"]*"\./, '');
      symbols.push({
        id: `type:${fqn}`,
        name: sym.getName(),
        qualifiedName: fqn,
        kind: 'type',
        filePath: relPath,
        line: sourceFile.getLineAndCharacterOfPosition(node.getStart()).line + 1,
        parentId,
        visibility: getVisibility(node),
        properties: { typeKind: 'enum' },
      });
    }

    if (ts.isTypeAliasDeclaration(node)) {
      const sym = node.name ? checker.getSymbolAtLocation(node.name) : undefined;
      if (!sym) return;

      const fqn = checker.getFullyQualifiedName(sym).replace(/^"[^"]*"\./, '');
      symbols.push({
        id: `type:${fqn}`,
        name: sym.getName(),
        qualifiedName: fqn,
        kind: 'type',
        filePath: relPath,
        line: sourceFile.getLineAndCharacterOfPosition(node.getStart()).line + 1,
        parentId,
        visibility: getVisibility(node),
        properties: { typeKind: 'typeAlias' },
      });
    }

    if (ts.isFunctionDeclaration(node) && node.name) {
      const sym = checker.getSymbolAtLocation(node.name);
      if (!sym) return;

      const fqn = checker.getFullyQualifiedName(sym).replace(/^"[^"]*"\./, '');
      symbols.push({
        id: `method:${fqn}`,
        name: sym.getName(),
        qualifiedName: fqn,
        kind: 'method',
        filePath: relPath,
        line: sourceFile.getLineAndCharacterOfPosition(node.getStart()).line + 1,
        parentId,
        visibility: getVisibility(node),
      });
    }

    ts.forEachChild(node, child => {
      if (!ts.isClassDeclaration(child) && !ts.isInterfaceDeclaration(child)
          && !ts.isEnumDeclaration(child) && !ts.isTypeAliasDeclaration(child)
          && !ts.isFunctionDeclaration(child)) {
        visit(child, parentId);
      } else {
        visit(child, parentId);
      }
    });
  }

  function visitMember(member: ts.ClassElement | ts.TypeElement, typeId: string, typeFqn: string): void {
    const name = member.name ? member.name.getText(sourceFile) : undefined;
    if (!name) return;

    const line = sourceFile.getLineAndCharacterOfPosition(member.getStart()).line + 1;

    if (ts.isMethodDeclaration(member) || ts.isMethodSignature(member)) {
      symbols.push({
        id: `method:${typeFqn}.${name}`,
        name,
        qualifiedName: `${typeFqn}.${name}`,
        kind: 'method',
        filePath: relPath,
        line,
        parentId: typeId,
        visibility: getVisibility(member),
        isAbstract: !!(ts.getCombinedModifierFlags(member as ts.Declaration) & ts.ModifierFlags.Abstract),
        isStatic: !!(ts.getCombinedModifierFlags(member as ts.Declaration) & ts.ModifierFlags.Static),
      });
    }

    if (ts.isPropertyDeclaration(member) || ts.isPropertySignature(member)) {
      symbols.push({
        id: `field:${typeFqn}.${name}`,
        name,
        qualifiedName: `${typeFqn}.${name}`,
        kind: 'field',
        filePath: relPath,
        line,
        parentId: typeId,
        visibility: getVisibility(member),
        isStatic: !!(ts.getCombinedModifierFlags(member as ts.Declaration) & ts.ModifierFlags.Static),
        properties: { isProperty: 'true' },
      });
    }

    if (ts.isConstructorDeclaration(member)) {
      symbols.push({
        id: `method:${typeFqn}.constructor`,
        name: 'constructor',
        qualifiedName: `${typeFqn}.constructor`,
        kind: 'method',
        filePath: relPath,
        line,
        parentId: typeId,
      });
    }
  }

  visit(sourceFile, fileId);
  return symbols;
}

function getVisibility(node: ts.Node): Visibility {
  const flags = ts.getCombinedModifierFlags(node as ts.Declaration);
  if (flags & ts.ModifierFlags.Private) return 'private';
  if (flags & ts.ModifierFlags.Protected) return 'protected';
  // TypeScript has no 'internal' — export = public, no export = file-scoped
  if (ts.canHaveModifiers(node)) {
    const mods = ts.getModifiers(node);
    if (mods?.some(m => m.kind === ts.SyntaxKind.ExportKeyword)) return 'public';
  }
  return 'internal';
}
