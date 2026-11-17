# MCP Server Setup

Lifeblood's MCP server (`lifeblood-mcp`) gives AI agents the full MCP tool surface (read + write side) over stdio JSON-RPC; live counts in [`STATUS.md`](STATUS.md). This page covers how it works, how to install it, and copy-paste configs for every major MCP client, including the Unity Editor via the Coplay MCP for Unity bridge.

## How it works

[Model Context Protocol](https://modelcontextprotocol.io/) (MCP) is the open stdio-based protocol that lets AI agents connect to local tool servers. Lifeblood ships one MCP server, `lifeblood-mcp`. It runs as a single .NET 11 process, speaks JSON-RPC 2.0 over stdin/stdout, and exposes the read + write MCP tool surface in one shared session (live counts in [`STATUS.md`](STATUS.md)).

The diagram below shows private stdio. Shared mode instead uses one thin proxy
per connected client and one workspace daemon, as described below.

```
┌─────────────────┐   spawn (stdin/stdout)   ┌──────────────────────────┐
│   MCP client    │ ───────────────────────► │      lifeblood-mcp       │
│ (Claude Code,   │ ◄─────────────────────── │  (single .NET 11 process) │
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

**Lifecycle.** In ordinary stdio mode, the client spawns one `lifeblood-mcp` process and that process owns one initially empty session. The first call to `lifeblood_analyze` walks the project's csproj files, discovers modules, decides per-module BCL ownership from `<Reference>` elements, parses sources with Roslyn, builds the semantic graph, and retains the workspace and graph. Subsequent tools share that publication. Ordinary graph reads do not recompile; compile-check, source refresh and scoped secondary-profile analysis can construct or emit compilations. Lifeblood does not require an IDE round-trip for those operations.

**Shared multi-agent mode.** For multiple agents attached to the same workspace, start `lifeblood-mcp` with `--shared` or set `LIFEBLOOD_SHARED_SESSION=1`. Each MCP client still gets a small stdio process, but that process is a persistent proxy to one canonical-workspace-keyed named-pipe daemon. The daemon owns the single latest semantic `GraphSession`: any attached agent may run `lifeblood_analyze`, identical concurrent requests coalesce, and every attached client observes the committed publication. The default key resolves to the nearest Git worktree root (falling back to the configured/process directory); use `--shared-key <path>` or `LIFEBLOOD_SHARED_SESSION_KEY` when launch directories cannot converge. `--shared-pipe` / `LIFEBLOOD_SHARED_PIPE_NAME` is diagnostic routing only and cannot bypass the typed protocol/build/workspace handshake. One persistent proxy connection owns one client lease and forwards matching MCP cancellation notifications while a request is active; cancelling or disconnecting one coalesced waiter does not cancel work still needed by another surviving waiter. A retried full analyze whose `WorkspaceAnalysisIdentity` already matches the current publication returns `publication.action:"reusedCurrent"` instead of publishing a duplicate generation. Disconnecting the last client does not discard the shared base by default; accepted maintenance drain or an explicitly configured idle-eviction policy cooperatively disposes the daemon-owned session. Bounded historical publications are graph-only, so the catalog never creates another Roslyn base. Integration fixtures cover client coalescing, cancellation, disconnected waiters, pinned batches, accepted changes, idle exit and recovery. Private stdio remains the one-line rollback path.

**Memory.** Streaming compilation with downgrading compiles, extracts, then downgrades each module to a PE metadata reference, so only one full Roslyn `Compilation` is held at once on the streaming path. Metadata size depends on the module. Retained MCP sessions keep compilations in memory for write-side tools. With ordinary stdio, every MCP client has its own retained heap. With shared mode, those clients converge on one daemon-owned retained heap per workspace key. Historical catalog entries share immutable graph/analysis references and retain zero Roslyn services; pinned entries count toward the same hard bound. Use the `usage` block on every `lifeblood_analyze` response, `lifeblood_snapshots`, and the current receipts in [`STATUS.md`](STATUS.md) as the source of truth for a workspace's actual memory profile.

**Tool availability and concurrency.** The read/write split is legacy retained-compilation compatibility metadata. The registry-owned `sessionRequirement`, `effect` and `sessionAccess` contracts drive actual availability and concurrency; snapshot reads derive from `Observe + SharedRead`. Use live `lifeblood_capabilities` for the running catalog, [`TOOLS.md`](TOOLS.md) for tool semantics and [`STATUS.md`](STATUS.md) for ratcheted counts instead of a second authored roster here.

**Read-only retention.** Private stdio and standalone CLI read-only analysis release compilations. Shared mode can preserve one compatible hot semantic publication, while catalog history remains graph-only. Recovery verifies canonical inputs before restoring graph and services together; inspect `semanticRetention` and live capability availability. If compatible services are unavailable, retained work returns `compilationStateUnavailable` and needs an ordinary retained full analysis or an allowed full fallback. A `readOnly:true` flag alone does not prove another Roslyn base was created or that an existing compatible base was lost.

**Symbol resolution.** Every read-side tool that takes a `symbolId` routes through `ISymbolResolver` before hitting the graph or workspace. Resolution order is: exact canonical id, then truncated method form (single-overload lenient), then bare short name. So `method:Foo.Bar` resolves correctly even though the canonical form is `method:Foo.Bar(int)`, and `lifeblood_resolve_short_name name="Bar"` discovers the canonical id when you don't know the namespace.

**Incremental re-analyze.** After a full analysis, `lifeblood_analyze` with `incremental: true` uses file timestamps as a cheap prefilter, then source content hashes decide which touched files actually re-extract. Every incremental/rejected/fallback response carries one `acceptedChanges` receipt derived from the adapter's canonical change set. The default `changeReceiptMode:"summary"` returns scan mode, causes, interpretation, and complete counts without paths. `acceptedChanges.interpretation` spells out `work`, `changedSourceFilesMeaning`, `contentChangeStatus`, and a short human `summary`; cold full fallback therefore says `work:"fullFallbackReanalysis"` and `contentChangeStatus:"none"` instead of asking clients to infer that from `changedSourceFiles`. Use `changeReceiptMode:"detail"` plus optional `changeReceiptLimit` (default 50, clamped 1–200) for one bounded, sorted union of project-relative workspace identities whose booleans distinguish reanalysis, mtime touch, content change, descriptor-forced recompile, and deletion; a Unity `file:` package is named by its logical `Packages/{name}/...` path even when stored outside the project root. `evidenceFileCount`, `returnedFileCount`, `omittedFileCount`, and `truncated` make clipping explicit. Legacy `changedSourceFiles`, `mtimeTouchedSourceFiles`, and `contentChangedSourceFiles` are derived from that same receipt. A save without content change is therefore visible as `mode:"incremental-noop"` plus the `mtimeOnly` cause, while a full fallback reports sources as reanalyzed without pretending all mtimes/content changed. If an editor or watcher already knows the exact changed set, pass `authoritativeChangedFiles` to narrow source scanning; csproj/asmdef descriptor drift and analysis-scope drift are still checked independently. Csproj changes force the affected module's sources through recompilation and surface `descriptorRecompile`. If compatible retained compilation services are unavailable, the response reports `fallbackReason:"compilationStateUnavailable"`; retry with `allowFullFallback:true` or run an ordinary retained full analyze. Shared hot recovery is described above.

**Snapshot-pinned reads.** Every registered `Observe + SharedRead` tool accepts optional `snapshotId`, `expectedSnapshotId`, and `expectedAnalysisGeneration` fields. `snapshotId` selects one exact current or retained graph-only publication; a missing/evicted id never falls through to latest. The server acquires an immutable lease first, then compares any precondition against that exact publication. Historical graph queries work normally, while live-source and Roslyn-backed tools return unavailable because the catalog stores neither source bytes nor semantic services. `lifeblood_batch` accepts 1–32 read calls, fully validates the entire plan before call zero, and runs it serially in input order under one outer lease. Unknown, nested, exclusive, effectful, or conflicting nested selections reject the whole plan. Use `lifeblood_snapshots` to list, pin/name, unpin, evict, or optionally drift-check the default-three/hard-sixteen graph-only catalog. Every successful envelope names the exact snapshot id, generation, and canonical analysis identity used by the result.

## Install

### Option 1. Published global tool (recommended)

```bash
dotnet tool install --global Lifeblood.Server.Mcp
```

Requires the [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0). After install, `lifeblood-mcp` is on `PATH`. Verify by running it directly. It will start, print nothing, and wait for JSON-RPC messages on stdin. Press `Ctrl+C` to exit.

To upgrade later: `dotnet tool update --global Lifeblood.Server.Mcp`.

### Option 2. Built locally from source

For development, or if you want to point at an unreleased build:

```bash
git clone https://github.com/user-hash/Lifeblood.git
cd Lifeblood
dotnet build
```

For shared use, pack and install one exact build into a dedicated tool directory,
then configure every client with that executable path:

```bash
dotnet pack src/Lifeblood.Server.Mcp -c Release -o /path/to/packages
dotnet tool install Lifeblood.Server.Mcp --tool-path /path/to/tools --add-source /path/to/packages --version <exact-package-version>
```

Use `dotnet tool update` for an existing tool directory. Keep the package and
build outputs on the workspace's configured output drive. An already-running
daemon continues executing its loaded build; replacing a package is not a live
service upgrade. Coordinate the handover and verify existing consumers before
calling a rollout complete. Build identity includes the compiled module MVID,
so a repository Debug DLL is unsuitable as a moving shared-service deployment.
The Lifeblood repo ships [`.mcp.json.example`](../.mcp.json.example) with the
installed-tool form. Machine-specific `.mcp.json` remains gitignored.

### Verify the install

```bash
lifeblood-mcp
# (no output: the process is waiting for JSON-RPC on stdin)
```

If `lifeblood-mcp` is not found, check that `~/.dotnet/tools` (or the platform equivalent) is on your `PATH`. The .NET SDK installer adds it on Windows; on macOS/Linux you may need to add it manually.

### Inspect an existing shared service without starting one

Use the repository's PowerShell 7 helper when direct MCP tools are unavailable:

```powershell
$workspace = (Resolve-Path ../MyProject).Path
./tools/inspect-lifeblood-shared.ps1 -Workspace $workspace `
  -Command (Get-Command lifeblood-mcp -ErrorAction Stop).Source `
  -CliCommand (Get-Command lifeblood -ErrorAction Stop).Source `
  -SdkCommand (Get-Command dotnet -ErrorAction Stop).Source `
  -EnvironmentFile src/Example.cs
```

Set the workspace and source-file examples to your project. Run the helper from
the Lifeblood checkout. `-Command` names the installed
MCP executable; `-RuntimeRoot` is optional when the executable already resolves its
runtime. The helper sets `LIFEBLOOD_SHARED_DAEMON_AUTOSTART=0` only in its child,
initializes one shared proxy, reads capabilities and the server catalog, and closes
that proxy in `finally`. An optional `EnvironmentFile` observes that exact retained
scope without diagnostics/compilation/refresh; older or compilation-free services
report it unavailable, without a fallback scan. The default overall deadline is
30 seconds (maximum 60), plus bounded owned-process cleanup. RPC frames are bounded
to 4,194,304 UTF-16 characters. An absent or incompatible daemon returns unavailable
JSON with exit code 1; no private session, daemon start, drain or analysis follows.

The original top-level fields remain. The additive `doctor` object joins resolved
PATH candidates, handshake/endpoint identity, live daemon/publication/base counts,
the selected CLI's `--version --json`, and a separate workspace SDK `--version`
probe. Each identity subprocess has a ten-second limit, 32,768-character bounds
per output stream and owned tree cleanup. A CLI/SDK failure does not erase a healthy
daemon receipt. Known version skew is distinct from alternate paths. SDK probe
selection does not establish which SDK an earlier analysis used. No machine-wide
PATH/runtime setting is changed. Other clients can publish during inspection, so
this is a point observation, not a workspace lock or content-drift check.

For direct clients, `lifeblood_capabilities` is the same cheap first check. A working
transport does not establish which tools a particular client has exposed. Verify
the tools in that client's current catalog separately. A successful direct client
also does not qualify an already-running Unity bridge: check its resolved
`LIFEBLOOD_MCP_COMMAND`/PATH executable and handshake independently. Do not start a
scan to diagnose a missing tool catalog. Supply `-ClientToolNames` from the affected
client and `-ClientCatalogSource` to compare that observation with the server
catalog; omission remains `unobserved`, rather than inferred healthy.

For an optional read-only Unity bridge observation, run the repository reader
through a ready Editor's Pipeline CLI at an ordinary operation boundary:

```powershell
$workspace = (Resolve-Path ../MyUnityProject).Path
$reader = (Resolve-Path ./tools/inspect-lifeblood-bridge.cs).Path
unity command script run --project-path $workspace --file $reader `
  --entry Lifeblood.Tooling.BridgeObservation.Read --format json > ./bridge-observation.json
```

Use paths for your checkout and output root. Pass the saved response as
`-BridgeObservationPath` to the doctor. The reader observes the loaded bridge
assembly, configured command and existing proxy without starting/reconnecting it,
calling an MCP tool or changing preferences. A busy or uninitialized bridge stays
explicit. The doctor labels the receipt as caller-provided and preserves its age;
it does not independently recapture another client's state. The bridge's cached
initialized flag does not prove present daemon liveness, and its handshake server
identity is not retained. See [qualification and remaining limits](VERIFICATION.md).

### Consume structured tool results

The server supports the legacy MCP revision and the structured-content revision
`2025-06-18`. Explicit legacy or omitted revision preserves text results. A modern
revision request negotiates the structured-capable revision and defaults to both
the existing `content[]` text and an equivalent `structuredContent` object.
Lifeblood declaration schema selection (`mcp-protocol-version: v1|v2`) is separate.

Clients that can consume objects without the redundant text copy may initialize
with `capabilities.experimental.lifebloodResultFormat:"structuredOnly"`; `"dual"`
and `"text"` are also supported. The initialize response acknowledges the selected
format and its projection limit under the same experimental capability. A legacy
revision cannot request structured output. Failed negotiation does not change the
connection's previous schema/format selection.

Prefer `result.structuredContent` when present, otherwise parse the existing text
payload. For Unity's typed wrapper, inspect `data.structuredContent`, preserving
the outer success/error and inner MCP `isError` bits. The bridge requests the modern
revision with compatible dual output and accepts older servers' legacy fallback.
Opaque/non-object/multi-content results remain content blocks. Above the stated
character limit the full original text survives and `_meta` reports the projection
fallback; use an existing scoped/compact tool query rather than losing data.

Structured output reduces client parsing work. Dual output increases wire bytes;
structured-only saves the redundant text copy but still costs server parsing and
serialization. See [qualification and measurements](VERIFICATION.md)
and the [MCP tool-result contract](https://modelcontextprotocol.io/specification/2025-06-18/server/tools#structured-content).

### Identify the CLI independently of the service

```powershell
Get-Command lifeblood, lifeblood-mcp -All | Select-Object Name, Source
lifeblood --version --json
```

`lifeblood --version` prints one build version. `--version --json` also reports the
CLI assembly path, process path, runtime, target framework and loaded Roslyn
version, with `executionMode:"standaloneCli"`. It needs no project and performs no
analysis or service attachment. Use capabilities to identify the MCP daemon;
the CLI identity cannot stand in for it. Older tools without this command must
be identified from their installed package or assembly metadata.

For a portable runtime, scope `DOTNET_ROOT_X64` to the launching process or the MCP
client's server environment. Changing the global SDK or `PATH` is unnecessary.
The self-contained Windows CLI bundles its runtime, not an SDK. SDK-style project
evaluation still launches a separately installed SDK host: `DOTNET_HOST_PATH`
selects an existing explicit host first, otherwise the host beside the running
framework installation is preferred, then `dotnet` on PATH. Keep that selection
separate from the CLI's reported runtime. Check all PATH matches when an older
global installation shadows the intended build. Custom `--tool-path` or
`DOTNET_CLI_HOME` installations may use another global-tools directory. Local
SDK selection and the unreproduced earlier evaluation timeout are recorded in the
[qualified scope and remaining diagnostic gap](VERIFICATION.md).
For example, Codex supports an explicit command and per-server environment in
project `.codex/config.toml` ([official MCP configuration](https://developers.openai.com/codex/mcp)):

```toml
[mcp_servers.lifeblood]
command = "lifeblood-mcp"
args = ["--shared", "--shared-key", "/absolute/path/to/workspace"]

# Only for a custom x64 runtime/SDK installation:
# [mcp_servers.lifeblood.env]
# DOTNET_ROOT_X64 = "/absolute/path/to/dotnet"
# DOTNET_HOST_PATH = "/absolute/path/to/dotnet/dotnet"
```

Replace the workspace placeholder with your real absolute path. If a runtime or
SDK override is needed, use its installed location and platform executable name
(`dotnet.exe` on Windows). Keep matching clients on the same Lifeblood build.

### Interpret source freshness separately from semantic confidence

Each envelope's `freshness.summary` states coverage, observed changes, and whether
content was compared or the input inventory recaptured. For example, a check of
256 out of 6,531 paths with zero observed changes is incomplete. Even checking all
tracked mtimes cannot detect timestamp-preserving edits, new inputs or changed
descriptors. `confidence:"Proven"` describes the query against its publication;
it does not certify that today's input files match that publication. Use
`lifeblood_snapshots(checkDrift:true)` when canonical input equality is required.
Compact batches retain the same per-tool freshness evidence.

A snapshot entry's canonical drift result can be `current` while its envelope's
mtime observation is `incomplete`: these receipts describe different checks.
Use the entry's canonical result for input equality. The envelope's generic
recommendation to run `checkDrift:true` need not be repeated when that same
response already contains a successful canonical check.

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
| `LIFEBLOOD_SHARED_IDLE_SECONDS` | disabled | Optional last-client idle interval before the daemon drains, disposes its retained session, releases pipe/mutex ownership, and exits. Unset or invalid values schedule no eviction; set a finite non-negative number only when the operator intentionally trades retained-graph availability for automatic memory reclamation. |
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

For isolated private development only, point at a local DLL without `--shared`.
That process owns separate state; rebuilding the DLL does not change a process
that already loaded it. Keep ordinary clients on the installed shared build:

```json
{
  "mcpServers": {
    "lifeblood": {
      "command": "dotnet",
      "args": ["/path/to/Lifeblood/src/Lifeblood.Server.Mcp/bin/Debug/net11.0/Lifeblood.Server.Mcp.dll"]
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
- **Arguments**: `--shared`
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
                                        │   (shared .NET 11 daemon)    │
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

- **No assembly conflicts.** Lifeblood uses its SDK-matched Roslyn build (current provenance in [STATUS](STATUS.md)). Unity ships its own compiler assemblies. Sidecar isolation keeps those assembly identities separate.
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

**Step 4. Open Unity.** The Editor compiles the bridge stubs. Coplay auto-discovers their typed nested `Parameters` properties and polling metadata. The Unity bridge surfaces a curated subset of the [MCP tools](TOOLS.md), with `lifeblood_analyze_project` wrapping analyze and owning the Unity project path, alongside the built-in Unity tools. The remaining tools are reachable through a direct `lifeblood-mcp --shared` client and observe the same daemon publication.

**Step 5. Connect any MCP client to Coplay MCP for Unity** following Coplay's own setup guide. From the client's perspective, `lifeblood_analyze_project`, `lifeblood_lookup`, `lifeblood_blast_radius`, and the other bridge tools appear next to Coplay's Unity tools on one connection.

**Step 6. First-call flow.** From the connected MCP client, call:

```
lifeblood_analyze_project incremental=false readOnly=false defineProfiles=["Editor","Player"]
```

The bridge injects the current Unity project root, starts the installed shared proxy, performs the MCP `initialize` handshake, and returns a polling receipt before Coplay's synchronous deadline. Coplay status calls need only `action:"status"`: the bridge resolves the one unconsumed call by tool identity, while retaining original-argument identity solely to coalesce exact retries or reject a conflicting replacement. Subsequent bridge and direct-agent calls reuse the same daemon graph.

### Locating the server command

`LifebloodBridgeClient` resolves the executable in this order:

1. `EditorPrefs` key `Lifeblood_McpCommand` (Unity-side, persisted per machine).
2. Environment variable `LIFEBLOOD_MCP_COMMAND`.
3. The standard `~/.dotnet/tools/lifeblood-mcp` shim, then `PATH`.

Overrides name one executable, not a command line. The bridge owns `--shared --shared-key <UnityProjectRoot>` so every admission and status call targets the same workspace identity.

For a portable x64 .NET runtime, set `EditorPrefs` key
`Lifeblood_DotnetRootX64` or environment variable `LIFEBLOOD_DOTNET_ROOT_X64`.
The preference wins when present. The bridge passes that value as
`DOTNET_ROOT_X64` only to its child proxy; otherwise normal inherited runtime
resolution applies. Pair this with `Lifeblood_McpCommand` pointing to the same
installed MCP build used by direct clients. Changing the machine-wide SDK is
unnecessary. A failed or malformed initialize handshake disposes the attempted
proxy and returns an error; it never marks the bridge connected. A terminal MCP
`isError:true` result becomes Coplay `ErrorResponse` with the original MCP result
preserved as data. A valid compile result containing compilation diagnostics
remains a successful transport response.

### Lifecycle

- **Domain reload.** The bridge disposes Unity's proxy before recompilation. The daemon keeps its base while another client lease exists; the next Unity call reconnects.
- **Unity quit.** Unity's proxy is disposed; the daemon retains the latest base unless an operator-configured idle policy or explicit maintenance drain ends it.
- **Crash recovery.** Process exit or pipe closure marks the proxy broken. The next independent call connects a replacement; failed effectful requests are never replayed automatically. Tool duration is observed through polling rather than a Lifeblood-authored wall-clock deadline.

### When to use Unity bridge vs standalone CLI

- **Use the bridge** when you want Lifeblood semantic queries available to an AI agent inside the Unity Editor, in the same connection as Coplay's scene/asset tools, with no separate MCP client wiring.
- **Use a standalone client** (Claude Code, Cursor) connected directly to `lifeblood-mcp` when you want Lifeblood without the Unity Editor running, or when you want the lowest-latency path with no Coplay layer in between.

You can run both at the same time. The canonical bridge always uses shared mode, so direct clients launched with `--shared` and the same workspace identity attach to that daemon-owned session. One analyze refreshes the graph every attached agent sees. Identity, workspace binding, client leases, idle drain, coalescing, cancellation, pinned batches and bounded graph-only history have qualified deployment receipts. Those checks do not establish sustained memory stability; current memory work and other limits remain explicit in [`STATUS.md`](STATUS.md). Keep private stdio available only as the rollback configuration.

---

## First steps after connecting

1. Inspect `lifeblood_capabilities` through a direct client or the attach-only helper first. Reuse a suitable existing publication. When an analysis is needed, use Unity's `lifeblood_analyze_project` or direct `lifeblood_analyze` with `projectPath` and the intended profiles.
2. Don't know the canonical id of a symbol? Call `lifeblood_resolve_short_name name="MyType"` to discover it.
3. Use `lifeblood_lookup`, `lifeblood_dependencies`, and `lifeblood_blast_radius` to query the graph. Truncated method ids and bare short names work because every read-side tool routes through `ISymbolResolver`.
4. For multi-tool evidence, copy `envelope.snapshotId` into `expectedSnapshotId` or use `lifeblood_batch` so a concurrent refresh cannot mix generations. Use `lifeblood_snapshots` and exact `snapshotId` selection only when a bounded graph-only investigation lane must survive a refresh.
5. Use `lifeblood_file_impact` to check which files break if you change a given file.
6. For write-side Roslyn features, use `lifeblood_find_references` (pass `includeDeclarations=true` to also list every partial declaration site), `lifeblood_execute` (the script globals `Graph`, `Compilations`, and `ModuleDependencies` give you typed access to the loaded semantic state), and `lifeblood_compile_check`.
7. After code changes, call `lifeblood_analyze` with `incremental: true` for fast re-analysis. Only changed modules recompile, and csproj edits trigger re-discovery.

The graph stays in memory for the session. Every tool shares the same loaded workspace.

## Notes for Unity, .NET Framework, and Mono workspaces

If your modules ship their own BCL via csproj `<Reference Include="netstandard|mscorlib|System.Runtime">` or via `<HintPath>` to a vendored framework DLL, Lifeblood detects this at discovery time and switches that module to `BclOwnership=ModuleProvided`. The host BCL is NOT injected for those modules. This prevents the BCL double-load that would otherwise produce CS0433 and CS0518 on every System usage and silently zero out call-graph extraction. No configuration required.

If you see a flood of CS0227 false positives on a module that uses `unsafe` blocks, confirm the csproj has `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`. Lifeblood reads this from the csproj and passes `WithAllowUnsafe(true)` into the compilation.
