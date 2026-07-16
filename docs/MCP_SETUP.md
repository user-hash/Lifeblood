# MCP Server Setup

Lifeblood's MCP server (`lifeblood-mcp`) gives AI agents the full MCP tool surface (read + write side) over stdio JSON-RPC; live counts in [`STATUS.md`](STATUS.md). This page covers how it works, how to install it, and copy-paste configs for every major MCP client, including the Unity Editor via the Coplay MCP for Unity bridge.

## How it works

[Model Context Protocol](https://modelcontextprotocol.io/) (MCP) is the open stdio-based protocol that lets AI agents connect to local tool servers. Lifeblood ships one MCP server, `lifeblood-mcp`. It runs as a single .NET 8 process, speaks JSON-RPC 2.0 over stdin/stdout, and exposes the read + write MCP tool surface in one shared session (live counts in [`STATUS.md`](STATUS.md)).

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

**Lifecycle.** In ordinary stdio mode, the client spawns one `lifeblood-mcp` process and that process owns one initially empty session. The first call to `lifeblood_analyze` walks the project's csproj files, discovers modules, decides per-module BCL ownership from `<Reference>` elements, parses sources with Roslyn, builds the semantic graph, and caches the workspace and graph in memory. Every subsequent tool call in that process shares that loaded state by reference. There is no per-call recompile, no domain reload, no IDE round-trip.

**Shared multi-agent mode.** For multiple agents attached to the same workspace, start `lifeblood-mcp` with `--shared` or set `LIFEBLOOD_SHARED_SESSION=1`. Each MCP client still gets a small stdio process, but that process is a persistent proxy to one canonical-workspace-keyed named-pipe daemon. The daemon owns the single latest semantic `GraphSession`: any attached agent may run `lifeblood_analyze`, identical concurrent requests coalesce, and every attached client observes the committed publication. The default key resolves to the nearest Git worktree root (falling back to the configured/process directory); use `--shared-key <path>` or `LIFEBLOOD_SHARED_SESSION_KEY` when launch directories cannot converge. `--shared-pipe` / `LIFEBLOOD_SHARED_PIPE_NAME` is diagnostic routing only and cannot bypass the typed protocol/build/workspace handshake. One persistent proxy connection owns one client lease and forwards matching MCP cancellation notifications while a request is active; cancelling or disconnecting one coalesced waiter does not cancel work still needed by another surviving waiter. A retried full analyze whose `WorkspaceAnalysisIdentity` already matches the current publication returns `publication.action:"reusedCurrent"` instead of publishing a duplicate generation. Disconnect starts the last-client idle deadline, and accepted maintenance drain or idle expiry cooperatively disposes the daemon-owned session. Bounded historical publications are graph-only, so the catalog never creates another Roslyn base. Exact-build Lifeblood and DAWG rollout receipts now cover 1/2/4 clients, coalescing, cancellation, disconnected-waiter survival, pinned batches, accepted-change evidence, idle exit, restart recovery, and memory. Private stdio remains the one-line rollback path.

**Memory.** Streaming compilation with downgrading compiles, extracts, then downgrades each module to a lightweight PE metadata reference (around 10 to 100 KB), so only one full Roslyn `Compilation` is held at once on the streaming path. Retained MCP sessions keep compilations in memory for write-side tools. With ordinary stdio, every MCP client has its own retained heap. With shared mode, those clients converge on one daemon-owned retained heap per workspace key. Historical catalog entries share immutable graph/analysis references and retain zero Roslyn services; pinned entries count toward the same hard bound. Use the `usage` block on every `lifeblood_analyze` response, `lifeblood_snapshots`, and the current receipts in [`STATUS.md`](STATUS.md) as the source of truth for a workspace's actual memory profile.

**Read vs write side.** Twenty-three tools are read-side: graph queries and host observations, namely `analyze`, `capabilities`, `batch`, `snapshots`, `lookup`, `dependencies`, `dependants`, `blast_radius`, `file_impact`, `asmdef_check`, `context`, `resolve_short_name`, `resolve_member`, `search`, `dead_code`, `partial_view`, `invariant_check`, `authority_report`, `authority_coverage`, `port_health`, `cycles`, `test_impact`, and `contract_audit`. Eighteen tools are write-side: Roslyn-backed compiler operations, namely `execute`, `diagnose`, `compile_check`, `find_references`, `find_definition`, `find_implementations`, `enum_coverage`, `static_tables`, `assignment_coverage`, `callsite_arguments`, `wire_audit`, `feature_switch_audit`, `member_count`, `struct_layout`, `symbol_at_position`, `documentation`, `rename`, and `format`. This 23/18 split is legacy retained-compilation compatibility metadata, not scheduling policy. The authoritative `sessionRequirement`, `effect`, and `sessionAccess` behavior contracts drive availability and concurrency; snapshot reads derive exactly from `Observe + SharedRead`. Live values are reported by `lifeblood_capabilities` and tracked in [`STATUS.md`](STATUS.md).

**Symbol resolution.** Every read-side tool that takes a `symbolId` routes through `ISymbolResolver` before hitting the graph or workspace. Resolution order is: exact canonical id, then truncated method form (single-overload lenient), then bare short name. So `method:Foo.Bar` resolves correctly even though the canonical form is `method:Foo.Bar(int)`, and `lifeblood_resolve_short_name name="Bar"` discovers the canonical id when you don't know the namespace.

**Incremental re-analyze.** After a full analysis, `lifeblood_analyze` with `incremental: true` uses file timestamps as a cheap prefilter, then source content hashes decide which touched files actually re-extract. Every incremental/rejected/fallback response carries one `acceptedChanges` receipt derived from the adapter's canonical change set. The default `changeReceiptMode:"summary"` returns scan mode, causes, interpretation, and complete counts without paths. `acceptedChanges.interpretation` spells out `work`, `changedSourceFilesMeaning`, `contentChangeStatus`, and a short human `summary`; cold full fallback therefore says `work:"fullFallbackReanalysis"` and `contentChangeStatus:"none"` instead of asking clients to infer that from `changedSourceFiles`. Use `changeReceiptMode:"detail"` plus optional `changeReceiptLimit` (default 50, clamped 1–200) for one bounded, sorted union of project-relative paths whose booleans distinguish reanalysis, mtime touch, content change, descriptor-forced recompile, and deletion; `evidenceFileCount`, `returnedFileCount`, `omittedFileCount`, and `truncated` make clipping explicit. Legacy `changedSourceFiles`, `mtimeTouchedSourceFiles`, and `contentChangedSourceFiles` are derived from that same receipt. A save without content change is therefore visible as `mode:"incremental-noop"` plus the `mtimeOnly` cause, while a full fallback reports sources as reanalyzed without pretending all mtimes/content changed. If an editor or watcher already knows the exact changed set, pass `authoritativeChangedFiles` to narrow source scanning; csproj/asmdef descriptor drift and analysis-scope drift are still checked independently. Csproj changes force the affected module's sources through recompilation and surface `descriptorRecompile`. A session previously loaded with `readOnly:true` reports `fallbackReason:"compilationStateUnavailable"` when retained compilation state is needed; retry with `allowFullFallback:true` or run a fresh full retained analyze.

**Snapshot-pinned reads.** Every registered `Observe + SharedRead` tool accepts optional `snapshotId`, `expectedSnapshotId`, and `expectedAnalysisGeneration` fields. `snapshotId` selects one exact current or retained graph-only publication; a missing/evicted id never falls through to latest. The server acquires an immutable lease first, then compares any precondition against that exact publication. Historical graph queries work normally, while live-source and Roslyn-backed tools return unavailable because the catalog stores neither source bytes nor semantic services. `lifeblood_batch` accepts 1–32 read calls, fully validates the entire plan before call zero, and runs it serially in input order under one outer lease. Unknown, nested, exclusive, effectful, or conflicting nested selections reject the whole plan. Use `lifeblood_snapshots` to list, pin/name, unpin, evict, or optionally drift-check the default-three/hard-sixteen graph-only catalog. Every successful envelope names the exact snapshot id, generation, and canonical analysis identity used by the result.

## Install

### Option 1. Published global tool (recommended)

```bash
dotnet tool install --global Lifeblood.Server.Mcp
```

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). After install, `lifeblood-mcp` is on `PATH`. Verify by running it directly. It will start, print nothing, and wait for JSON-RPC messages on stdin. Press `Ctrl+C` to exit.

To upgrade later: `dotnet tool update --global Lifeblood.Server.Mcp`.

### Option 2. Built locally from source

For development, or if you want to point at an unreleased build:

```bash
git clone https://github.com/user-hash/Lifeblood.git
cd Lifeblood
dotnet build
```

The build output lives at `src/Lifeblood.Server.Mcp/bin/Debug/net8.0/Lifeblood.Server.Mcp.dll`. Point `.mcp.json` at that path directly (see the dev example below). Every subsequent `dotnet build` or `dotnet test` refreshes it automatically - no publish step, no stale-binary drift class. The Lifeblood repo ships [`.mcp.json.example`](../.mcp.json.example) with the canonical published-tool form so you can copy it to `.mcp.json` locally and edit the path if you want a dev-build override. `.mcp.json` itself is gitignored, so machine-specific paths do not leak across contributors.

### Verify the install

```bash
lifeblood-mcp
# (no output: the process is waiting for JSON-RPC on stdin)
```

If `lifeblood-mcp` is not found, check that `~/.dotnet/tools` (or the platform equivalent) is on your `PATH`. The .NET SDK installer adds it on Windows; on macOS/Linux you may need to add it manually.

---

## Configuration (environment variables)

The server reads optional environment variables at startup. All have safe defaults; set them per deployment without a code change. Shared-session variables are honored by direct clients; the Unity bridge always supplies shared mode and its current project root. `LIFEBLOOD_MCP_COMMAND` is the bridge-only executable override.

| Variable | Default | Effect |
|---|---|---|
| `LIFEBLOOD_TELEMETRY` | off | Opt-in operational telemetry. Set to `1`, `true`, `yes`, `on`, or `diagnostics` to emit .NET `ActivitySource` / `Meter` events (tool success/error, argument diagnostics, response-JSON cost, analyze result/fallback, analyze phase allocation, result truncation, invariant-parse cache outcomes). Any other value (or unset) uses the no-op sink. |
| `LIFEBLOOD_STALENESS_SECONDS_THRESHOLD` | `3600` | Wall-clock age (seconds) past which a read-side response adds a staleness limitation to its truth envelope. |
| `LIFEBLOOD_FILES_CHANGED_THRESHOLD` | `10` | File-churn count since the last analyze past which a read-side response adds a files-changed limitation to its truth envelope. |
| `LIFEBLOOD_JSON_COMPAT` | `legacy` | Tool-argument compatibility mode: `legacy` accepts today's wire, `warn` accepts but emits `lifeblood.tool.arguments` telemetry for unknown/missing/type-mismatch/duplicate arguments, and `strict` rejects invalid tool arguments. In strict mode the MCP request parser also rejects duplicate JSON properties before binding (`INV-MCP-STRICT-JSON-001`, `INV-MCP-TOOL-ARG-CONTRACT-001`). |
| `LIFEBLOOD_STRICT_JSON` | off | Backward-compatible strict alias used only when `LIFEBLOOD_JSON_COMPAT` is unset. Truthy values select the same strict behavior as `LIFEBLOOD_JSON_COMPAT=strict`. |
| `LIFEBLOOD_SNAPSHOT_HISTORY_LIMIT` | `3` | Graph-only publications retained per session. `0` disables history; the Application hard maximum is `16`. Invalid/out-of-range values use `3`; pins consume the same bound. |
| `LIFEBLOOD_SNAPSHOT_HISTORY_MAX_AGE_SECONDS` | `86400` | Unpinned history age before eviction. `0` disables age expiry; maximum accepted deployment value is one year. Invalid/out-of-range values use 24 hours. |
| `LIFEBLOOD_SHARED_SESSION` | off | Truthy values make this process a stdio proxy to a workspace-keyed shared daemon instead of owning a private in-process `GraphSession`. Equivalent to passing `--shared`. |
| `LIFEBLOOD_SHARED_SESSION_KEY` | nearest Git worktree root, else current working directory | Canonical root used to derive shared daemon identity. Set an explicit absolute workspace path when several agents launch outside the worktree but should share one scan. |
| `LIFEBLOOD_SHARED_PIPE_NAME` | derived from key | Explicit named-pipe name. Use only when you need exact interop with a supervisor; otherwise prefer the key. |
| `LIFEBLOOD_SHARED_DAEMON_AUTOSTART` | on | Set false only when an external supervisor owns the daemon lifecycle. A missing daemon then returns a recoverable proxy error and the proxy remains attached for a later replacement instead of spawning a detached process. |
| `LIFEBLOOD_SHARED_IDLE_SECONDS` | `300` | Last-client idle interval before the daemon drains, disposes its retained session, releases pipe/mutex ownership, and exits. Malformed, negative, NaN, or infinite values fall back to five minutes. |
| `LIFEBLOOD_SHARED_PROXY_TRACE` | unset | Optional diagnostic file receiving bounded proxy transport traces. Leave unset for normal operation; stdout remains JSON-RPC only. |
| `LIFEBLOOD_MCP_COMMAND` | standard global-tool shim / `PATH` | Unity-bridge-only executable override. Supply the `lifeblood-mcp` executable path, not a DLL or argument string; the bridge appends shared mode and the Unity project key. |

Malformed numeric values fall through to each setting's documented default; they never throw. The live capability surface — including which feature flags and telemetry events are active in the running server — is reported by the `lifeblood_capabilities` tool.

---

## Claude Code

Add to `.mcp.json` in your project root (or `~/.claude/.mcp.json` for global). The Lifeblood repo ships [`.mcp.json.example`](../.mcp.json.example) with the canonical published-tool form. Copy it to `.mcp.json` and you are done:

```json
{
  "mcpServers": {
    "lifeblood": {
      "command": "lifeblood-mcp",
      "args": ["--shared"]
    }
  }
}
```

Private stdio rollback form:

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

Or from source (development), pointing at the local build output. Any `dotnet build` or `dotnet test` refreshes it, so you always run against HEAD with no publish step:

```json
{
  "mcpServers": {
    "lifeblood": {
      "command": "dotnet",
      "args": ["/path/to/Lifeblood/src/Lifeblood.Server.Mcp/bin/Debug/net8.0/Lifeblood.Server.Mcp.dll"]
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
      "args": ["--shared"]
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
      "args": ["--shared"]
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
          "args": ["--shared"]
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

Lifeblood integrates with the Unity Editor as a **canonical UPM outer adapter** under the [Coplay MCP for Unity](https://github.com/CoplayDev/MCPForUnity) plugin. Unity already speaks MCP through that plugin (scenes, GameObjects, scripts, prefabs, assets, build, and so on). Lifeblood adds semantic tools to that connection through a thin shared proxy, while direct agent clients and Unity consume one daemon-owned graph.

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
                        │  │   to shared proxy)     │  │  │
                        │  └────────────────────────┘  │  │
                        └──────────────────────────────┘  │
                                                          │ stdio JSON-RPC
                                                          ▼
                                        ┌─────────────────────────────┐
                                        │      lifeblood-mcp          │
                                        │   (shared .NET 8 daemon)    │
                                        │   - Roslyn workspace        │
                                        │   - Semantic graph          │
                                        │   - All tools share         │
                                        │     one loaded state        │
                                        └─────────────────────────────┘
```

Three pieces work together:

1. **Coplay MCP for Unity** is the host plugin. It exposes its own MCP server inside the Unity Editor and lets you connect any MCP client (Claude Code, Cursor, …) to your running Editor.
2. **Lifeblood Unity bridge** lives at `unity/Editor/LifebloodBridge/` in the Lifeblood repo (`LifebloodTools.cs` + `LifebloodBridgeClient.cs`). Each Lifeblood tool has a small `[McpForUnityTool]`-decorated stub class. Coplay's plugin auto-discovers those stubs via reflection and registers them under its own MCP server alongside its built-in tools. When the client calls `lifeblood_lookup`, Coplay routes the call to the corresponding stub.
3. **The stubs forward through an installed `lifeblood-mcp --shared` proxy** managed by `LifebloodBridgeClient`. The proxy handshakes with the canonical workspace-keyed daemon, which owns the one retained semantic base. Each tool call becomes a JSON-RPC `tools/call` request; Coplay polls long calls until the bridge returns their terminal result.

Lifeblood runs **outside** Unity's `AppDomain`. Three consequences:

- **No assembly conflicts.** Lifeblood pulls Roslyn 4.14 (Microsoft.CodeAnalysis). Unity ships its own (often older) Roslyn assemblies. Sidecar isolation means neither side fights for type identity.
- **No domain reload state loss.** When Unity recompiles, the bridge disposes only Unity's proxy. The semantic daemon stays alive while another workspace lease exists, and the next Unity call reconnects to the same publication.
- **Editor stays responsive.** Lifeblood work runs in the shared daemon and every bridge tool uses Coplay's polling lifecycle, so Unity's synchronous gateway does not need to hold a cold call open.

### Setup (~3 minutes)

You need: Unity 2021.3+ or Unity 6, [Coplay MCP for Unity](https://github.com/CoplayDev/MCPForUnity) installed in the project, the Lifeblood repo available to Unity Package Manager, and the matching `Lifeblood.Server.Mcp` global tool installed.

**Step 1. Install Lifeblood's MCP tool.**

```bash
dotnet tool install --global Lifeblood.Server.Mcp
# Later updates:
dotnet tool update --global Lifeblood.Server.Mcp
```

The bridge launches the installed executable, never a repository Debug DLL. Shared-host identity includes the compiled module MVID, so Unity and direct clients must use the same installed build.

**Step 2. Reference the canonical UPM package.** With Lifeblood and the Unity project as sibling directories, add this dependency to `Packages/manifest.json`:

```json
"com.dawgtools.lifeblood-bridge": "file:../../Lifeblood/unity"
```

Adjust the relative path for your layout. The package ships its own Editor-only asmdef with `MCPForUnity.Editor` referenced.

**Step 3. Remove any legacy bridge copy or junction.** Delete a copied `Packages/com.dawgtools.lifeblood-bridge` directory or `Assets/Editor/LifebloodBridge` junction before refreshing. The consumer tracks only the manifest reference; `unity/` remains the single source.

**Step 4. Open Unity.** The Editor compiles the bridge stubs. Coplay auto-discovers their typed nested `Parameters` properties and polling metadata. The Unity bridge surfaces a curated subset — 19 of the 41 tools, with `lifeblood_analyze_project` wrapping analyze and owning the Unity project path — alongside the built-in Unity tools. The remaining tools are reachable through a direct `lifeblood-mcp --shared` client and observe the same daemon publication.

**Step 5. Connect any MCP client to Coplay MCP for Unity** following Coplay's own setup guide. From the client's perspective, `lifeblood_analyze_project`, `lifeblood_lookup`, `lifeblood_blast_radius`, and the other bridge tools appear next to Coplay's Unity tools on one connection.

**Step 6. First-call flow.** From the connected MCP client, call:

```
lifeblood_analyze_project incremental=false readOnly=false defineProfiles=["Editor","Player"]
```

The bridge injects the current Unity project root, starts the installed shared proxy, performs the MCP `initialize` handshake, and returns a polling receipt before Coplay's synchronous deadline. Status polls use the same arguments and project root, so they retrieve the original task's terminal result. Subsequent bridge and direct-agent calls reuse the same daemon graph.

### Locating the server command

`LifebloodBridgeClient` resolves the executable in this order:

1. `EditorPrefs` key `Lifeblood_McpCommand` (Unity-side, persisted per machine).
2. Environment variable `LIFEBLOOD_MCP_COMMAND`.
3. The standard `~/.dotnet/tools/lifeblood-mcp` shim, then `PATH`.

Overrides name one executable, not a command line. The bridge owns `--shared --shared-key <UnityProjectRoot>` so every admission and status call targets the same workspace identity.

### Lifecycle

- **Domain reload.** The bridge disposes Unity's proxy before recompilation. The daemon keeps its base while another client lease exists; the next Unity call reconnects.
- **Unity quit.** Unity's proxy is disposed; the daemon follows the shared idle/drain policy.
- **Crash recovery.** EOF or timeout closes the broken proxy. The next independent call connects a replacement; failed effectful requests are never replayed automatically.

### When to use Unity bridge vs standalone CLI

- **Use the bridge** when you want Lifeblood semantic queries available to an AI agent inside the Unity Editor, in the same connection as Coplay's scene/asset tools, with no separate MCP client wiring.
- **Use a standalone client** (Claude Code, Cursor) connected directly to `lifeblood-mcp` when you want Lifeblood without the Unity Editor running, or when you want the lowest-latency path with no Coplay layer in between.

You can run both at the same time. The canonical bridge always uses shared mode, so direct clients launched with `--shared` and the same workspace identity attach to that daemon-owned session. One analyze refreshes the graph every attached agent sees. Identity, workspace binding, client leases, idle drain, exact analyze coalescing, request cancellation, pinned read batches, bounded graph-only history, and Lifeblood/DAWG deployment and memory gates are closed. Keep private stdio available only as the rollback configuration.

---

## First steps after connecting

1. Through Unity, call `lifeblood_analyze_project`; through a direct client, call `lifeblood_analyze` with `projectPath`.
2. Don't know the canonical id of a symbol? Call `lifeblood_resolve_short_name name="MyType"` to discover it.
3. Use `lifeblood_lookup`, `lifeblood_dependencies`, and `lifeblood_blast_radius` to query the graph. Truncated method ids and bare short names work because every read-side tool routes through `ISymbolResolver`.
4. For multi-tool evidence, copy `envelope.snapshotId` into `expectedSnapshotId` or use `lifeblood_batch` so a concurrent refresh cannot mix generations. Use `lifeblood_snapshots` and exact `snapshotId` selection only when a bounded graph-only investigation lane must survive a refresh.
5. Use `lifeblood_file_impact` to check which files break if you change a given file.
6. For write-side Roslyn features, use `lifeblood_find_references` (pass `includeDeclarations=true` to also list every partial declaration site), `lifeblood_execute` (the script globals `Graph`, `Compilations`, and `ModuleDependencies` give you typed access to the loaded semantic state), and `lifeblood_compile_check`.
7. After code changes, call `lifeblood_analyze` with `incremental: true` for fast re-analysis. Only changed modules recompile, and csproj edits trigger re-discovery.

The graph stays in memory for the session. Every tool shares the same loaded workspace.

## Notes for Unity, .NET Framework, and Mono workspaces

If your modules ship their own BCL via csproj `<Reference Include="netstandard|mscorlib|System.Runtime">` or via `<HintPath>` to a vendored framework DLL, Lifeblood detects this at discovery time and switches that module to `BclOwnership=ModuleProvided`. The host .NET 8 BCL is NOT injected for those modules. This prevents the BCL double-load that would otherwise produce CS0433 and CS0518 on every System usage and silently zero out call-graph extraction. No configuration required.

If you see a flood of CS0227 false positives on a module that uses `unsafe` blocks, confirm the csproj has `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`. Lifeblood reads this from the csproj and passes `WithAllowUnsafe(true)` into the compilation.
