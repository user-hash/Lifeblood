# Unity Integration

Lifeblood runs as a **sidecar semantic engine** alongside Unity MCP. The Unity Editor stays in control of scenes, GameObjects, and assets. Lifeblood provides compiler-grade code intelligence.

## Architecture

```
Claude Code ──→ Unity MCP (action/control plane)
                    │
                    ├── built-in tools (scenes, GameObjects, scripts...)
                    │
                    └── [McpForUnityTool] custom tools ──→ Lifeblood MCP (child process)
                        └── semantic tools (analyze, references, blast radius, dead code, search, invariant check, authority report, port health, cycles, test impact, enum coverage, ...)
```

Lifeblood does NOT run inside Unity. It spawns as a separate .NET process with its own Roslyn workspace. No assembly conflicts, no domain reload interference, no memory pressure on the Editor.

## Setup

The bridge source lives in `unity/Editor/LifebloodBridge/` in the Lifeblood repo (3 files: asmdef, client, tools). Unity projects create a **directory junction** to this path so Unity sees the files as local.

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [MCP for Unity](https://github.com/CoplayDev/MCPForUnity) plugin installed in your Unity project
- Lifeblood repo cloned and built

### Step 1: Build Lifeblood

```bash
git clone https://github.com/user-hash/Lifeblood.git
cd Lifeblood
dotnet build
```

### Step 2: Create a directory junction

The bridge files need to be visible to Unity as editor scripts. Create a junction from your Unity project to the Lifeblood repo:

**Windows:**
```cmd
mklink /J "D:\YourUnityProject\Assets\Editor\LifebloodBridge" "D:\Lifeblood\unity\Editor\LifebloodBridge"
```

**macOS/Linux:**
```bash
ln -s /path/to/Lifeblood/unity/Editor/LifebloodBridge /path/to/YourUnityProject/Assets/Editor/LifebloodBridge
```

### Step 3: Add to `.gitignore`

The junction is a local dev concern. Don't track it in your Unity project's git:

```
Assets/Editor/LifebloodBridge/
Assets/Editor/LifebloodBridge.meta
```

### Step 4: Verify

Open Unity. The bridge auto-discovers via `[McpForUnityTool]` attributes. The Unity bridge surfaces a curated in-Editor subset of the Lifeblood tools alongside Unity MCP's built-in tools; the standalone `lifeblood-mcp` server exposes the full tool surface.

## Server Discovery

The bridge finds the Lifeblood server DLL automatically via sibling directory convention:

```
YourUnityProject/          ← your Unity project
Lifeblood/                 ← sibling directory
  src/Lifeblood.Server.Mcp/bin/Debug/net8.0/Lifeblood.Server.Mcp.dll
```

**Override if needed:**
- **EditorPrefs:** Set `Lifeblood_ServerPath` to the full path of `Lifeblood.Server.Mcp.dll`
- **Environment variable:** Set `LIFEBLOOD_SERVER_DLL`

## Incremental Re-Analyze

After the first `lifeblood_analyze_project`, pass `incremental=true` for fast updates. Only modules with changed files are recompiled:

```
lifeblood_analyze_project                    → full analysis (~60s on large projects)
lifeblood_analyze_project incremental=true   → seconds (only changed modules)
```

## Memory

Streaming compilation with downgrading keeps memory bounded:

| Project size | Peak memory (CLI streaming) | Peak memory (MCP retained) | Graph |
|---|---|---|---|
| ~11 modules (Lifeblood itself) | see `STATUS.md` | see `STATUS.md` | current live counts in `STATUS.md` |
| ~90 modules (400k LOC Unity project) | ~570 MB | ~3.7 GB | 62,134 symbols, 219,548 edges (~48 s wall) |

Two memory profiles on the same workspace are expected. The CLI path streams and releases compilations after extraction. The MCP path retains compilations in memory because the write-side tools (`lifeblood_execute`, `lifeblood_find_references`, `lifeblood_rename`, etc.) need to query the loaded workspace interactively. Pass `readOnly: true` to `lifeblood_analyze` on the MCP server to fall back to the CLI streaming profile in exchange for no write-side tools. To regain write-side tools after a read-only session, run a full retained analyze or retry a `fallbackReason:"compilationStateUnavailable"` incremental rejection with `allowFullFallback:true`.

Peak memory and wall time come from the native `usage` block on every `lifeblood_analyze` response. Prefer live receipts in `STATUS.md` over copying old workstation-specific numbers into this setup guide.

Each module is compiled, extracted, then downgraded to a lightweight PE metadata reference (~10-100KB vs ~200MB full compilation). Only one full compilation is in memory at a time.

## Lifecycle

- **Domain reload:** The bridge kills the sidecar process before Unity recompiles, and restarts it on next tool call.
- **Editor quit:** Process is killed via `EditorApplication.quitting` hook.
- **Crash recovery:** If the sidecar dies, the next tool call auto-restarts it.

## Unity-Aware Reachability (`INV-UNITY-001`)

Lifeblood detects Unity's framework dispatch automatically. `lifeblood_dead_code` does NOT flag:

- **Unity Editor reflection attributes (full roster):** `RuntimeInitializeOnLoadMethod`, `InitializeOnLoad`, `InitializeOnLoadMethod`, `InitializeOnEnterPlayMode`, `DidReloadScripts`, `MenuItem`, `ContextMenu`, `ContextMenuItem`, `CustomEditor`, `CustomPropertyDrawer`, `PropertyDrawer`, `PostProcessBuild`, `PostProcessScene`, `ScriptedImporter`, `OnOpenAsset`, `SettingsProvider`, `SettingsProviderGroup`, `Shortcut`, `Preserve`. Plus native interop: `BurstCompile`, `MonoPInvokeCallback`. Plus the full NUnit / Unity Test Framework lifecycle: `Test`, `TestCase`, `TestCaseSource`, `TestFixture`, `TestFixtureSource`, `Theory`, `SetUp`, `TearDown`, `OneTimeSetUp`, `OneTimeTearDown`, `UnityTest`, `UnitySetUp`, `UnityTearDown`.
- **MonoBehaviour magic methods:** `Awake`, `Start`, `Update`, `FixedUpdate`, `LateUpdate`, `OnEnable`, `OnDisable`, `OnDestroy`, `Reset`, `OnValidate`, `OnGUI`, `OnTriggerEnter` and variants, `OnCollisionEnter` and variants, `OnAudioFilterRead`, `OnRenderImage`, `OnDrawGizmos` and variants, full Unity message catalog. Only flagged when the containing type's transitive inheritance chain reaches a Unity message-receiver root: `UnityEngine.MonoBehaviour`, `UnityEngine.ScriptableObject`, `UnityEditor.Editor`, `UnityEditor.EditorWindow`, `UnityEngine.StateMachineBehaviour`. The chain is Roslyn-resolved, so components deriving through metadata-only framework bases such as Unity UI `Graphic -> UIBehaviour -> MonoBehaviour` are covered without a hardcoded subclass list.
- **Type-via-child propagation (`LB-FP-003`):** a type is reachable if ANY of its directly-contained members carries an entrypoint attribute. Closes the standard Unity pattern of `[SettingsProvider]` (or any other Unity reflection attr) on a static method inside a host type that otherwise has no incoming references — pre-fix the method became reachable while the host type surfaced as a dead candidate.

The chain walk uses `Symbol.Properties["baseTypeChain"]` plus the older direct-base `Symbol.Properties["baseType"]` fallback (set by the C# extractor), so types that inherit directly or indirectly from `UnityEngine.MonoBehaviour` still resolve even though the engine DLL itself isn't analyzed source.

**Dogfood vs the consumer workspace (87-module Unity workspace):** dead-code findings 1,095 → 729 (-33%) post-`INV-UNITY-001`, MonoBehaviour-magic FPs 378 → 13 (-97%). Type-level findings 6 → 4 post-`LB-FP-003` (`XRaySettingsProvider` and `MpServiceResets` cleared via the new `[SettingsProvider]` + type-via-child rules). Remaining advisory candidates are structural — UI Toolkit `VisualElement` subclasses with magic-named methods, audio callbacks on non-MonoBehaviour bases, reflection-based dispatch via `Type.GetType` + `MethodInfo.Invoke` — that future tightening can target via custom adapter rosters.

## Asmdef Edits (`INV-UNITY-002`)

Editing an asmdef without forcing Unity to regenerate the on-disk csproj used to leave the analyzer running against stale module facts. `RoslynWorkspaceAnalyzer.IncrementalAnalyze` now scans every `*.asmdef` under the project root on every incremental call; any addition / removal / mtime change triggers a full re-analyze that round.

## Asmdef Boundary Check (`INV-ASMDEF-CHECK-001`)

`lifeblood_asmdef_check` audits the loaded graph for compile-direction boundary
violations on Unity/old-format modules. For every cross-module source edge whose
source module is marked `referenceClosure=DirectOnly`, it checks that the source
module declares a direct dependency on the target module through the module-level
`DependsOn` graph edges produced from project discovery.

The response groups violations by source-target module pair and includes the
first offending source symbol, target symbol, edge kind, call site when present,
profile set when present, declared dependency list, and offending-edge count.
SDK-style/transitive modules are skipped because transitive ProjectReference
closure is valid for those projects. Re-run `lifeblood_analyze` after Unity
regenerates project descriptors so the module map and references are current.

## Unity Define Profiles (`INV-MULTI-DEFINE-UNITY-RESOLVER-001`)

On Unity workspaces (`Library/` exists at the project root),
`UnityDefineProfileResolver` exposes three canonical profiles:

- `Editor`: identity profile. Uses the csproj baseline defines.
- `Player`: removes the Unity editor discriminator family so
  `#if !UNITY_EDITOR` callsites become active.
- `Standalone`: removes the same editor discriminators and adds
  `UNITY_STANDALONE`, covering platform-neutral desktop guards such as
  `#if UNITY_STANDALONE && !UNITY_EDITOR`.

Use `defineProfiles:["Editor","Player","Standalone"]` for Unity dead-code or
dependency work when desktop-only guarded code matters. `Standalone` does not
pretend to be Windows, macOS, or Linux specifically; OS-specific desktop symbols
belong in a future target-platform profile atom.

## Execute Robustness on Unity (`INV-EXECUTE-001`)

`lifeblood_execute` auto-injects DLLs from `Library/ScriptAssemblies/`, `Library/Bee/artifacts/`, and `Library/PackageCache/` so scripts can touch UnityEngine types without Unity being open. Empty `Library/` surfaces a `runtimeAssemblyWarnings` entry telling the caller to run a Unity build first. The execute reference builder filters non-managed PEs, runtime BCL/contract assemblies such as stripped player `mscorlib.dll`, duplicate assembly identities, and assemblies already represented by retained workspace compilations before handing the set to Roslyn. `host` is the execution profile; non-host `targetProfile` values are accepted as compatibility hints, still run against the host scripting BCL, and surface the limitation on `targetRuntimeWarnings`.

## File-mode `compile_check` for Unity files (`LB-BUG-019`)

`lifeblood_compile_check filePath="Assets/.../YourFile.cs"` resolves the file's owning compilation by matching the path against every loaded compilation's syntax trees, then **swaps the existing tree** for the on-disk content via `ReplaceSyntaxTree` instead of adding the file as a fresh snippet tree to an arbitrary first compilation:

```
> lifeblood_compile_check filePath="Assets/Scripts/Core/MultiPartialHost.cs"

{
  "success": true,
  "diagnostics": [],
  "resolvedModule": "Acme.Module.Runtime",
  "existingTreeReplaced": true,
  "filePath": "Assets/Scripts/Core/MultiPartialHost.cs"
}
```

Pre-fix the same call surfaced ~120 spurious CS0246 / CS0103 errors against `UnityEngine`, `MonoBehaviour`, sibling partials, and every cross-file type because the snippet path picked some arbitrary first compilation that didn't carry the file's references. File-mode preserves every reference in the file's real owning module compilation and filters pre-existing diagnostics in OTHER files in the module so only changes the user introduced in THIS file surface. Pinned `moduleName` overrides auto-detection — if the file isn't in that module the request fails with `LB0002` rather than silently picking another.
