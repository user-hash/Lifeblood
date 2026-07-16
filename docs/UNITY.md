# Unity Integration

Lifeblood runs as a **workspace-shared semantic engine** alongside Unity MCP. The Unity Editor stays in control of scenes, GameObjects, and assets. Lifeblood provides compiler-grade code intelligence through a thin stdio proxy to the same daemon used by standalone agent clients.

## Architecture

```
Claude Code ──→ Unity MCP (action/control plane)
                    │
                    ├── built-in tools (scenes, GameObjects, scripts...)
                    │
                    └── [McpForUnityTool] custom tools ──→ Lifeblood MCP (shared proxy)
                        └── semantic tools (analyze, references, blast radius, dead code, search, invariant check, authority report, port health, cycles, test impact, enum coverage, ...)
```

Lifeblood does NOT run inside Unity. The bridge starts the installed `lifeblood-mcp` command in shared mode; its small proxy attaches to one workspace-keyed daemon that owns the Roslyn workspace. No Lifeblood/Roslyn assemblies load into Unity, and Unity plus direct agents observe the same committed graph.

## Setup

The Lifeblood repo's `unity/` directory is the canonical UPM package. Unity projects reference that package directly from `Packages/manifest.json`; do not copy its files or create a second bridge package in the consumer repository.

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [MCP for Unity](https://github.com/CoplayDev/MCPForUnity) plugin installed in your Unity project
- Lifeblood repo cloned next to the Unity project
- The matching `Lifeblood.Server.Mcp` global tool installed

### Step 1: Install the Lifeblood tool

```bash
dotnet tool install --global Lifeblood.Server.Mcp
# Later updates:
dotnet tool update --global Lifeblood.Server.Mcp
```

The bridge intentionally launches the installed tool rather than a repository Debug DLL. Shared-host admission compares the compiled module MVID, so every proxy must use the same installed build.

### Step 2: Reference the canonical UPM package

With the Unity project and Lifeblood as sibling directories, add this dependency to `Packages/manifest.json`:

```json
"com.dawgtools.lifeblood-bridge": "file:../../Lifeblood/unity"
```

Adjust the relative path for your layout. Track the manifest entry; the Lifeblood repository remains the sole bridge source.

### Step 3: Remove legacy copies

Delete any copied `Packages/com.dawgtools.lifeblood-bridge` directory or old `Assets/Editor/LifebloodBridge` junction before refreshing Unity. Keeping either would compile duplicate tool types and recreate two authorities.

### Step 4: Verify

Open Unity and refresh scripts. The bridge auto-discovers via `[McpForUnityTool]` attributes. Every parameter is a typed public instance property on the tool's nested `Parameters` class or shared snapshot-read base, matching the server input contract and Coplay's discovery contract. All bridge calls use Coplay's polling lifecycle so a cold analysis or reference search can outlive the synchronous gateway deadline without losing its result. Admission owns at most one unconsumed call per tool; an action-only status poll retrieves it without resending the original arguments. Lifeblood does not author per-tool max-poll metadata or kill a healthy proxy after a fixed tool-call wall clock.

## Server command discovery

The bridge resolves the command in this order:

1. Unity `EditorPrefs` key `Lifeblood_McpCommand`.
2. Environment variable `LIFEBLOOD_MCP_COMMAND`.
3. The standard `~/.dotnet/tools/lifeblood-mcp` global-tool shim, then `PATH`.

The command must be the executable, not a DLL plus arguments. The bridge owns `--shared --shared-key <UnityProjectRoot>` so admission and polling always address the same canonical workspace.

## Incremental Re-Analyze

After the first `lifeblood_analyze_project`, pass `incremental=true` for fast updates. The wrapper owns `projectPath`; callers can select `readOnly`, `allowFullFallback`, and `defineProfiles` directly through the typed schema:

```
lifeblood_analyze_project                    → full analysis (~60s on large projects)
lifeblood_analyze_project incremental=true   → seconds (only changed modules)
```

## Memory

Streaming compilation with downgrading keeps memory bounded:

| Project size | Peak memory (CLI streaming) | Peak memory (MCP retained) | Graph |
|---|---|---|---|
| ~11 modules (Lifeblood itself) | see `STATUS.md` | see `STATUS.md` | current live counts in `STATUS.md` |
| 100 modules (DAWG Editor+Player receipt) | see `STATUS.md` | 2.40 GB working set | 88,832 symbols, 346,496 edges (51.94 s server wall) |

Two memory profiles on the same workspace are expected. The CLI path streams and releases compilations after extraction. The MCP path retains compilations in memory because the write-side tools (`lifeblood_execute`, `lifeblood_find_references`, `lifeblood_rename`, etc.) need to query the loaded workspace interactively. Pass `readOnly: true` to `lifeblood_analyze` on the MCP server to fall back to the CLI streaming profile in exchange for no write-side tools. To regain write-side tools after a read-only session, run a full retained analyze or retry a `fallbackReason:"compilationStateUnavailable"` incremental rejection with `allowFullFallback:true`.

For multi-agent Unity work, shared mode (`lifeblood-mcp --shared` or `LIFEBLOOD_SHARED_SESSION=1`) lets agents attach to one daemon-owned `GraphSession` instead of each retaining a private Unity Roslyn workspace. The first agent to re-analyze refreshes the graph and retained compilations that every attached agent sees. Exact-build DAWG Editor+Player rollout now proves one daemon across four proxies, a common generation/snapshot, bounded incremental receipts, a pinned batch, last-client exit, clean restart, and 2.35 GB four-client steady private bytes versus multiplying a retained base per client. Use shared mode for same-workspace agents; keep ordinary stdio as the rollback path.

Peak memory and wall time come from the native `usage` block on every `lifeblood_analyze` response. Prefer live receipts in `STATUS.md` over copying old workstation-specific numbers into this setup guide.

Each module on the streaming path is compiled, extracted, then downgraded to a lightweight PE metadata reference (~10-100KB vs ~200MB full compilation). Retained shared mode keeps the one compiler base needed by write-side tools.

## Lifecycle

- **Domain reload:** The bridge disposes Unity's proxy before recompilation. The daemon and semantic base remain alive while another workspace client lease exists; the next Unity call reconnects.
- **Editor quit:** Unity's proxy is disposed via `EditorApplication.quitting`; the daemon follows its lease-aware idle policy.
- **Crash recovery:** Process exit or pipe closure marks the proxy broken. The next independent call starts a replacement proxy; a crashed daemon starts empty rather than replaying a possibly effectful request.

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
