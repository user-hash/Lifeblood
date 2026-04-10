# BCL Ownership Fix — Architectural Plan (v2)

**Status:** v2 DRAFT — corrections from external review folded in; awaiting approval before implementation
**Author:** Claude (working with Matic)
**Date:** 2026-04-10
**Severity:** CRITICAL — silently produces empty results from `find_references`,
  `dependants`, edge extraction, and call-graph analysis on any Unity / .NET
  Framework / Mono workspace
**Empirical evidence (the running hotfix dist proves the theory):**
  - Before: `Nebulae.BeatGrid.Audio` module → 29,523 compilation errors
    (CS0433 ambiguous types, CS0518 missing predefined types). All Unity modules
    affected the same way.
  - After (filename-sniff hotfix): same module → 3 unused-field warnings.
  - `find_references` for `method:Nebulae.BeatGrid.Audio.DSP.Voice.SetPatch(...)`:
    0 → 18 reference locations.
  - Total graph edges across DAWG (75 modules): 78,126 → 86,334 (+8,208 edges
    that the broken compilation was silently dropping).
  - Type-only references (e.g. `type:MidiDriver`) worked before because type
    resolution survives some binding chaos; method calls and member references
    require type binding to be clean and silently disappear without it.

**v1 → v2 changes (what the external review caught):**
  1. **Removed** `AnalysisConfig.ForceHostBcl` — placing a Roslyn/BCL-specific
     escape hatch on the generic application port leaks adapter policy through
     the wrong seam. v2 ships no override; if a malformed csproj ever requires
     one, we add it on the C# adapter constructor, not on the shared contract.
  2. **Promoted** the csproj-timestamp invalidation work from "open question /
     follow-up" to a **required step in this same fix**. Without it, a user
     who edits a csproj to add a BCL reference and then runs incremental
     re-analyze gets a stale `OwnsBcl` flag forever — silent re-introduction
     of the same bug. The reviewer correctly identified this as the hole the
     v1 plan left exposed.
  3. **Tightened** the BCL name matcher: Include attributes can carry
     `Name, Version=…, Culture=…, PublicKeyToken=…` metadata in legacy
     .NET Framework csprojs. v2 parses the assembly identity (split on
     comma, take first token, trim) and exact-matches the simple name.
     HintPath basename remains the secondary signal.
  4. **Upgraded** `bool OwnsBcl` to `enum BclOwnershipMode { HostProvided, ModuleProvided }`.
     The reviewer noted bool is fine but enum is the cleanest long-term
     shape; v2 takes the upgrade because the patch is the same size and the
     enum leaves a future `Mixed` / `Vendored` value possible without
     re-shaping the field.
  5. **Reframed** the `AnalysisSnapshot` audit from "verify if needed" to
     a mandatory implementation step — `AnalysisSnapshot.Modules` is
     already `ModuleInfo[]`, so the v2 schema change is immediately visible
     in cached state. Audit happens during implementation, not after.
  6. **Added** an explicit incremental + csproj-only-change regression test
     to the test plan.

**What the running hotfix did (now reverted from source):** in
  `ModuleCompilationBuilder.CreateCompilation`, sniffed `module.ExternalDllPaths`
  for the file names `netstandard.dll` / `mscorlib.dll` / `System.Runtime.dll`,
  and skipped the host `BclReferenceLoader.References.Value` bundle when any
  were present. The hotfix worked but is the wrong layer and the wrong
  detection mechanism. This document specifies the proper fix.

---

## 1. Invariant statement

**INV-BCL-001 — Single BCL per compilation.** Every `CSharpCompilation` produced
by `ModuleCompilationBuilder` MUST have references to exactly one base class
library — never zero, never two. A second BCL produces CS0433 / CS0518 errors
on every System type, which makes Roslyn's `GetSymbolInfo` return null at every
call site, which silently returns empty results from every read- and write-side
tool that walks call sites. (Type-only references survive because their
resolution is partially syntactic.)

**INV-BCL-002 — Module owns its BCL when its csproj declares one.** A module
that ships its own BCL via `<Reference Include="netstandard|mscorlib|System.Runtime">`
or HintPath references resolving to those file names (Unity, .NET Framework,
Mono, Xamarin, vendored runtimes, etc.) MUST NOT also receive the host
process's .NET 8 BCL bundle. The module's csproj is authoritative.

**INV-BCL-003 — Host BCL is the fallback for SDK-style projects.** A module
whose csproj is SDK-style and brings no BCL of its own (the common case for
modern .NET libraries — `<Project Sdk="Microsoft.NET.Sdk">` with a
`<TargetFramework>net*</TargetFramework>` and no HintPath BCL refs) MUST
receive the host BCL bundle so System types resolve.

**INV-BCL-004 — BCL ownership is decided at discovery time, single source of
truth.** The decision "does this module own its BCL" is a property of the
csproj. It MUST be computed once during `RoslynModuleDiscovery.ParseProject`
and stored on `ModuleInfo.BclOwnership`. `ModuleCompilationBuilder.CreateCompilation`
MUST read the field, never re-derive it from `ExternalDllPaths` strings or
re-parse the csproj. Detection logic lives in exactly one place.

**INV-BCL-005 — Incremental re-analyze respects csproj edits.** When a module's
csproj file timestamp changes since the last analysis, that module MUST be
re-discovered and recompiled even if no `.cs` file changed. Otherwise a
csproj edit that adds or removes a BCL reference (or any other discovery-
relevant element) silently produces a stale `BclOwnership` value and the
double-BCL bug returns under incremental mode.

These invariants are testable, documented, and prevent regression.

---

## 2. Why the running hotfix is the wrong design

The hotfix worked but had four real architectural problems:

1. **Wrong layer.** Detection happened in `CreateCompilation` per call. The
   responsibility belongs to discovery — a csproj is parsed once, its BCL
   ownership doesn't change between compilations of the same module, and
   `ModuleInfo` is the right home for this fact.

2. **Filename sniffing is fragile.** `Path.GetFileName(dll) == "netstandard.dll"`
   only catches HintPath shapes; it misses csprojs that use bare
   `<Reference Include="netstandard">` with no HintPath child. The correct
   PRIMARY signal is the csproj `<Reference Include>` value (parsed as an
   assembly identity), with HintPath basename as the SECONDARY backstop.

3. **No audit of stale state.** `AnalysisSnapshot.Modules` already stores
   `ModuleInfo[]`. The hotfix would have produced a stale snapshot under
   incremental re-analyze. v2 fixes this by tracking csproj timestamps and
   invalidating modules whose csproj changes (INV-BCL-005).

4. **Tests are not where they should live.** A regression test for this
   needs to construct a synthetic csproj that declares its own netstandard
   reference (with a real PE file behind it) AND verify that the resulting
   compilation has zero CS0433 / CS0518 diagnostics. The hotfix had no such
   test — it just hoped DAWG worked.

---

## 3. Detection mechanism — comparison

| Signal | Pros | Cons | Verdict |
|---|---|---|---|
| **A. csproj `<Reference Include>` simple name** (`Include="netstandard"`, `Include="mscorlib"`) | Authoritative. The csproj is telling us "I want netstandard as a reference". Survives renames of the underlying DLL. Matches Unity's modern shape. | None — the canonical signal. | **PRIMARY** signal. |
| **B. csproj `<Reference Include>` strong-name identity** (`Include="System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"`) | Same authority as A; legacy .NET Framework / NuGet-converted csprojs use this shape. | Need to parse the assembly identity (split on comma, trim, take first token) before matching. | **PRIMARY** (same matcher as A — A is a degenerate case of B with no metadata). |
| **C. HintPath filename** (`netstandard.dll`, `mscorlib.dll`) | Cheap. Already in `ExternalDllPaths`. Catches csprojs that use a non-BCL Include name but point HintPath at a BCL DLL. | Sniffs file names — fragile to vendored copies, capitalization, future shapes. | **SECONDARY** backstop signal. |
| **D. csproj `<TargetFrameworkVersion>` element** (Unity uses `v4.7.1` etc.) | Distinguishes .NET Framework from .NET Core/Standard/8. | Doesn't directly tell us "module brings its own BCL" — need to combine with reference inspection. SDK-style projects use `<TargetFramework>net8.0</TargetFramework>` instead. | **NOT USED**. Out of scope; YAGNI. |
| **E. PEReader.GetAssemblyDefinition().Name** | Authoritative even for vendored / renamed DLLs. | Opens every DLL to read PE metadata — adds I/O cost beyond the existing `IsNativeDll` check. | **NOT USED**. Filename + Include attribute give us enough. |

**Decision: combine signals A/B (Include attribute, parsed as assembly identity)
and C (HintPath basename) with OR.** A module is BCL-owning iff its csproj has
ANY `<Reference>` element where:
- the `Include` attribute's parsed assembly identity (first comma-separated
  token, case-insensitive trimmed) is one of `netstandard`, `mscorlib`,
  `System.Runtime`, OR
- the `<HintPath>` child resolves to a file whose name (with or without
  `.dll` suffix, case-insensitive) is `netstandard`, `mscorlib`, or
  `System.Runtime`.

The Include-attribute signal is primary because it's the most-authoritative
declaration the csproj makes. The HintPath signal is the backstop for
csprojs that omit Include or use a non-canonical Include name.

```csharp
// Pseudocode for the detection helper. Lives as a private static in
// RoslynModuleDiscovery — single home, testable in isolation.
private static bool ReferenceDeclaresBcl(XElement referenceElement)
{
    var include = referenceElement.Attribute("Include")?.Value ?? "";
    var simpleName = ParseAssemblyIdentitySimpleName(include);
    if (IsBclSimpleName(simpleName)) return true;

    var hintPath = referenceElement.Elements()
        .FirstOrDefault(c => c.Name.LocalName == "HintPath")?.Value;
    if (string.IsNullOrEmpty(hintPath)) return false;

    var hintBasename = Path.GetFileNameWithoutExtension(hintPath);
    return IsBclSimpleName(hintBasename);
}

// "netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=..." → "netstandard"
// "netstandard"                                                       → "netstandard"
// ""                                                                   → ""
private static string ParseAssemblyIdentitySimpleName(string includeValue)
{
    if (string.IsNullOrWhiteSpace(includeValue)) return "";
    var commaIdx = includeValue.IndexOf(',');
    var firstToken = commaIdx < 0 ? includeValue : includeValue.Substring(0, commaIdx);
    return firstToken.Trim();
}

private static bool IsBclSimpleName(string name) =>
    name.Equals("netstandard",    StringComparison.OrdinalIgnoreCase)
 || name.Equals("mscorlib",       StringComparison.OrdinalIgnoreCase)
 || name.Equals("System.Runtime", StringComparison.OrdinalIgnoreCase);
```

The three accepted names cover .NET Standard (Unity / cross-platform libs),
.NET Framework / Mono / Xamarin (mscorlib), and modern .NET (System.Runtime
as a reference assembly). `System.Private.CoreLib` is intentionally excluded
because it's the .NET 8 implementation assembly, not a reference assembly any
csproj declares.

---

## 4. Schema change — `ModuleInfo`

Add one typed enum field (NOT a bool, NOT a `Properties[…]` string entry):

```csharp
// Lifeblood.Application/Ports/Left/IModuleDiscovery.cs

/// <summary>
/// How a module obtains its base class library — host runtime or csproj-declared.
/// Decided at discovery time and consumed by the compilation builder.
/// See INV-BCL-001..005 in the BCL Ownership plan.
/// </summary>
public enum BclOwnershipMode
{
    /// <summary>
    /// Module relies on the host process's runtime BCL (the default for SDK-style
    /// .NET 8 projects with no HintPath BCL refs). The compilation builder
    /// MUST inject BclReferenceLoader.References.Value into this module.
    /// </summary>
    HostProvided = 0,

    /// <summary>
    /// Module ships its own BCL via csproj &lt;Reference Include="netstandard|mscorlib|System.Runtime"&gt;
    /// or via HintPath references resolving to those file names (Unity ships
    /// .NET Standard 2.1; .NET Framework ships mscorlib; Mono / Xamarin similar).
    /// The compilation builder MUST NOT inject the host BCL — doing so causes
    /// CS0433 / CS0518 diagnostics on every System type and silently breaks
    /// find_references / dependants / call-graph extraction (Roslyn returns null
    /// from GetSymbolInfo at every call site).
    /// </summary>
    ModuleProvided = 1,
}

public sealed class ModuleInfo
{
    // ... existing fields ...

    /// <summary>
    /// Where this module's base class library comes from. Decided once at
    /// discovery time by inspecting the csproj's &lt;Reference&gt; elements.
    /// Default <see cref="BclOwnershipMode.HostProvided"/> preserves existing
    /// behavior for plain SDK-style csprojs.
    /// </summary>
    public BclOwnershipMode BclOwnership { get; init; } = BclOwnershipMode.HostProvided;
}
```

This is **additive and backward-compatible**. Default `HostProvided` matches
existing behavior for every test fixture and golden repo currently in the
suite. Existing test fixtures that construct `ModuleInfo` literals don't break.

The enum (vs bool) buys us:
- Self-documenting at the call site (`module.BclOwnership == BclOwnershipMode.ModuleProvided`
  reads better than `module.OwnsBcl`).
- Future extensibility — `Mixed` (BCL-owning module that still wants
  augmentation) or `Vendored` (BCL declared but ships in the project tree)
  can be added without re-shaping the field.
- No more risk than a bool — both fit in a single byte.

---

## 5. Discovery change — `RoslynModuleDiscovery.ParseProject`

The csproj is already being walked for `<Reference>` elements to extract
`ExternalDllPaths`. Add one pass that sets `BclOwnership`:

```csharp
// Inside ParseProject, after externalDlls are computed.
// Walk all <Reference> elements once and ask each whether it declares a BCL.
bool ownsBcl = doc.Descendants()
    .Where(el => el.Name.LocalName == "Reference")
    .Any(ReferenceDeclaresBcl);

return new ModuleInfo
{
    Name = assemblyName,
    FilePaths = sourceFiles,
    Dependencies = deps,
    IsPure = isPure,
    ExternalDllPaths = externalDlls,
    BclOwnership = ownsBcl
        ? BclOwnershipMode.ModuleProvided
        : BclOwnershipMode.HostProvided,           // ← new
    Properties = new Dictionary<string, string>
    {
        ["projectFile"] = Path.GetRelativePath(projectRoot, csprojPath).Replace('\\', '/'),
    },
};
```

The helper `ReferenceDeclaresBcl` plus its supporting `ParseAssemblyIdentitySimpleName`
and `IsBclSimpleName` are private statics in `RoslynModuleDiscovery`. Total
addition: ~20 lines of code, all in one file, all in one layer.

---

## 6. Compilation change — `ModuleCompilationBuilder.CreateCompilation`

The compilation builder reads the field and acts on it. **No detection logic
in this file. No filename sniffing. No re-derivation from `ExternalDllPaths`.**

```csharp
// INV-BCL-001 / INV-BCL-002 / INV-BCL-003 / INV-BCL-004:
// BCL ownership is a discovered fact. Read the field, act on it.
// Do not re-derive from ExternalDllPaths — that's the discovery layer's job.
var references = module.BclOwnership == BclOwnershipMode.ModuleProvided
    ? new List<MetadataReference>()
    : new List<MetadataReference>(BclReferenceLoader.References.Value);

references.AddRange(_nuget.Resolve(module, projectRoot, _refCache));
references.AddRange(dependencyRefs);

// External DLLs (Unity engine modules + the module's own BCL stubs when
// applicable) are loaded the same way regardless of BclOwnership — they were
// already declared in the csproj and discovery already collected them.
foreach (var dllPath in module.ExternalDllPaths)
{
    try
    {
        if (!BclReferenceLoader.IsNativeDll(dllPath))
            references.Add(_refCache.GetOrCreate(dllPath));
    }
    catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { }
}
```

The change is two lines plus a comment block. The compilation builder is
ignorant of detection rules; it consumes a typed field.

---

## 7. Override mechanism — DELIBERATELY DEFERRED

v1 proposed `AnalysisConfig.ForceHostBcl`. **v2 ships no override** because:

1. The reviewer correctly observed that placing a Roslyn/BCL-specific knob on
   the generic application port `AnalysisConfig` leaks adapter policy through
   the wrong seam. `AnalysisConfig` is the contract that ALL analyzer
   implementations (current and future) must honor. A property called
   `ForceHostBcl` is meaningless to a hypothetical Python or Rust analyzer
   adapter.
2. **YAGNI** — no real-world csproj has yet been observed that needs this
   override. DAWG, Lifeblood self-analyze, and the WriteSideApp golden repo
   all work correctly with the discovery-based decision.
3. If a malformed csproj is found that needs an override later, the **right
   home** is a constructor parameter on `RoslynWorkspaceAnalyzer` (the
   adapter), e.g.
   `new RoslynWorkspaceAnalyzer(fs, new RoslynAnalyzerOptions { ForceHostBcl = true })`.
   That keeps the override adapter-scoped and out of the application port.
   This is a v3 concern, NOT a v2 concern.

v2 ships zero override. The fix is correct for every csproj shape we've
verified empirically. If a future shape breaks, we add the option in the
right layer at that time.

---

## 8. Incremental re-analyze fix — csproj timestamp tracking (REQUIRED in v2)

**This is the hole the v1 plan left exposed and the reviewer correctly
flagged. It ships in the SAME PR as the rest of the BCL fix.**

### Problem

`RoslynWorkspaceAnalyzer.IncrementalAnalyze` (line 200-220) only walks
`module.FilePaths` filtered to `.cs` files and only stores `.cs` file
timestamps in `_snapshot.FileTimestamps`. A user who edits a csproj to add
or remove a `<Reference Include="netstandard">` element does NOT trigger
re-discovery of that module. The cached `ModuleInfo.BclOwnership` value
stays whatever it was at first analyze, and the double-BCL bug silently
returns under incremental mode.

### Fix

Track csproj file timestamps in `AnalysisSnapshot` alongside .cs file
timestamps. In `IncrementalAnalyze`, before the .cs file scan, check
each module's csproj timestamp. If it changed, force re-discovery for
that module (and recompile it).

#### 8.1 Schema addition to `AnalysisSnapshot`

```csharp
// Lifeblood.Adapters.CSharp/Internal/AnalysisSnapshot.cs

internal sealed class AnalysisSnapshot
{
    // ... existing fields ...

    /// <summary>
    /// Absolute csproj file path → last-write-time-UTC at analysis time.
    /// Tracked separately from FileTimestamps (which only stores .cs files)
    /// because csproj edits change discovered module facts (BclOwnership,
    /// ExternalDllPaths, Dependencies) and require re-discovery, not just
    /// re-extraction.
    /// </summary>
    public Dictionary<string, DateTime> CsprojTimestamps { get; }
        = new(StringComparer.OrdinalIgnoreCase);
}
```

#### 8.2 Population during full `AnalyzeWorkspace`

After modules are discovered, record each csproj's timestamp:

```csharp
// In RoslynWorkspaceAnalyzer.AnalyzeWorkspace, after discovery + before
// the per-module compilation loop:
foreach (var module in modules)
{
    if (module.Properties.TryGetValue("projectFile", out var relCsproj))
    {
        var csprojAbs = Path.GetFullPath(Path.Combine(projectRoot, relCsproj));
        if (_fs.FileExists(csprojAbs))
            snapshot.CsprojTimestamps[csprojAbs] = _fs.GetLastWriteTimeUtc(csprojAbs);
    }
}
```

The csproj path is already exposed via `ModuleInfo.Properties["projectFile"]`
(set in `RoslynModuleDiscovery.ParseProject` line 161). No new discovery
field needed for this — the existing relative path is enough.

#### 8.3 Check during `IncrementalAnalyze`

Before the existing `.cs` file timestamp loop, run a csproj loop that
unions changed-csproj modules into `changedModules`:

```csharp
// In RoslynWorkspaceAnalyzer.IncrementalAnalyze, immediately after the
// "if module set changed → full re-analyze" check.

// Detect csproj edits. A csproj change rewrites discovered module facts
// (BclOwnership, dependencies, external DLLs) so the module must be
// fully re-discovered AND recompiled, not just re-extracted.
var csprojChanged = new HashSet<string>(StringComparer.Ordinal); // module names
foreach (var module in currentModules)
{
    if (!module.Properties.TryGetValue("projectFile", out var relCsproj)) continue;
    var csprojAbs = Path.GetFullPath(Path.Combine(projectRoot, relCsproj));
    if (!_fs.FileExists(csprojAbs)) continue;

    var currentTs = _fs.GetLastWriteTimeUtc(csprojAbs);
    if (_snapshot.CsprojTimestamps.TryGetValue(csprojAbs, out var prevTs)
        && currentTs == prevTs) continue;

    csprojChanged.Add(module.Name);
    _snapshot.CsprojTimestamps[csprojAbs] = currentTs;
}

// Mark every .cs file in csproj-changed modules as changed so the existing
// downstream pipeline picks them up uniformly.
foreach (var module in currentModules.Where(m => csprojChanged.Contains(m.Name)))
{
    foreach (var filePath in module.FilePaths.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
    {
        if (_fs.FileExists(filePath))
            changedFiles.Add(filePath);
    }
    changedModules.Add(module.Name);
}
```

This sits BEFORE the existing `.cs` file timestamp loop. The .cs loop then
adds any .cs-only changes on top. The downstream code path (recompilation
of `changedModules`, partial extraction replacement) doesn't need to change
— it already iterates `changedFiles` and `changedModules`.

The newly-discovered `currentModules[i]` already has the fresh `BclOwnership`
value because `IncrementalAnalyze` already calls `_discovery.DiscoverModules`
at line 188. The fix just makes sure the recompilation picks up the new
discovery.

### What this fix does NOT cover

- A csproj edit that ADDS or REMOVES a module (changes the module set) is
  already handled by the existing "if module set changed → full re-analyze"
  branch at line 193-198.
- A NuGet `project.assets.json` change (e.g. user ran `dotnet restore`
  with new package versions) is NOT detected. Out of scope for v2; the
  reviewer didn't flag it, and a full re-analyze fixes it.
- A change to a NESTED `.props` / `Directory.Build.props` file is NOT
  detected. Out of scope; full re-analyze fixes it.

These are documented limits. They are strictly less severe than the BCL
ownership hole because they don't silently corrupt the semantic model.

---

## 9. Test plan

The fix is verified at four layers, each with a concrete contract test:

### 9.1 Discovery layer — `HardeningTests.cs`

Four new tests in the existing `HardeningTests` class:

```csharp
[Fact]
public void RoslynModuleDiscovery_DetectsBclOwnership_FromBareReferenceInclude() {
    // <Reference Include="netstandard"> with no HintPath → ModuleProvided
}

[Fact]
public void RoslynModuleDiscovery_DetectsBclOwnership_FromStrongNameReferenceInclude() {
    // <Reference Include="netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51"/>
    // → ModuleProvided  (proves the assembly-identity parser handles the legacy shape)
}

[Fact]
public void RoslynModuleDiscovery_DetectsBclOwnership_FromHintPathFilenameOnly() {
    // <Reference Include="SomeOddName"><HintPath>...netstandard.dll</HintPath></Reference>
    // → ModuleProvided  (proves the HintPath fallback works when Include is unhelpful)
}

[Fact]
public void RoslynModuleDiscovery_PlainSdkProject_ReportsHostProvided() {
    // <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>
    // with no HintPath references → HostProvided  (golden WriteSideApp shape)
}
```

### 9.2 Compilation layer — new `BclOwnershipCompilationTests.cs`

Two new tests verifying the compilation builder's behavior:

```csharp
[Fact]
public void CreateCompilation_ModuleProvided_ProducesNoBclConflictDiagnostics()
{
    // Build a synthetic two-module workspace where Module A's csproj declares
    // <Reference Include="netstandard"> with HintPath pointing at the host
    // runtime's netstandard.dll. Compile via the real ModuleCompilationBuilder
    // path (NOT a stub). Assert:
    //   - Compilation produces zero CS0433 (ambiguous type)
    //   - Compilation produces zero CS0518 (missing predefined type)
    //   - GetSymbolInfo on a method-call expression returns a non-null symbol
    //     (proves the semantic model is healthy at the call-graph layer, not
    //     just syntactically valid).
}

[Fact]
public void CreateCompilation_HostProvided_StillReceivesHostBcl()
{
    // Plain net8.0 SDK-style csproj (no HintPath refs). BclOwnership = HostProvided.
    // Verify compilation references contain BclReferenceLoader.References values
    // and source code resolves System.Object correctly.
}
```

### 9.3 Incremental re-analyze layer — `IncrementalAnalyzeTests.cs`

Two new tests covering the csproj-timestamp invalidation (INV-BCL-005):

```csharp
[Fact]
public void IncrementalAnalyze_CsprojEdit_TriggersRediscoveryAndRecompile()
{
    // 1. Write a synthetic two-module project with Module A as plain SDK
    //    (BclOwnership = HostProvided).
    // 2. Run AnalyzeWorkspace. Verify graph contains expected symbols.
    // 3. Edit Module A's csproj to add <Reference Include="netstandard">
    //    and a HintPath. Touch the csproj file timestamp.
    // 4. Run IncrementalAnalyze.
    // 5. Verify:
    //    - Module A is in the recompiled set (changedFileCount > 0).
    //    - The recompiled Module A's BclOwnership is now ModuleProvided.
    //    - The compilation has zero CS0433 / CS0518 diagnostics.
}

[Fact]
public void IncrementalAnalyze_CsprojUnchanged_DoesNotRecompileModule()
{
    // Negative test. Touch only a .cs file timestamp. Verify the csproj-timestamp
    // path does NOT mark the module as changed for csproj reasons (the .cs path
    // still does, but for a different reason). Proves we're not over-invalidating.
}
```

### 9.4 Integration layer — extend `WriteSideIntegrationTests.cs`

The existing WriteSideApp golden-repo test continues to assert
`FindReferences` works for the SDK-style happy path. **No new on-disk
golden repo is needed.**

A NEW synthetic two-module fixture in
`tests/Lifeblood.Tests/FindReferencesCrossModuleTests.cs` (the file I
created during Finding B work) constructs a Unity-flavored
BCL-owning two-module setup in memory and asserts:
1. `FindReferences` on a struct method in Module A (BCL-owning) finds the
   call site in Module B's `voices[i].Method(patch)` shape.
2. `RoslynCompilationHost.GetDiagnostics(moduleA)` returns zero CS0433 /
   CS0518 errors.

### 9.5 Regression matrix

| Project shape | BclOwnership | Host BCL added | Test |
|---|---|---|---|
| Plain net8.0 SDK (Lifeblood self, golden WriteSideApp) | HostProvided | yes | existing WriteSideIntegration + new discovery test |
| Unity csproj `<Reference Include="netstandard">` (DAWG Audio.DSP) | ModuleProvided | no | new discovery test 1 |
| .NET Framework `<Reference Include="netstandard, Version=2.1.0.0, ...">` | ModuleProvided | no | new discovery test 2 |
| HintPath-only (`Include="X"`, `<HintPath>...netstandard.dll</HintPath>`) | ModuleProvided | no | new discovery test 3 |
| .NET Framework v4.x `<Reference Include="mscorlib">` | ModuleProvided | no | (covered by test 1 — same matcher) |
| Csproj edit adds Reference Include — incremental | HostProvided → ModuleProvided | no after edit | new incremental test 1 |
| Csproj unchanged + .cs edit — incremental | unchanged | unchanged | new incremental test 2 |

All seven rows have an explicit test. None of them are DAWG-specific.

---

## 10. Backward compatibility

- `ModuleInfo.BclOwnership` defaults to `HostProvided` — existing test fixtures
  that construct `ModuleInfo` literals continue to compile and behave as before.
- `BclOwnershipMode` is a new enum in the application port. It's additive;
  no existing code consumes it.
- The 301 existing tests in `Lifeblood.Tests` should all still pass after
  the fix because none of them exercise a BCL-owning scenario today. The new
  tests in section 9 are the safety net for the new behavior.
- `AnalysisSnapshot.Modules` is `ModuleInfo[]` (line 19). The schema change
  IS immediately visible in cached state. **In-memory state only** — Lifeblood
  does not persist `AnalysisSnapshot` to disk between MCP server restarts as
  far as discovery shows; verify during implementation. If a snapshot
  persistence layer DOES exist somewhere I missed, the schema change is
  additive (new field with sensible default) and reading an old snapshot
  produces `BclOwnership = HostProvided` (the default), which is the
  pre-fix behavior — graceful degradation, not crash.
- `AnalysisSnapshot.CsprojTimestamps` is a new dict on the snapshot. Same
  in-memory backward-compat story — additive, defaults to empty.

---

## 11. Rollout steps (post-approval)

**Each step is a separate atomic commit. Full suite must pass between commits.**

1. **Application port** — add `BclOwnershipMode` enum and `ModuleInfo.BclOwnership`
   field to `IModuleDiscovery.cs`. Run full suite — should still be 301 passing
   because nothing reads the field yet.
2. **Discovery** — add `IsBclSimpleName` / `ParseAssemblyIdentitySimpleName` /
   `ReferenceDeclaresBcl` private helpers and `BclOwnership` computation in
   `RoslynModuleDiscovery.ParseProject`. Run full suite — should still be 301.
3. **Discovery tests** — add the four `HardeningTests` cases from §9.1.
   Run full suite — should be 305.
4. **Compilation** — `ModuleCompilationBuilder.CreateCompilation` reads the
   field, gates host BCL injection. Two-line change + comment block. Run
   full suite — should still be 305 because no test exercises the
   ModuleProvided path yet.
5. **Compilation tests** — add `BclOwnershipCompilationTests.cs` with the
   two cases from §9.2. Run full suite — should be 307.
6. **Incremental snapshot** — add `CsprojTimestamps` dict to
   `AnalysisSnapshot`. Run full suite — should still be 307 because nothing
   populates it yet.
7. **Incremental population** — `RoslynWorkspaceAnalyzer.AnalyzeWorkspace`
   populates `CsprojTimestamps` after discovery. Run full suite — still 307.
8. **Incremental detection** — `IncrementalAnalyze` checks csproj timestamps
   before the .cs loop. Run full suite — still 307.
9. **Incremental tests** — add the two cases from §9.3. Run full suite —
   should be 309.
10. **Integration test** — add the BCL-owning synthetic fixture to
    `FindReferencesCrossModuleTests.cs` from §9.4. Run full suite — should
    be 310.
11. **Audit** — run `grep -rn "ToDisplayString\|BclReferenceLoader.References"
    src/Lifeblood.Adapters.CSharp/` and verify no other code path still
    sniffs filenames or re-derives BCL ownership.
12. **Publish to dist** — kill any locked .NET host processes, then
    `dotnet publish src/Lifeblood.Server.Mcp -c Release -o dist`.
13. **Verify against DAWG** — re-run the diagnostic queries:
    - `lifeblood_analyze projectPath="D:/Projekti/DAWG" incremental=false`
    - `lifeblood_diagnose moduleName="Nebulae.BeatGrid.Audio"` → expect ≤ 5
      diagnostics, all warnings
    - `lifeblood_find_references symbolId="method:Nebulae.BeatGrid.Audio.DSP.Voice.SetPatch(Nebulae.BeatGrid.Audio.DSP.VoicePatch)"`
      → expect ≥ 18 results including `PatchPublisher.cs:106`
14. **Verify incremental against DAWG** — touch `Nebulae.BeatGrid.Audio.csproj`
    and run `lifeblood_analyze incremental=true`. Expect that module to
    appear in the recompiled set (changedFileCount > 0).
15. **CHANGELOG entry** — INV-BCL-001 through 005 documented; behavior
    change noted.
16. **Update Lifeblood `CLAUDE.md`** — add the five invariants and a brief
    paragraph explaining BCL ownership and incremental csproj invalidation.

---

## 12. Out of scope (deliberately)

- **`AnalysisConfig.ForceHostBcl`.** Removed from v2 — adapter policy on a
  generic port. If ever needed, add it on `RoslynWorkspaceAnalyzer`'s
  constructor as a v3 concern.
- **TargetFramework parsing.** Lifeblood doesn't care about which framework
  the code targets, only about whether the BCL is module-owned. YAGNI.
- **PEReader-based AssemblyName detection.** Cheap heuristics work; we don't
  need to open every DLL.
- **Multi-target csprojs (`<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>`).**
  Lifeblood today picks the first target framework's references from
  `project.assets.json`. Out of scope.
- **NuGet `project.assets.json` incremental invalidation.** A separate hole;
  the reviewer didn't flag it; full re-analyze fixes it. Documented as a
  known limitation.
- **`Directory.Build.props` change detection.** Same as above.
- **The other Lifeblood findings** (A, C, D, feature requests). Tracked
  separately. This plan is BCL-only.

---

## 13. Risk assessment

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| New tests catch a regression in golden WriteSideApp | LOW | HIGH | The default path (host BCL added) is unchanged for plain SDK csprojs. WriteSideApp doesn't declare its own BCL, so it stays in the host-BCL path. |
| BCL-owning module has incomplete BCL declaration | LOW | HIGH | DAWG empirically works after the hotfix; Unity ships a complete netstandard 2.1 facade. If a future workspace breaks here, add adapter-side override per §7. |
| Assembly-identity parser misses an obscure shape | LOW | MEDIUM | Three signals (bare Include, comma-tokenized strong-name Include, HintPath basename). All three have explicit unit tests. |
| `AnalysisSnapshot` schema change breaks persisted state | UNKNOWN | LOW | Audit during step 6. Lifeblood doesn't appear to persist snapshots to disk, but verify. If it does, additive enum default is graceful. |
| `CsprojTimestamps` over-invalidates (false positive) | LOW | LOW | Negative test in §9.3 proves untouched csproj does not trigger recompile. |
| `CsprojTimestamps` under-invalidates (false negative) | LOW | HIGH | Positive test in §9.3 proves a csproj edit DOES trigger recompile. The original v1 hole was exactly this; v2 closes it with explicit coverage. |
| New invariants conflict with future analyzer adapters | LOW | LOW | INV-BCL-001..005 all reference Roslyn-specific concepts. They live in the C# adapter's contract, not in the application port. The application port only sees the typed `BclOwnership` field, which is language-agnostic. |

---

## 14. Approval

**Awaiting Matic's "go" on this v2 plan.** No code is written until then.

After approval, the rollout is sequential through the 16 steps in section 11.
Each commit covers one logical layer. The full suite must pass after every
commit. The DAWG verification (steps 13-14) is the final acceptance gate.

The v2 plan addresses every correction in the external review:
1. ✅ `ForceHostBcl` removed from `AnalysisConfig` (§7)
2. ✅ Csproj-timestamp invalidation included in same fix, not deferred (§8)
3. ✅ Assembly-identity matcher tightened for legacy strong-name shape (§3)
4. ✅ `AnalysisSnapshot` audit promoted to mandatory (§10, step 11)
5. ✅ Incremental + csproj-only-change regression test added (§9.3)
6. ✅ `bool OwnsBcl` upgraded to `enum BclOwnershipMode` (§4)

Plus the v1 architecture (decide once in discovery, consume in compilation,
no detection logic at the compilation layer) is preserved unchanged because
the reviewer agreed it was correct.
