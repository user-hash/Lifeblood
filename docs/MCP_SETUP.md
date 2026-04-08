# MCP Server Setup

Lifeblood's MCP server (`lifeblood-mcp`) gives AI agents 17 tools over stdio JSON-RPC. This page has copy-paste configs for every major MCP client.

## Prerequisites

```bash
dotnet tool install --global Lifeblood.Server.Mcp
```

Verify: `lifeblood-mcp` should start and wait for JSON-RPC on stdin. Press Ctrl+C to exit.

---

## Claude Code

Add to `.mcp.json` in your project root (or `~/.claude/.mcp.json` for global):

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

Or from source (development):

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

## First steps after connecting

1. Call `lifeblood_analyze` with your project path to load the semantic graph
2. Use `lifeblood_lookup`, `lifeblood_dependencies`, `lifeblood_blast_radius` to query it
3. Use `lifeblood_find_references`, `lifeblood_execute`, `lifeblood_compile_check` for write-side Roslyn features

The graph stays in memory for the session. All 17 tools share the same loaded workspace.
