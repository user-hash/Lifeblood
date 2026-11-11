# C# Adapter Invariants

Canonical symbol IDs, the typed semantic-view handle, BCL ownership, and
csproj-driven compilation facts. Every rule on this page lives in or is
consumed by `Lifeblood.Adapters.CSharp`.

- **INV-REPEATED-CODE-001. Repeated executable bodies use one bounded left-side port over the leased retained syntax trees.** `IRepeatedCodeProvider` accepts explicit scope and hard input/output budgets. The C# adapter compares complete token-kind/spelling sequences after ignoring trivia, uses hashes only as buckets and confirms full sequence equality. Identifiers and literal spelling remain significant. Canonical owners use the existing symbol formatter and physical spans use the workspace source-path authority. Local functions and lambdas remain inside their enclosing body; linked physical bodies appear once under the first selected module. All grouping state dies with the request, cancellation is observed during scanning/hashing, and no graph/cache/compilation set is created or mutated. Counts describe observed scope and every exhausted budget is reported. Another retained profile, graph-only or historical compilation-free state is rejected. Syntax equality is advisory for semantic equivalence or deletion. Competing ownership remains the existing `lifeblood_authority_coverage` tool with caller-declared policy, composed through the existing snapshot-pinned batch. Pinned by `RepeatedCodeProviderTests` and `ToolHandlerTests` duplication cases under both canonical ID policies.

- **INV-EXTRACT-OPERATOR-BINDING-001. Operator declaration flags and call edges follow compiler binding.** `RoslynSymbolExtractor` preserves Roslyn `IsStatic` and `IsAbstract` for operator and conversion declarations. `RoslynEdgeExtractor` walks bound operation roots through the shared `RoslynOperationFacts` owner and emits calls for user-defined unary/binary/increment/compound operators, conversions and operator invocations, including implicit compound input/output conversions. Built-in operations without a bound method do not invent targets. Targets use the existing canonical method formatter, tracked-assembly policy and edge builder, preserving open-generic/metadata identity, first-occurrence provenance and profile dedup. Containing method ownership resolves one selected declaration, including operator/conversion/destructor bodies; local functions and lambdas retain the enclosing graph-addressable owner. Root discovery also retains independent attribute and parameter-default operations. Pinned by `CSharp14SemanticsTests` instance/checked/lifted/generic/extension/abstract/conversion/initializer/metadata and operation-root cases under both identity policies.

- **INV-SOURCE-EVIDENCE-001. Caller-selected lexical evidence streams through one bounded left-side port over the retained C# syntax trees and never becomes another source index or semantic base.** `ISourceEvidenceProvider.ScanSourceEvidence` accepts exact terms, optional module/file scope, selected `Comment` / `XmlDocumentation` / `StringLiteral` kinds, and a hard fact cap. `RoslynSourceEvidenceProvider` reuses the current retained profile's immutable syntax trees and semantic models, emits deterministic request-local facts with physical source spans and the nearest graph-addressable containing symbol, and reports `executionMode:"RetainedSyntaxTrees"`, input identity, truncation, and `additionalSemanticBaseCount:0`. Matching is ordinal case-insensitive substring policy supplied by the caller; the adapter never labels prose stale, invents synonyms, or treats lexical presence as enforcement. It does not scan generated, skipped, non-C#, or another define profile's inactive syntax and says so in `limitations[]`. The callback and all request-local symbol memoization die with the scan. No `EdgeKind`, graph count, compilation cache, package inventory, or invariant declaration authority changes. Pinned by `SourceEvidenceProviderTests` and the contract-audit end-to-end fixture.

## Canonical Symbol ID Determinism

Lifeblood symbol IDs must be byte-identical for the same underlying
method regardless of which compilation extracts it. Drift between two
instances of the same method (one source, one metadata; or one declared
in module A and one implementing the same interface in module B)
silently corrupts every downstream feature: `find_references`,
`dependants`, blast radius, the fuzzy short-name resolver, and every
cross-module tool that relies on string equality of canonical IDs.

- **INV-CANONICAL-001. Transitive-mode compilations receive the full transitive dependency closure, not just direct `ModuleInfo.Dependencies`.**

- **INV-MODULE-REFS-001. Compilation reference closure mirrors the build tool that owns the csproj.**

## Adapter Semantic View

Each language adapter publishes a typed read-only accessor for its loaded
semantic state. Tools that need read access (script host today; debuggers,
visualizers, custom linters tomorrow) consume the view by reference, never
threading raw fields through individual constructors.

- **INV-VIEW-001. Each language adapter publishes a typed semantic view.**
  The C# adapter publishes `Lifeblood.Adapters.CSharp.RoslynSemanticView`
  (`Compilations`, `Graph`, `ModuleDependencies`). Future language adapters
  publish their own view type using their language's semantic model.

- **INV-VIEW-002. Tools consume the view, not raw fields.** The script host
  (`RoslynCodeExecutor`) takes a `RoslynSemanticView` in its constructor.
  Future consumers (debuggers, visualizers, linters, REPLs) take the same
  view. NEVER thread raw `Compilations` / `Graph` / `ModuleDependencies`
  through individual tool constructors.

- **INV-VIEW-003. The view is constructed once per workspace load and
  shared by reference.** `GraphSession.Load` (full and incremental paths)
  builds the view once with three field assignments and passes it by
  reference to consumers. The view never holds locks, never caches anything
  beyond its three references, never mutates state. It is purely a typed
  handle. Sharing avoids accidental divergence between consumers' views of
  the same workspace.

## Operation Fact Boundary

- **INV-OPERATION-FACTS-001. Occurrence-level contract evidence streams through one language-neutral port and never changes the semantic graph.**

Calls retain their observed dispatch kind without promoting a declared interface,
virtual slot, delegate signature or unknown dynamic/function-pointer target into
a concrete runtime implementation. Dynamic and pointer invocations remain call
occurrences with null declaration targets. Local initialization and event add/remove
preserve source binding inputs; conditional receivers name their original value.
Opaque callable keys identify lexical nested bodies independently of graph owner
filters and declaration-schema projection. Nested parameter/local provenance uses
that owner scope. These keys are not graph lookup addresses. Deferred bodies keep
their anonymous/local-function control contexts; their lexical presence does not
establish execution by the enclosing method. All callable memoization remains
request-local under the existing operation-fact boundary.

Callable declaration occurrences are explicitly opted into the same stream,
selectors and fact budget. They distinguish an empty source body from a
declaration with no source body, retain deferred/parent identity and binding-error
observations, and carry the retained compiler text's stated checksum algorithm
and encoding. They do not claim fresh raw-file identity or native/PDB verification.
Secondary-profile query copying preserves that opt-in. Member and element access
placement separately records read and write, including both for compound,
increment and ref operations; alias and accessor effects remain unresolved.
`MethodEffectAnalyzer` uses existing graph routes only to select bounded candidate
owners, then aggregates this one stream request-locally. Only supported bound
direct/local call occurrences enter another callable body. Every indirect,
external, unavailable, binding-error and scope-exhaustion case stays explicit;
unentered deferred bodies never become execution by their enclosing method.
No absence of observed operations is a purity or runtime-effect proof.

Declaration facts may additionally project SHA-1/SHA-256 over the same retained
text. Their two-algorithm map is materialized once per selected tree, reused only
within that scan and never replaces the original checksum/encoding provenance.
An alternate algorithm may require re-encoding decoded text; lost raw encoding
or BOM information must remain a mismatch/limitation, not an inferred match.
Optional native source comparison reuses the existing capture importer and the
same operation stream. Artifact/build/debug identities remain caller-reported;
matching a digest never authenticates a binary/PDB or proves native execution.

## Compile Check Validation

Compile checks identify their executing compiler through the same factory used
by environment observation. The optional validation receipt is absent when
compilation did not start, preserves the selected diagnostic array and indexes
its attribution without copying payloads. Locationless errors have compilation
scope; an exact comparison with current binding diagnostics may identify an
emit-only phase, never edit ownership. Source placement remains distinct from
proof that a requested edit introduced a diagnostic. The retained binding
baseline is not an emit baseline. No added baseline emit, retained diagnostic
cache, target-build claim or implicit reference widening is permitted by this
receipt. Pinned by `CompilerValidationTests` and compile-check handler projections.

## BCL Ownership

Some csprojs ship their own base class library via `<Reference Include="netstandard|mscorlib|System.Runtime">` (Unity ships .NET Standard 2.1; .NET Framework / Mono ship mscorlib). Plain SDK-style csprojs (`<Project Sdk="Microsoft.NET.Sdk">`) don't. They rely on the host runtime BCL.

Lifeblood treats this as a discovered module fact: `Lifeblood.Application.Ports.Left.ModuleInfo.BclOwnership` is `BclOwnershipMode.HostProvided` or `BclOwnershipMode.ModuleProvided`. The decision is computed ONCE during `RoslynModuleDiscovery.ParseProject` and consumed by `ModuleCompilationBuilder.CreateCompilation`. Detection logic lives in exactly one place: `RoslynModuleDiscovery.ReferenceDeclaresBcl` plus its `ParseAssemblyIdentitySimpleName` and `IsBclSimpleName` helpers. The compilation builder reads the field and never re-derives.

- **INV-BCL-001. Single BCL per compilation.** Two BCLs causes CS0433 + CS0518 on every System type; semantic model becomes unusable (`GetSymbolInfo` returns null at every call site → `find_references`, `dependants`, call-graph extraction silently produce zero results).

- **INV-BCL-002. Module owns its BCL when its csproj declares one.** A `<Reference>` whose parsed assembly identity or HintPath basename matches `netstandard`, `mscorlib`, or `System.Runtime` is the authoritative declaration. Such modules MUST NOT also receive the host BCL bundle.

- **INV-BCL-003. Host BCL is the fallback for plain SDK-style projects.** Modules with no BCL-naming `<Reference>` receive `BclReferenceLoader.References.Value`.

- **INV-BCL-004. BCL ownership is decided at discovery time, single source of truth.** Detection logic in `RoslynModuleDiscovery`. `ModuleCompilationBuilder` reads the typed field — no filename sniffing, no re-derivation at the compilation layer.

- **INV-BCL-005. Incremental re-analyze respects csproj edits.** `AnalysisSnapshot.CsprojTimestamps` tracks csproj timestamps. A csproj-timestamp change forces re-discovery and recompile even if no .cs file changed. Without this, a csproj edit that flips BCL ownership silently re-introduces the double-BCL bug under incremental mode.

## Csproj-Driven Compilation Facts

- **INV-SDK-LANGUAGE-FACTS-001. SDK language defaults and imports are evaluated by the selected build tool, never inferred from the host Roslyn default or a target-framework table.** Discovery evaluates one SDK project batch in an isolated MSBuild process, stores the effective language setting on `ModuleInfo.LanguageVersion`, and includes actual imported files, SDK/global.json selection and effective-property receipts in the existing descriptor fingerprint. Explicit `default`, `latest` and `latestMajor` resolve through that SDK's Roslyn language primitive; explicit numeric and `preview` spellings retain their meaning. One module still selects the first effective target framework. Generated Unity/legacy descriptors retain their own language settings without SDK evaluation. Evaluation failure and unsupported language settings fail explicitly instead of publishing guessed compiler facts. Imported-content, input-membership or selection drift forces the existing policy-controlled full fallback before reuse of retained compilations. SDK projects/assemblies are not retained in the semantic host and no second semantic base is created. SDK child stdin is a null handle; Windows inheritance is restricted to an explicit handle allowlist. One 30-second deadline covers both stdout/stderr capture and exit, and timed-out or faulted live children are terminated as a complete process tree and reaped. `SdkEvaluationProcessTests` pins pipe draining, EOF, nonzero diagnostics and timeout cleanup. Pinned by `SdkLanguageFactsTests` and `CsprojCompilationFactsTests`.

Csproj is the source of truth for module-level compilation options. Each fact
flows through one path: discover at parse time → store on `ModuleInfo` →
consume at compilation time. The pattern is the contract. Every future
csproj-driven option follows the same shape.

- **INV-COMPFACT-ENVIRONMENT-001. Compilation-environment observation projects the exact retained compiler objects and existing descriptor/profile receipts, without diagnostics, source I/O, refresh or another compilation.** `ICompilationHost.GetCompilationEnvironment` requires one explicit file/module scope; the C# adapter uses the canonical fail-closed ownership resolver, reports actual compilation options and distinct parse-option groups, and bounds references/defines/features/diagnostic overrides with complete total and omitted counts. Compiler/runtime identity describes the observing host, never Unity's compiler or target runtime. `lifeblood_diagnose mode:environment` adds snapshot-owned module provenance and applicability, rejects other-profile requests rather than substituting retained options, and preserves the publication/envelope. Defaults keep the diagnostics lane unchanged. Reference rows describe inputs without forcing metadata binding or certifying their contents. Pinned by `CompilationEnvironmentTests` and environment-mode `ToolHandlerTests`.

- **INV-COMPFACT-001. Csproj is authoritative for module-level compilation
  options.** Shipped facts: `BclOwnership` (v2), `AllowUnsafeCode` (v4),
  `ImplicitUsings` (v0.6.4 — closes the implicit-global-usings extractor
  gap), `LangVersion` / `Nullable` warning level / `NoWarn` (FOLLOWUP-001..003,
  post-v0.7.3 — threaded into `CSharpParseOptions` /
  `CSharpCompilationOptions` so a csproj declaring `<LangVersion>13</LangVersion>`
  actually compiles under C# 13 instead of the host default), `DefineConstants`
  (BUG-2, post-v0.7.3 — preprocessor symbols flow into `ParseOptions` so
  `#if MY_FLAG` blocks survive parse instead of dead-code-eliminating).
  `CompilerFeatures` (runtime-async compatibility awareness) carries csproj
  `<Features>` name/value pairs into `CSharpParseOptions.WithFeatures` so
  experimental compiler switches are represented in the same parse-options
  seam as the user's build.
  Future additions (`Platform`, `WarningLevel`, etc.) follow the same
  shape with zero new incremental work per INV-COMPFACT-003.

- **INV-COMPFACT-002. Each compilation fact lives as a typed field on
  `ModuleInfo`.** Default value preserves pre-fix behavior. Set during
  `RoslynModuleDiscovery.ParseProject`. Consumed exactly once during
  `Internal.ModuleCompilationBuilder.CreateCompilation`. NEVER re-derive from
  the csproj at the compilation layer; NEVER sniff filenames as a substitute
  for declared options.

- **INV-COMPFACT-003. Csproj edits invalidate cached module facts.** The
  `AnalysisSnapshot.CsprojTimestamps` tracking added in v2 (INV-BCL-005)
  covers every compilation fact for free. `IncrementalAnalyze` rebuilds the
  entire `ModuleInfo` on csproj edit, not just one field. The next compilation
  fact added under this convention ships with zero new incremental work.

- **INV-COMPFACT-UNITY-PROJECT-TYPE-001. `<UnityProjectType>` is the descriptor-owned module-applicability fact for Unity profile analysis.**

- **INV-RUNTIME-ASYNC-COMPAT-001. Runtime Async starts as user-project
  compatibility metadata, not a Lifeblood server feature.** If an analyzed
  csproj declares `<Features>runtime-async=on</Features>` or another compiler
  feature switch, `RoslynModuleDiscovery.ParseProject` parses the semicolon-
  separated name/value pairs into `ModuleInfo.CompilerFeatures`.
  `DefineProfileApplier.WithProfileDefines` must preserve the field while
  applying profile-specific preprocessor symbols, and
  `ModuleCompilationBuilder.CreateCompilation` must pass it into
  `CSharpParseOptions.WithFeatures` for every real source tree. This keeps
  analyze, diagnose, and compile-check aligned with the user's project options
  before Lifeblood itself ever experiments with running under Runtime Async.
  Pinned by `CsprojCompilationFactsTests.Discovery_ReadsCompilerFeatures_FromCsproj`
  and `CsprojCompilationFactsTests.Compilation_ThreadsCompilerFeatures_IntoParseOptions`.

- **INV-SOURCEGEN-SERIAL-001. Framework source-generator execution is serialized and version-isolated at the adapter seam.** `SourceGeneratorRunner` is the only code path that loads analyzer DLLs and runs Roslyn generator drivers discovered from framework compilation facts. Loading and execution pass through the runner's process-wide gate. One compilation pass owns a collectible assembly context per exact analyzer-path inventory: Roslyn interfaces share host type identity, different inventories stay isolated, and modules sharing an inventory reuse loaded code while creating fresh generators and drivers. Standalone calls own a one-run scope. Pass disposal requests unload and clears loader references without a process-wide generator cache. Assembly streams close after loading so asynchronous unload does not lock analyzer files. Missing/corrupt dependencies, load failures, generator exceptions and error diagnostics reject the candidate explicitly with module context rather than publishing missing generated members as ordinary source errors. The gate is scoped to generators, not ordinary compilation/extraction. Pinned by `CsprojCompilationFactsTests` concurrent generator coverage and `SourceGeneratorIsolationTests` standalone/scoped mixed-framework orders, fresh instances, scope release and load/execution failures.

- **INV-SOURCEGEN-PATH-PROVENANCE-001. Source-generator hint paths are virtual identities and never resolve through the process working directory.**

- **INV-WORKSPACE-SOURCE-PATH-001. Every adapter-mounted compiler source has one workspace identity across compilation, graph, fingerprint, package, and incremental receipts.** `Internal.WorkspaceSourcePathMap` owns the default project mount plus explicit adapter mounts. Unity package discovery registers each descriptor-resolved physical package root at `Packages/{packageName}`; a local `file:` package outside the project therefore never leaks a `../` traversal path. `CaptureAnalysisInputs`, `ModuleCompilationBuilder` exclusion matching, `SyntaxTreePathIdentity`, `PackageSourceVisibilityReport`, `AnalysisSnapshot` fingerprints, incremental deletion, and `AcceptedChangeSet` projection consume that same map instead of independently calling `Path.GetRelativePath`. Unknown external sources remain explicit traversal identities and are rejected by the existing `AcceptedChangeSet` contract rather than silently invented. A fresh-session `incremental:true, allowFullFallback:true` request must complete after analyzing an external package, publish its graph under the logical package path, and return the same path in a detailed full-fallback receipt. Pinned by `PackageSourceVisibilityTests.FreshIncrementalFallback_ExternalFilePackageUsesOneLogicalSourceIdentity`, including its logical `excludePaths` negative case, plus `SourceGeneratedPathProvenanceTests` and `AcceptedChangeSetTests`.

- **INV-EXTRACT-PARALLEL-001. Per-tree Roslyn semantic extraction is bounded, isolated, and deterministically published through one adapter seam.**

## Compilation File Ownership

File-scoped compilation operations resolve one retained syntax tree before they
read diagnostics or replace source. The resolver is adapter-owned because path
identity and compilation membership are language-host facts; its result is a
language-neutral Domain receipt consumed by every public projection.

- **INV-COMPILATION-FILE-OWNERSHIP-001. Diagnose, compile-check and source-position binding consume one fail-closed compilation-file ownership resolver; no operation may select the first suffix match.** `Internal.CompilationFileOwnershipResolver` normalizes separators, applies operating-system case semantics, ranks exact normalized paths above boundary-safe suffix matches, and returns one of `Unique`, `Ambiguous`, `NotFound`, `ModuleNotFound`, or `NotInModule`. A unique result carries the owning module and syntax tree. An ambiguous result carries stable, ordinal module candidates and candidate tree paths but no selected module/tree; callers must pin `moduleName` or provide a more specific path. `DiagnosticsReport.FileOwnership` and `CompileCheckResult.FileOwnership` are the canonical Domain receipt; `ResolvedModule` and `CompileCheckFileResolution` remain additive compatibility projections. `lifeblood_diagnose` fails closed to an empty diagnostic/define set on non-unique file ownership instead of presenting another compilation as clean, while `lifeblood_compile_check` returns `LB0004` for ambiguity and never replaces a guessed tree. Pinned by `CompileCheckFileResolutionTests` (unique, missing, pinned miss, missing module, exact-path precedence, deterministic ambiguity), `DiagnosticEnvelopeDefinesTests`, and `ToolHandlerTests.Handle_DiagnoseAndCompileCheck_AmbiguousFileOwnershipFailsClosed`. Source-position binding returns a `SymbolAtPositionReport` from one typed core; the nullable SDK signature delegates to it. `PositionResolution` distinguishes `FileNotResolved`, `InvalidCoordinate`, `NoSymbol`, `Resolved`, and adapter `Unsupported`, carries the same ownership receipt, and exposes line/column failure and known bounds. Optional `moduleName` pins linked source. Columns address one-based UTF-16 code units, count tabs once and exclude line terminators; empty EOF lines contain no valid character coordinate. MCP preserves the original success fields, adds retained-profile provenance, and reports exact leased snapshot/generation on lookup failures. Pinned by `SymbolAtPositionOwnershipTests` and `McpCanonicalIdentityWireTests` (both negotiated schemas, stable linked candidates and module pins). Closes `LB-INTAKE-20260629-018`.

- **INV-UNITY-PACKAGE-SOURCE-001. Unity package source visibility is one C# adapter receipt derived from package descriptors, asmdefs, module membership, and analysis scope.**

## Diagnostic Parity Wall

Lifeblood's `diagnose` output on a workspace `dotnet build` calls clean
must itself be clean within a canonical parity diagnostic class.
Individual INVs below (`INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001`,
`INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001`, `INV-DIAGNOSTIC-IVT-PARITY-001`,
`INV-MODULE-REFS-001`) each close one historical regression class; the
wall ratchet pins all of them at one chokepoint so a future regression
that re-opens any of the classes fails the same test.

- **INV-DIAGNOSTIC-PARITY-001. Lifeblood diagnose on Lifeblood itself never fires parity-class diagnostics.** The canonical parity ID set is `{ CS0122, CS0117, CS0234, CS1503, CS1701, CS1702, CS1705, CS1729 }` — every member corresponds to one historical Lifeblood-side regression class an existing INV already prevents at the compilation seam (IVT propagation, reference closure, binding-redirect baseline, friend-assembly downstream). Lifeblood's own source tree is the fixture: every commit on `main` must pass `dotnet build` before reaching the test suite, so any parity-class diagnostic firing under Lifeblood's discovery + `ModuleCompilationBuilder` + `RoslynCompilationHost` pipeline against Lifeblood's own modules is by definition a Lifeblood-side false positive. Adding a new parity ID costs one string in the set; **removing one requires written justification + user approval (per `INV-WORK-008`) — relaxing the wall defeats the regression catch it exists to provide**. Pinned by `BuildDiagnosticParityTests.LifebloodSelfDiagnose_NeverFiresParityClassDiagnostics`.

## Reference Set Normalization

A workspace's reference set frequently carries multiple `MetadataReference`
entries that share an assembly simple-name but disagree on version (BCL ref
pack 8.0.x.x vs NuGet contract assembly 4.x.x.x is the canonical collision;
xunit / Microsoft.NET.Test.Sdk closures hit it deterministically). MSBuild
silently unifies these through its `<AutoUnify>true</AutoUnify>` default
before the compiler ever sees them. Roslyn does not.

- **INV-DIAGNOSTIC-IVT-PARITY-001. Csproj `<InternalsVisibleTo Include="X" />` items synthesize `[assembly: InternalsVisibleTo("X")]` attribute trees onto the producer compilation.** MSBuild's `GenerateAssemblyInfo` target writes the matching `[assembly: InternalsVisibleTo("X")]` attribute into `obj/<Tfm>/<AssemblyName>.AssemblyInfo.cs` during build; Lifeblood's SDK-style source scan skips `obj/` so that file never enters the compilation. Without surfacing the items the producer PE carries no friend-assembly metadata and every internal access from a friend module fires CS0122 — empirically 223 spurious findings on Lifeblood.Tests against the Adapters.CSharp surface while `dotnet build` was clean on the same compilation. The discovery seam (`RoslynModuleDiscovery.ParseProject`) parses `<InternalsVisibleTo>` items into `ModuleInfo.InternalsVisibleTo` (trim, distinct by ordinal, empty Includes drop). The compilation seam (`Internal.ModuleCompilationBuilder.CreateCompilation`) prepends a synthetic syntax tree at path `<InternalsVisibleTo>.cs` carrying one `[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("X")]` per declared target, parsed with the module's own `CSharpParseOptions` so downstream `AddSyntaxTrees` / `ReplaceSyntaxTree` calls do not throw "Inconsistent language versions" against modules declaring a non-default `<LangVersion>`. Modules that surface friend access through hand-authored `[assembly: InternalsVisibleTo]` attributes in source compile correctly regardless — their IVT lives on a `.cs` file that flows through normal source discovery. NEVER widen the SDK-style scan to include `obj/` as a substitute (the directory holds MSBuild-generated artifacts whose lifecycle is owned by `dotnet build`, not by the source tree). Pinned by `InternalsVisibleToParityTests` (discovery shape: single item, multi-item union + dedupe + empty-drop, no-items default; compilation shape: synthesized IVT attribute round-trips on the producer compilation's `Assembly.GetAttributes()`; end-to-end shape: a friend-named consumer reaching producer internals emits zero CS0122).

- **INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001. Top-level compilation reference sets handed to `CSharpCompilation.Create` are unified by `(simple-name, culture, public-key)` identity bucket, highest version wins within each bucket.**

- **INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001. The MSBuild csc-default NoWarn baseline (`1701`, `1702`) is unioned into every discovered module's suppression set.** `Microsoft.CSharp.CurrentVersion.targets` sets `<NoWarn>$(NoWarn);1701;1702</NoWarn>` for every csc invocation MSBuild produces, regardless of csproj schema. Both IDs are cross-module TypeRef binding-redirect warnings (`CS1701`: "Assuming assembly reference matches identity"; `CS1702`: same family with stricter version comparison). They fire per consuming type-ref whenever an upstream PE's recorded version of a transitively-shared assembly disagrees with the version currently loaded — exactly the shape that produced 7,642 spurious `CS1701` findings against `Lifeblood.Tests` (xunit.core baked against `System.Runtime 4.0.0.0` vs BCL ref pack `8.0.0.0`) while `dotnet build` was clean on the same compilation. The baseline is documented MSBuild behavior, not a Lifeblood opinion: not mirroring it ships diagnose responses with thousands of warnings the workspace's own CI does not see. The seam is `RoslynModuleDiscovery.MsbuildImplicitNoWarnBaseline` (private static, one source of truth) unioned into `noWarnIds` during `ParseProject` before the field lands on `ModuleInfo`. User-declared `<NoWarn>` entries union with the baseline (dedupe by `OrdinalIgnoreCase`); a module that genuinely needs `1701`/`1702` back uses MSBuild's own escape hatch — `<WarningsNotAsErrors>` or per-finding `#pragma warning restore` — exactly the same shape MSBuild offers. Pinned by `BuildDiagnosticParityTests.LifebloodSelfDiagnose_NeverFiresParityClassDiagnostics` (the wall ratchet) and via the existing `INV-COMPFACT-001..003` thread-through (`noWarnIds` → `WithSpecificDiagnosticOptions(ReportDiagnostic.Suppress)` in `ModuleCompilationBuilder.CreateCompilation`).

## Workspace File Discovery

Lifeblood discovers source files through generated project descriptors
(`.csproj` / asmdef membership), never a raw disk sweep. A `.cs` file on disk
that no descriptor lists is in no module and cannot enter the graph until the
descriptors regenerate — so the public tool surface must describe descriptor-
driven discovery honestly rather than promise pre-import disk pickup.

- **INV-UNITY-NEWFILE-DISCOVERY-HONESTY-001. The analyze tool description states descriptor-driven discovery, never raw pre-meta disk pickup, and compile_check surfaces the stale-descriptor case.**

## Reference search

The retained reference service owns one bounded request-local matching core.
Single-target operations delegate to it. At most 32 targets are admitted before
ordinary syntax is visited once; conversion targets share the existing operation
provider's one scan. Canonical-ID memoization is released with the request.
Physical occurrence identity and declaration kind remain per target. Work counters
scope syntax binding separately from target admission, containing-member lookup
and conversion traversal. `FindReferencesTests.MultiTarget_*` pins cross-module
parity, mixed conversion coverage, stable order, duplicates and admission bounds.
No retained reference mirror or second semantic base is allowed.

## Symbol ID Grammar (C# Adapter)

The following is the default legacy v1 grammar. The existing formatter also owns
an explicitly selected assembly-qualified v2 backend, pinned by
`VersionedCanonicalSymbolTests`. Its locator combines assembly identity, the
Roslyn declaring-type documentation ID (including enclosing arity), member
metadata name and a length-delimited signature with passing modes, method arity
and conversion return identity. Parameter type identity includes assembly owners
and nested constructed arguments; tuple labels and dynamic annotations normalize
to their underlying declaration types. Exact lookup uses the same formatter to
validate candidates. The MCP composition root selects v2 for one shared C#
publication; each connection selects v1 predecessor projection or v2 addresses
under [schema deprecation policy](../SCHEMA_DEPRECATION_POLICY.md). CLI and
library defaults remain v1 for compatibility.

Lifeblood symbol IDs are stable identifiers produced by `Lifeblood.Adapters.CSharp.Internal.SymbolIds`
and consumed by every read/write tool. The C# adapter format:

```
mod:AssemblyName
file:Relative/Forward/Slashed/Path.cs
ns:Fully.Qualified.Namespace
type:Fully.Qualified.TypeName
method:Fully.Qualified.TypeName.MethodName(Param1Type,Param2Type)
field:Fully.Qualified.TypeName.FieldName
property:Fully.Qualified.TypeName.PropertyName
property:Fully.Qualified.TypeName.this[Param1Type,Param2Type]   // indexer
```

**Method parameter signature is FULLY-QUALIFIED.** A method that takes one `MyApp.Item` parameter
appears as `method:MyApp.Service.Process(MyApp.Item)`. Never `method:MyApp.Service.Process(Item)`.

The exact format is pinned by `Internal.CanonicalSymbolFormat`. Every Roslyn-symbol ID builder
in the C# adapter routes through `CanonicalSymbolFormat.BuildSymbolId` or the specific
`BuildTypeId` / `BuildMethodId` / `BuildFieldId` / `BuildPropertyId` / `BuildEventId`
helper so source, metadata, declaration, and call-site-bound symbols ALWAYS produce the same ID.
Do not call `SymbolIds.Type` / `SymbolIds.Method` / `SymbolIds.Field` / `SymbolIds.Property`
outside `CanonicalSymbolFormat`; `ArchitectureInvariantTests.CSharpAdapter_RoslynSymbolIds_HaveSingleSourceOfTruth`
pins that seam.

**Constructors:** name part is `.ctor`. Example: `method:MyApp.Service..ctor(string,int)`.

**Operators / conversion operators:** name part is the Roslyn operator name (e.g. `op_Addition`,
`op_Implicit`).

**Special types:** C# aliases (`int`, `string`, `bool`, `void`, …) are used instead of their
`System.*` qualified names. The canonical format has `UseSpecialTypes` enabled.

**Nullability:** never reflected in IDs. `string?` and `string` share the same parameter signature
because they refer to the same underlying method symbol.

## Symbol Extraction Coverage

- **INV-EXTRACT-SYNTHESIZED-CTOR-001. Synthesized `.cctor` and parameterless `.ctor()` surfaces appear as graph Symbols whenever the containing type carries field or property initializers, so initializer-derived edges survive `GraphBuilder`'s source/target filter.**

- **INV-EXTRACT-DISPATCH-TABLE-COVERAGE-001. Dispatch-table-shaped static initializers emit graph edges for every method-group, enum-member, and field-reference cell through the same identifier / member-access walkers regular code paths use — no separate static-table-edge synthesizer is needed.**

- **INV-CANONICAL-ID-PARITY-001. Symbol-id parsing preserves the literal IL constructor names `.ctor` and `.cctor`; consumer-side lookup routes through `INamedTypeSymbol.Constructors` / `.StaticConstructors`.** Producer side (`SymbolIds.Method`) emits `method:NS.T..ctor()` and `method:NS.T..cctor()` for instance and static constructors — the leading dot is baked into `IMethodSymbol.Name`. Pre-fix, `RoslynWorkspaceManager.ParseSymbolId` called `nameOnly.Split('.')` on `"App.Service..ctor"` and produced `["App", "Service", "", "ctor"]`; the empty middle segment made `FindInCompilation` fail at `GetMembers("")` so `FindReferences("method:App.Service..ctor()")` returned zero refs. Fix: `SplitPreservingCtorNames` detects the `..cctor` / `..ctor` suffix (order matters — `.cctor` ends in `.ctor`), strips the suffix, splits the remaining container path on dots, and appends the literal `.cctor` / `.ctor` as the last part. `FindInCompilation`'s method arm uses `INamedTypeSymbol.Constructors` / `.StaticConstructors` for the special names (the documented surface — `GetMembers(".ctor")` works on modern Roslyn but the dedicated collections are the contract). Pinned by `SymbolIdCanonicalParityTests`: four invocation-site fixtures (`FindReferences_InstanceConstructor_ResolvesUnderDotCtorId`, `FindReferences_RegularMethod_StillResolvesPostCtorFix`, `FindReferences_NestedTypeMember_StillResolvesPostCtorFix`) plus three white-box parser ratchets (`ParseSymbolId_DotCtor`, `ParseSymbolId_DotCctor`, `ParseSymbolId_NestedTypeCtor`) and one end-to-end resolve ratchet (`ResolveSymbol_StaticConstructor_ResolvesUnderDotCctorId` — the static constructor has no explicit source invocation site so use-site `find_references` returns zero refs by design, but symbol resolution itself must succeed for rename / dependants paths). LB-TRACK-20260519-023 / F1c atom of the 2026-05-19 plan / 2026-05-15 correctness masterplan Stage 1.

- **INV-EXTRACT-CROSS-PARTIAL-RESOLUTION-001. Edge extraction across partial-class declarations resolves through the shared Roslyn `Compilation` regardless of which `SyntaxTree` is currently being walked.** When `Service` is declared in `Service.A.cs` (`private void Helper()`) and `Service.B.cs` (`public void Drive() { Helper(); }`), `extractor.Extract(modelB, treeB.GetRoot())` must emit `method:App.Service.Drive() -[Calls]-> method:App.Service.Helper()`. The Roslyn semantic model resolves the call natively because both trees join the same `CSharpCompilation`, but the contract is fragile: any future walker change that scopes resolution to the syntax tree currently being extracted would silently drop every cross-partial edge. Same invariant applies to cross-partial private field reads. Pinned by `ExtractEdges_CrossPartialPrivateMethodCall_EmitsCallsEdgeWithCanonicalId` (file A declares private method, file B partial invokes it) and `ExtractEdges_CrossPartialPrivateFieldReference_EmitsReferencesEdge` (file A declares private field, file B partial reads it). LB-TRACK-20260519-021-adjacent / F1b atom of the 2026-05-19 plan / 2026-05-15 correctness masterplan Stage 1.

- **INV-EXTRACT-EXTENSION-REDUCED-001. Extension-method canonical ids walk through `ReducedFrom` before `OriginalDefinition`, so instance-style invocations (`x.Foo()`) and static-style invocations (`Helper.Foo(x)`) emit graph edges with byte-identical ids.**

- **INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001. Graph edges target the source-declared `OriginalDefinition` of any `IMethodSymbol`, never an instantiated constructed-generic form.**

- **INV-EXTRACT-METHOD-GROUP-CANDIDATE-001. Method-group references that Roslyn surfaces through `SymbolInfo.CandidateSymbols` emit graph edges to the first candidate's source-declared definition.**

- **INV-EXTRACT-PROPERTY-READ-001. Bare-identifier sibling-member property and event reads emit a symbol-level `References` edge from the containing method to the property/event symbol.**

- **INV-EXTRACT-ENUMMEMBER-001. Every enum member is a first-class graph symbol.** `RoslynSymbolExtractor.ExtractEnum` walks `EnumDeclarationSyntax.Members` and emits one `Symbol` per member with `Kind = Field`, `Id = CanonicalSymbolFormat.BuildFieldId(memberSym)` (canonical wire form `field:enumFqn.memberName`), `ParentId = type:enumFqn`, `IsStatic = true`, `Properties[fieldKind] = "enumMember"`, `Properties[fieldType] = enumFqn`, `Properties[constantValue] = "<int-or-flags-bitfield>"`. Pre-fix, only the enum type entered the graph and three failure modes followed: (1) exact-ID lookups like `field:NS.Color.Red` missed, triggering resolver Rule 4 cross-type substitution (see `INV-RESOLVER-007`); (2) References edges to enum members were dropped by `GraphBuilder`'s dangling-edge filter, so `find_references`/`dependants` returned 0 hits for valid usages; (3) dead-code analysis could never observe enum-member usage. `RoslynEdgeExtractor`'s existing `IFieldSymbol` arm already emits the correct `field:` ID — the symbols just have to exist. Pinned by `RoslynExtractorTests.ExtractSymbols_EnumMembers_*` (six cases: emitted-as-Field, ID round-trip, implicit autoincrement constant value, explicit values, `[Flags]` bitfield, nested enum, xmldoc summary).

- **INV-ADAPTER-THIN-001. Adapter services that own a self-contained Roslyn-walk (enum coverage, static tables, future siblings) live in their own files in `Lifeblood.Adapters.CSharp`; `RoslynCompilationHost` is the orchestrator + composition root, not the implementation.** Pre-S8a, the host carried 1155 LOC including the entire `GetEnumCoverage` walker (~115 LOC method body + 65 LOC of `EnumRefClass` / `EnumMemberCounter` / `ClassifyEnumReferenceSite` helpers) inline. The host's `ICompilationHost` orchestration concerns (compilation lookup, symbol resolution, diagnostics dispatch) and the enum-coverage walker's concerns (per-member counter map, parent-syntax classification, dispatch-table predicate composition) had no shared mutable state — pure extraction opportunity. S8a moves the walker to `RoslynEnumCoverageService.cs` (own file, internal sealed class, takes the host by ctor for `Compilations` + `ResolveFromSource` + `BuildSymbolId` access). The host exposes those three seams as `internal` (Compilations property + ResolveFromSource method + static BuildSymbolId) — no public-API surface change. Host shrinks 1155 → 952 LOC; service is 247 LOC isolated. The 16 `EnumCoverageTests` fixtures (8 INV-ENUM-COVERAGE-001 + 8 INV-ENUM-COVERAGE-DISPATCH-TABLE-001) pin the behavior across the move; identical wire shape. Plan acceptance: "Largest C# files trend below 600 LOC over time" — S8a is the first cut, future atoms (S8b/S8c/...) extract `static_tables`, `find_references`, `diagnostics` siblings. LB-TRACK-20260519-028 / S8a atom of the 2026-05-19 plan.

- **INV-EXTRACT-IFACE-INHERIT-001. `interface : interface` emits `EdgeKind.Inherits`; `class : interface` and `struct : interface` emit `EdgeKind.Implements`.** Pre-F3c, `RoslynEdgeExtractor` emitted `EdgeKind.Implements` for every entry of `typeSymbol.Interfaces` regardless of source `TypeKind` — conflating "this class satisfies this contract" with "this interface extends this parent interface". The semantic conflation broke every downstream walker that needed transitive interface-inheritance closure: `port_health`'s F3b composite walk and `authority_report`'s implemented-interface collection both expected the inheritance relationship to live on `Inherits`, so on real C# graphs they silently reported composite interfaces as `empty` even when inherited contracts carried the real surface. F3c branches on `typeSymbol.TypeKind == TypeKind.Interface` inside the interface-loop and emits `Inherits` for interface sources, `Implements` for class/struct sources. The transitive walk lives in `Lifeblood.Domain.Graph.InterfaceInheritanceWalker.CollectTransitiveInherited` (shared by `port_health` + `authority_report`). The "directly satisfied interfaces" walk for authority-report semantic uses `CollectDirectInterfaceContracts` which branches on source `Properties["typeKind"]` (defaults to `Implements`-only when typeKind is absent — preserves hand-built test graphs and older JSON imports). Pre-F3c blind spot: F3b's synthetic-graph fixtures hand-built `Inherits` edges and passed despite the extractor never emitting that edge kind for iface→iface on real C# graphs. F3c real-graph regression pin closes that. Pinned by `RoslynExtractorTests.ExtractEdges_InterfaceExtendsInterface_EmitsInherits` (positive + negative pin: must emit `Inherits`, must NOT emit `Implements`), `ExtractEdges_ClassImplementsInterface_StillEmitsImplements_PostF3c`, `ExtractEdges_StructImplementsInterface_StillEmitsImplements_PostF3c` (backward-compat pins). F3c atom of the 2026-05-19 plan.

- **INV-EDGE-CALLSITE-001. Every expression-derived edge carries authoring provenance for its FIRST observed occurrence.** `RoslynEdgeExtractor.BuildCallSite` emits a `Lifeblood.Domain.Graph.CallSite` (`FilePath` / `Line` / `Column` / `EndLine` / `EndColumn` / `ContainingSymbolId`) for every edge sourced from a Roslyn `SyntaxNode` — invocation operations, member-access expressions, field references, property accesses, etc. `Edge.CallSite` is null for graph-derived edges with no single authoring location (module→module `DependsOn`, type→type `Inherits` without a surfaced clause node, type-level edges synthesised at graph-construction time). **First-occurrence semantics (load-bearing, interacts with `INV-STREAM-005`)**: the extractor dedups by `(sourceId, targetId, kind)` before emit, so multiple authoring expressions from the same source method to the same target of the same kind (e.g. two `obj.Method()` calls on different lines, or a typeof + new on the same target type within one body) collapse to ONE edge — the `CallSite` reflects the FIRST extracted occurrence, not every occurrence. Changing this would invert a long-standing graph invariant that downstream analysis (blast radius, cycle detection, dead-code) depends on; tools wanting per-occurrence data should query the source range via `lifeblood_compile_check` or `lifeblood_find_references` instead. The JSON graph importer / exporter round-trip the field so a cached graph preserves provenance across save / load. Downstream tools (`lifeblood_dependencies` / `lifeblood_dependants` wire surface, future tools) lift this directly without re-walking files — one call answers "where in source does X first depend on Y?". Pinned by `EdgeCallSiteTests.Extract_CallEdge_AttachesCallSiteWithFileLineColumn`, `Extract_FieldReference_AttachesCallSite`, `JsonGraph_RoundTripsCallSite`. Closes the field-report 2026-05-11 P1 CallSite ask.
