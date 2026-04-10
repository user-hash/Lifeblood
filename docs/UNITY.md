# Unity Integration

Lifeblood runs as a **sidecar semantic engine** alongside Unity MCP. The Unity Editor stays in control of scenes, GameObjects, and assets. Lifeblood provides compiler-grade code intelligence.

## Architecture

```
Claude Code ──→ Unity MCP (action/control plane)
                    │
                    ├── built-in tools (scenes, GameObjects, scripts...)
                    │
                    └── [McpForUnityTool] custom tools ──→ Lifeblood MCP (child process)
                        └── 18 semantic tools (analyze, references, blast radius, file impact, resolve short name...)
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

Open Unity. The bridge auto-discovers via `[McpForUnityTool]` attributes. All 18 Lifeblood tools should appear alongside Unity MCP's built-in tools.

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

| Project size | Peak memory | Graph |
|---|---|---|
| ~10 modules (Lifeblood itself) | 212 MB peak | 1,376 symbols, 3,822 edges (5.1 s wall) |
| ~75 modules (400k LOC Unity project) | 571 MB peak | 44,569 symbols, 87,238 edges (32.6 s wall) |

Measured on AMD Ryzen 9 5950X (16 cores / 32 threads). Peak working set and wall time come from the native `usage` block on every `lifeblood_analyze` response. Older docs cited ~4 GB peak because early streaming measurements predated the `RetainCompilations=false` CLI path doing its job. The CLI analyze path now sits comfortably under 1 GB even on 75-module workspaces.

Each module is compiled, extracted, then downgraded to a lightweight PE metadata reference (~10-100KB vs ~200MB full compilation). Only one full compilation is in memory at a time.

## Lifecycle

- **Domain reload:** The bridge kills the sidecar process before Unity recompiles, and restarts it on next tool call.
- **Editor quit:** Process is killed via `EditorApplication.quitting` hook.
- **Crash recovery:** If the sidecar dies, the next tool call auto-restarts it.
