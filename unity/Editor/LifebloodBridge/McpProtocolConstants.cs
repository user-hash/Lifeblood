namespace Lifeblood.UnityBridge
{
    /// <summary>
    /// MCP wire-format constants used by the Unity bridge client.
    ///
    /// <para>
    /// <b>This file is a mirror.</b> The single source of truth is
    /// <c>Lifeblood.Connectors.Mcp.McpProtocolSpec</c> in the main
    /// Lifeblood solution. The Unity bridge compiles inside Unity's
    /// own assembly context and cannot take a <c>ProjectReference</c>
    /// to Lifeblood assemblies, so the constants are duplicated here.
    /// </para>
    ///
    /// <para>
    /// Drift is prevented by <c>McpProtocolSourceOfTruthTests</c> in
    /// <c>Lifeblood.Tests</c>, which reads this file as text, parses
    /// out the constant values with a regex, and asserts byte-equality
    /// against <see cref="Lifeblood.Connectors.Mcp.McpProtocolSpec"/>.
    /// Editing a value here without updating the source of truth — or
    /// vice versa — fails the ratchet on CI.
    /// </para>
    ///
    /// <para>
    /// <b>INV-MCP-003</b>: every MCP wire constant lives in exactly one
    /// canonical site per side (server: <c>McpProtocolSpec</c>; Unity
    /// client: this file). No bare protocol strings elsewhere.
    /// </para>
    /// </summary>
    internal static class McpProtocolConstants
    {
        // === Protocol version ===
        public const string SupportedVersion = "2024-11-05";

        // === JSON-RPC methods ===
        public const string MethodInitialize = "initialize";
        public const string MethodToolsList = "tools/list";
        public const string MethodToolsCall = "tools/call";
        public const string MethodShutdown = "shutdown";

        // === Notifications — canonical spec forms only ===
        // (Legacy aliases are never sent by first-party clients.)
        public const string NotificationInitialized = "notifications/initialized";
        public const string NotificationCancelled = "notifications/cancelled";

        // === Client identity sent in initialize.params.clientInfo ===
        public const string ClientInfoName = "lifeblood-unity-bridge";
        public const string ClientInfoVersion = "1.0";

        // === JSON-RPC framing ===
        public const string JsonRpcVersion = "2.0";
    }
}
