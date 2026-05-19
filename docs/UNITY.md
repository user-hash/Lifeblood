# Unity Integration

Lifeblood runs as a **sidecar semantic engine** alongside Unity MCP. The Unity Editor stays in control of scenes, GameObjects, and assets. Lifeblood provides compiler-grade code intelligence.

## Architecture

```
Claude Code ──→ Unity MCP (action/control plane)
                    │
                    ├── built-in tools (scenes, GameObjects, scripts...)
                    │
                    └── [McpForUnityTool] custom tools ──→ Lifeblood MCP (child process)
                        └── 28 semantic tools (analyze, references, blast radius, dead code, search, invariant check, authority report, port health, cycles, test impact, enum coverage, ...)
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

Open Unity. The bridge auto-discovers via `[McpForUnityTool]` attributes. All 25 Lifeblood tools should appear alongside Unity MCP's built-in tools.

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
| ~11 modules (Lifeblood itself) | ~220 MB | ~3.5 GB | 2,513 symbols, 12,446 edges (~2 s wall) |
| ~90 modules (400k LOC Unity project) | ~570 MB | ~3.7 GB | 62,134 symbols, 219,548 edges (~48 s wall) |

Two memory profiles on the same workspace are expected. The CLI path streams and releases compilations after extraction (peak stays under 600 MB on a 75-module Unity workspace). The MCP path retains compilations in memory because the write-side tools (`lifeblood_execute`, `lifeblood_find_references`, `lifeblood_rename`, etc.) need to query the loaded workspace interactively, which pushes peak to ~2.5 GB on the same workspace. Pass `readOnly: true` to `lifeblood_analyze` on the MCP server to fall back to the CLI streaming profile in exchange for no write-side tools.

Measured on AMD Ryzen 9 5950X (16 cores / 32 threads). Peak memory and wall time come from the native `usage` block on every `lifeblood_analyze` response. Older docs cited ~4 GB peak - that figure was almost certainly measured against the MCP retained path without noting the distinction, and is closer to the 2.5 GB MCP peak than to the CLI 571 MB peak.

Each module is compiled, extracted, then downgraded to a lightweight PE metadata reference (~10-100KB vs ~200MB full compilation). Only one full compilation is in memory at a time.

## Lifecycle

- **Domain reload:** The bridge kills the sidecar process before Unity recompiles, and restarts it on next tool call.
- **Editor quit:** Process is killed via `EditorApplication.quitting` hook.
- **Crash recovery:** If the sidecar dies, the next tool call auto-restarts it.

## Unity-Aware Reachability (`INV-UNITY-001`)

Lifeblood detects Unity's framework dispatch automatically. `lifeblood_dead_code` does NOT flag:

- **Unity Editor reflection attributes (full roster):** `RuntimeInitializeOnLoadMethod`, `InitializeOnLoad`, `InitializeOnLoadMethod`, `InitializeOnEnterPlayMode`, `DidReloadScripts`, `MenuItem`, `ContextMenu`, `ContextMenuItem`, `CustomEditor`, `CustomPropertyDrawer`, `PropertyDrawer`, `PostProcessBuild`, `PostProcessScene`, `ScriptedImporter`, `OnOpenAsset`, `SettingsProvider`, `SettingsProviderGroup`, `Shortcut`, `Preserve`. Plus native interop: `BurstCompile`, `MonoPInvokeCallback`. Plus the full NUnit / Unity Test Framework lifecycle: `Test`, `TestCase`, `TestCaseSource`, `TestFixture`, `TestFixtureSource`, `Theory`, `SetUp`, `TearDown`, `OneTimeSetUp`, `OneTimeTearDown`, `UnityTest`, `UnitySetUp`, `UnityTearDown`.
- **MonoBehaviour magic methods:** `Awake`, `Start`, `Update`, `FixedUpdate`, `LateUpdate`, `OnEnable`, `OnDisable`, `OnDestroy`, `OnGUI`, `OnTriggerEnter` and variants, `OnCollisionEnter` and variants, `OnAudioFilterRead`, `OnRenderImage`, `OnDrawGizmos` and variants, full Unity message catalog. Only flagged when the containing type's transitive inheritance chain reaches a Unity message-receiver base: `UnityEngine.MonoBehaviour`, `UnityEngine.ScriptableObject`, `UnityEditor.Editor`, `UnityEditor.EditorWindow`, `UnityEngine.StateMachineBehaviour`.
- **Type-via-child propagation (`LB-FP-003`):** a type is reachable if ANY of its directly-contained members carries an entrypoint attribute. Closes the standard Unity pattern of `[SettingsProvider]` (or any other Unity reflection attr) on a static method inside a host type that otherwise has no incoming references — pre-fix the method became reachable while the host type surfaced as a dead candidate.

The chain walk uses `Symbol.Properties["baseType"]` (set by the C# extractor) so types that inherit directly from `UnityEngine.MonoBehaviour` still resolve - even though the engine DLL itself isn't analyzed source.

**Dogfood vs the consumer workspace (87-module Unity workspace):** dead-code findings 1,095 → 729 (-33%) post-`INV-UNITY-001`, MonoBehaviour-magic FPs 378 → 13 (-97%). Type-level findings 6 → 4 post-`LB-FP-003` (`XRaySettingsProvider` and `MpServiceResets` cleared via the new `[SettingsProvider]` + type-via-child rules). Remaining advisory candidates are structural — UI Toolkit `VisualElement` subclasses with magic-named methods, audio callbacks on non-MonoBehaviour bases, reflection-based dispatch via `Type.GetType` + `MethodInfo.Invoke` — that future tightening can target via custom adapter rosters.

## Asmdef Edits (`INV-UNITY-002`)

Editing an asmdef without forcing Unity to regenerate the on-disk csproj used to leave the analyzer running against stale module facts. `RoslynWorkspaceAnalyzer.IncrementalAnalyze` now scans every `*.asmdef` under the project root on every incremental call; any addition / removal / mtime change triggers a full re-analyze that round.

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
