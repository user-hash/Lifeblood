# MCP Server Setup

Lifeblood's MCP server (`lifeblood-mcp`) gives AI agents 18 tools over stdio JSON-RPC. This page covers how it works, how to install it, and copy-paste configs for every major MCP client — including the Unity Editor via the Coplay MCP for Unity bridge.

## How it works

[Model Context Protocol](https://modelcontextprotocol.io/) (MCP) is the open stdio-based protocol that lets AI agents connect to local tool servers. Lifeblood ships one MCP server: `lifeblood-mcp`. It runs as a single .NET 8 process, speaks JSON-RPC 2.0 over stdin/stdout, and exposes 18 tools.

```
┌─────────────────┐   spawn (stdin/stdout)   ┌──────────────────────────┐
│   MCP client    │ ───────────────────────► │      lifeblood-mcp       │
│ (Claude Code,   │ ◄─────────────────────── │  (single .NET 8 process) │
│  Cursor, etc.)  │   JSON-RPC 2.0 messages  │                          │
└─────────────────┘                          │  ┌────────────────────┐  │
                                             │  │  Roslyn workspace  │  │
                                             │  │  (loaded once via  │  │
                                             │  │  lifeblood_analyze)│  │
                                             │  └────────────────────┘  │
                                             │  ┌────────────────────┐  │
                                             │  │  Semantic Graph    │  │
                                             │  │  (immutable, lazy  │  │
                                             │  │  indexes, shared   │  │
                                             │  │  across all tools) │  │
                                             │  └────────────────────┘  │
                                             └──────────────────────────┘
```

**Lifecycle.** The client spawns `lifeblood-mcp` once per session. The server starts empty — no graph loaded. The first call to `lifeblood_analyze` walks the project's csproj files, discovers modules, decides per-module BCL ownership from `<Reference>` elements, parses sources with Roslyn, builds the semantic graph, and caches the workspace and graph in memory. Every subsequent tool call shares that loaded state by reference. There is no per-call recompile, no domain reload, no IDE round-trip.

**Memory.** Streaming compilation with downgrading keeps a 75-module Unity workspace under ~4 GB. Each module is compiled, extracted, then downgraded to a lightweight PE metadata reference (~10–100 KB) so only one full Roslyn `Compilation` is held at once. Smaller projects (~10 modules) sit at ~200 MB.

**Read vs write side.** Eight tools are read-side (graph queries — `analyze`, `lookup`, `dependencies`, `dependants`, `blast_radius`, `file_impact`, `context`, `resolve_short_name`). Ten tools are write-side (Roslyn-backed compiler operations — `execute`, `diagnose`, `compile_check`, `find_references`, `find_definition`, `find_implementations`, `symbol_at_position`, `documentation`, `rename`, `format`). The split matters because write-side tools require a real Roslyn workspace and become unavailable in `readOnly` analysis mode.

**Symbol resolution.** Every read-side tool that takes a `symbolId` routes through `ISymbolResolver` before hitting the graph or workspace. Resolution order: exact canonical id → truncated method form (single-overload lenient) → bare short name. So `method:Foo.Bar` resolves correctly even though the canonical form is `method:Foo.Bar(int)`, and `lifeblood_resolve_short_name name="Bar"` discovers the canonical id when you don't know the namespace.

**Incremental re-analyze.** After a full analysis, `lifeblood_analyze` with `incremental: true` walks file timestamps, re-extracts only the changed source files, surgically replaces their graph slices, and returns in seconds. Csproj timestamp changes trigger a full re-discovery + recompile so BCL ownership and `AllowUnsafeCode` cannot go stale.

## Install

### Option 1 — published global tool (recommended)

```bash
dotnet tool install --global Lifeblood.Server.Mcp
```

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). After install, `lifeblood-mcp` is on `PATH`. Verify by running it directly — it will start, print nothing, and wait for JSON-RPC messages on stdin. Press `Ctrl+C` to exit.

To upgrade later: `dotnet tool update --global Lifeblood.Server.Mcp`.

### Option 2 — built locally from source

For development, or if you want to point at an unreleased build:

```bash
git clone https://github.com/user-hash/Lifeblood.git
cd Lifeblood
dotnet build
dotnet publish src/Lifeblood.Server.Mcp/Lifeblood.Server.Mcp.csproj -c Release -o dist
```

The published DLL lives at `dist/Lifeblood.Server.Mcp.dll`. Run via `dotnet dist/Lifeblood.Server.Mcp.dll` instead of the published tool. The Lifeblood repo ships [`.mcp.json.example`](../.mcp.json.example) with the canonical published-tool form so you can copy it to `.mcp.json` locally and edit the path if you want a dist override. `.mcp.json` itself is gitignored — machine-specific dist paths do not leak across contributors.

### Verify the install

```bash
lifeblood-mcp
# (no output — process is waiting for JSON-RPC on stdin)
```

If `lifeblood-mcp` is not found, check that `~/.dotnet/tools` (or the platform equivalent) is on your `PATH`. The .NET SDK installer adds it on Windows; on macOS/Linux you may need to add it manually.

---

## Claude Code

Add to `.mcp.json` in your project root (or `~/.claude/.mcp.json` for global). The Lifeblood repo ships [`.mcp.json.example`](../.mcp.json.example) with the canonical published-tool form — copy it to `.mcp.json` and you are done:

```json
{
  "mcpServers": {
    "lifeblood": {
      "command": "lifeblood-mcp",
      "args": []
    }
  }
}
```

Or from source (development) — point at a locally-published dist:

```json
{
  "mcpServers": {
    "lifeblood": {
      "command": "dotnet",
      "args": ["/path/to/Lifeblood/dist/Lifeblood.Server.Mcp.dll"]
    }
  }
}
```

Or directly via `dotnet run`:

```json
{
  "mcpServers": {
    "lifeblood": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/Lifeblood/src/Lifeblood.Server.Mcp"]
    }
  }
}
```

`.mcp.json` is gitignored in this repo so machine-specific dist paths do not leak across contributors. `.mcp.json.example` is the canonical template.

---

## Claude Desktop

Add to `claude_desktop_config.json`:

- **macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows**: `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "lifeblood": {
      "command": "lifeblood-mcp",
      "args": []
    }
  }
}
```

---

## Cursor

In Cursor settings, add an MCP server:

- **Name**: `lifeblood`
- **Command**: `lifeblood-mcp`
- **Arguments**: (none)
- **Transport**: `stdio`

Or in `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "lifeblood": {
      "command": "lifeblood-mcp",
      "args": []
    }
  }
}
```

---

## VS Code + Continue

In `.continue/config.json`:

```json
{
  "experimental": {
    "modelContextProtocolServers": [
      {
        "transport": {
          "type": "stdio",
          "command": "lifeblood-mcp",
          "args": []
        }
      }
    ]
  }
}
```

---

## Any stdio MCP client

Lifeblood uses **stdio transport** (JSON-RPC 2.0 over stdin/stdout). Launch the process and send JSON-RPC messages:

```bash
lifeblood-mcp
```

Initialize:

```json
{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "my-client", "version": "1.0"}}}
```

List tools:

```json
{"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}
```

Call a tool:

```json
{"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "lifeblood_analyze", "arguments": {"projectPath": "/path/to/project"}}}
```

---

## Unity Editor (via Coplay MCP for Unity)

Lifeblood integrates with the Unity Editor as a **sidecar process** under the [Coplay MCP for Unity](https://github.com/CoplayDev/MCPForUnity) plugin. Unity already speaks MCP through that plugin (scenes, GameObjects, scripts, prefabs, assets, build, etc.) — Lifeblood adds its 18 semantic tools to the same connection without competing for assemblies, without triggering domain reloads, and without colliding with Unity's own tooling.

### How the bridge works

```
┌──────────────────┐    ┌──────────────────────────────┐
│   Unity Editor   │    │  MCP for Unity (Coplay)      │
│                  │◄──►│  ┌────────────────────────┐  │
│  AssetDatabase,  │    │  │  Built-in tools        │  │
│  GameObjects,    │    │  │  (scenes, GO, assets…) │  │
│  C# compilation, │    │  └────────────────────────┘  │
│  prefabs, scenes │    │  ┌────────────────────────┐  │
└──────────────────┘    │  │ [McpForUnityTool]      │  │
                        │  │ Lifeblood bridge stubs │──┼──┐
                        │  │  (one per tool, fwds   │  │  │
                        │  │   to child process)    │  │  │
                        │  └────────────────────────┘  │  │
                        └──────────────────────────────┘  │
                                                          │ stdio JSON-RPC
                                                          ▼
                                        ┌─────────────────────────────┐
                                        │      lifeblood-mcp          │
                                        │   (separate .NET 8 process) │
                                        │   - Roslyn workspace        │
                                        │   - Semantic graph          │
                                        │   - 18 tools, all share     │
                                        │     one loaded state        │
                                        └─────────────────────────────┘
```

Three pieces work together:

1. **Coplay MCP for Unity** is the host plugin. It exposes its own MCP server inside the Unity Editor and lets you connect any MCP client (Claude Code, Cursor, …) to your running Editor.
2. **Lifeblood Unity bridge** lives at `unity/Editor/LifebloodBridge/` in the Lifeblood repo (`LifebloodTools.cs` + `LifebloodBridgeClient.cs`). Each Lifeblood tool has a small `[McpForUnityTool]`-decorated stub class. Coplay's plugin auto-discovers those stubs via reflection and registers them under its own MCP server alongside its built-in tools. When the client calls `lifeblood_lookup`, Coplay routes the call to the corresponding stub.
3. **The stubs forward to a child `lifeblood-mcp` process** managed by `LifebloodBridgeClient`. The first call spawns the child, performs the JSON-RPC `initialize` handshake, and reuses the same process for the rest of the session. Each tool call becomes a JSON-RPC `tools/call` request; the response is unwrapped back to Coplay and returned to the client.

The child process runs **outside** Unity's `AppDomain`. Three consequences:
- **No assembly conflicts.** Lifeblood pulls Roslyn 4.12.0 + Microsoft.CodeAnalysis. Unity ships its own (often older) Roslyn assemblies. Sidecar isolation means neither side fights for type identity.
- **No domain reload interference.** When Unity recompiles, the bridge kills the child and restarts it on the next call. The semantic graph is rebuilt fresh after the reload — analysis state cannot leak across compilation cycles.
- **Editor stays responsive.** Lifeblood analysis (~60–90 s on a 75-module Unity workspace cold) runs on the child process; Unity's main thread is not blocked.

### Setup (~3 minutes)

You need: Unity 2021.3+ or Unity 6, [Coplay MCP for Unity](https://github.com/CoplayDev/MCPForUnity) installed in the project, and Lifeblood built.

**1. Install or build Lifeblood somewhere alongside the Unity project.**

```bash
git clone https://github.com/user-hash/Lifeblood.git
cd Lifeblood
dotnet build
dotnet publish src/Lifeblood.Server.Mcp/Lifeblood.Server.Mcp.csproj -c Release -o dist
```

The bridge needs the published DLL at `dist/Lifeblood.Server.Mcp.dll`. The bridge auto-resolves the DLL if Lifeblood is a sibling directory of the Unity project — otherwise see the override below.

**2. Create a directory junction from your Unity project to the bridge.** This is what makes Unity treat the bridge files as if they live in the project, while keeping the source of truth in the Lifeblood repo:

Windows (`cmd.exe`, run from the Unity project root):
```cmd
mklink /J "Assets\Editor\LifebloodBridge" "X:\path\to\Lifeblood\unity\Editor\LifebloodBridge"
```

macOS / Linux:
```bash
ln -s /path/to/Lifeblood/unity/Editor/LifebloodBridge Assets/Editor/LifebloodBridge
```

The bridge ships its own asmdef so Unity compiles it as an Editor-only assembly with `MCPForUnity.Editor` referenced.

**3. Add the junction to your Unity project's `.gitignore`:**

```
Assets/Editor/LifebloodBridge/
Assets/Editor/LifebloodBridge.meta
```

The bridge files belong in the Lifeblood repo. They should not be committed to the consuming Unity project's git history.

**4. Open Unity.** The Editor compiles the bridge stubs. Coplay's MCP plugin auto-discovers them via the `[McpForUnityTool]` attribute. All 18 Lifeblood tools appear in Coplay's tool list alongside the built-in Unity tools.

**5. Connect any MCP client to Coplay MCP for Unity** following Coplay's own setup guide. From the client's perspective, Lifeblood tools (`lifeblood_analyze`, `lifeblood_lookup`, `lifeblood_blast_radius`, …) appear next to Coplay's tools (`unity_manage_scene`, `unity_find_gameobjects`, …) on a single MCP connection.

**6. First-call flow.** From the connected MCP client, call:

```
lifeblood_analyze projectPath="<absolute path to your Unity project>"
```

The bridge spawns the child process, performs the MCP `initialize` handshake (15 s timeout), forwards the analyze call (5 min timeout — DAWG cold analysis is ~90 s), and returns the loaded module/symbol/edge/violation summary. Subsequent tool calls reuse the same child process and the same loaded graph.

### Locating the server DLL

`LifebloodBridgeClient` resolves the DLL path in this order:

1. `EditorPrefs` key `Lifeblood_ServerPath` (Unity-side, persisted per machine).
2. Environment variable `LIFEBLOOD_SERVER_DLL` (process-wide).
3. Sibling `dist/` directory next to the Unity project (`<project>/../Lifeblood/dist/Lifeblood.Server.Mcp.dll`).
4. Sibling `dist/` next to the bridge symlink target.

If none resolve, the bridge logs an error to the Unity console and tool calls return an error object. Set the EditorPref or env var explicitly if your layout is non-standard.

### Lifecycle

- **Domain reload.** The bridge kills the child process before Unity recompiles, and restarts it on next tool call. Plan for ~60–90 s of cold-analyze re-warming on the first call after a recompile, or use `incremental: true` if the prior analysis result is still useful.
- **Unity quit.** The child process is killed when the Editor exits.
- **Crash recovery.** If the child dies mid-session (compile error, OOM), the bridge detects EOF on stdout, logs the failure, and respawns on the next tool call.

### When to use Unity bridge vs standalone CLI

- **Use the bridge** when you want Lifeblood semantic queries available to an AI agent inside the Unity Editor, in the same connection as Coplay's scene/asset tools, with no separate MCP client wiring.
- **Use a standalone client** (Claude Code, Cursor) connected directly to `lifeblood-mcp` when you want Lifeblood without the Unity Editor running, or when you want the lowest-latency path with no Coplay layer in between.

You can run both at the same time. Each is its own `lifeblood-mcp` process; nothing is shared between them.

---

## First steps after connecting

1. Call `lifeblood_analyze` with your project path to load the semantic graph
2. Don't know the canonical id of a symbol? Call `lifeblood_resolve_short_name name="MyType"` to discover it
3. Use `lifeblood_lookup`, `lifeblood_dependencies`, `lifeblood_blast_radius` to query the graph (truncated method ids and bare short names work — every read-side tool routes through `ISymbolResolver`)
4. Use `lifeblood_file_impact` to check which files break if you change a given file
5. Use `lifeblood_find_references` (pass `includeDeclarations=true` to also list every partial declaration site), `lifeblood_execute` (the script globals `Graph` / `Compilations` / `ModuleDependencies` give you typed access to the loaded semantic state), `lifeblood_compile_check` for write-side Roslyn features
6. After code changes, call `lifeblood_analyze` with `incremental: true` for fast re-analysis (only changed modules recompile, and csproj edits trigger re-discovery)

The graph stays in memory for the session. All 18 tools share the same loaded workspace.

## Notes for Unity / .NET Framework / Mono workspaces

If your modules ship their own BCL via csproj `<Reference Include="netstandard|mscorlib|System.Runtime">` or via `<HintPath>` to a vendored framework DLL, Lifeblood detects this at discovery time and switches that module to `BclOwnership=ModuleProvided`. The host .NET 8 BCL is NOT injected for those modules — preventing the BCL double-load that would otherwise produce CS0433/CS0518 on every System usage and silently zero out call-graph extraction. No configuration required.

If you see a flood of CS0227 false positives on a module that uses `unsafe` blocks, confirm the csproj has `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` — Lifeblood reads this from the csproj and passes `WithAllowUnsafe(true)` into the compilation.
