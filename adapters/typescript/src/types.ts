/**
 * Lifeblood graph schema types.
 * Matches schemas/graph.schema.json exactly.
 */

export type SymbolKind = 'module' | 'file' | 'namespace' | 'type' | 'method' | 'field' | 'parameter';
export type EdgeKind = 'contains' | 'dependsOn' | 'implements' | 'inherits' | 'calls' | 'references' | 'overrides';
export type Visibility = 'public' | 'internal' | 'protected' | 'private';
export type EvidenceKind = 'syntax' | 'semantic' | 'inferred';
export type ConfidenceLevel = 'none' | 'bestEffort' | 'high' | 'proven';

export interface GraphSymbol {
  id: string;
  name: string;
  qualifiedName?: string;
  kind: SymbolKind;
  filePath?: string;
  line?: number;
  parentId?: string;
  visibility?: Visibility;
  isAbstract?: boolean;
  isStatic?: boolean;
  properties?: Record<string, string>;
}

export interface GraphEdge {
  sourceId: string;
  targetId: string;
  kind: EdgeKind;
  evidence?: Evidence;
  properties?: Record<string, string>;
}

export interface Evidence {
  kind: EvidenceKind;
  adapterName: string;
  sourceSpan?: string;
  confidence: ConfidenceLevel;
}

export interface GraphDocument {
  version: string;
  language: string;
  adapter: {
    name: string;
    version: string;
    capabilities: {
      discoverSymbols: boolean;
      typeResolution: ConfidenceLevel;
      callResolution: ConfidenceLevel;
      implementationResolution: ConfidenceLevel;
      crossModuleReferences: ConfidenceLevel;
      overrideResolution: ConfidenceLevel;
    };
  };
  symbols: GraphSymbol[];
  edges: GraphEdge[];
}
