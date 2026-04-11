using System.Collections.Generic;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Single source of truth for Model Context Protocol wire-format
/// constants shared by the Lifeblood MCP server
/// (<c>Lifeblood.Server.Mcp</c>) and every MCP client that speaks to
/// it (Unity bridge today, future first-party clients tomorrow).
///
/// <para>
/// The server consumes these constants directly via project reference.
/// Clients that cannot take a project reference — notably the Unity
/// bridge, which compiles inside Unity's own assembly context — mirror
/// the constants into a standalone file and a ratchet test in
/// <c>Lifeblood.Tests</c> asserts that the mirror stays byte-equal to
/// this source.
/// </para>
///
/// <para>
/// <b>INV-MCP-003:</b> Every MCP wire constant (protocol version,
/// method name, notification name) lives here and nowhere else. Adding
/// a hardcoded protocol string anywhere else in the repo — server,
/// client, or test — is a drift-class bug and must fail the
/// <c>McpProtocolSourceOfTruthTests</c> ratchet.
/// </para>
/// </summary>
public static class McpProtocolSpec
{
    /// <summary>
    /// The MCP specification version this Lifeblood release implements.
    /// Bumping this is the only edit required when we migrate to a newer
    /// MCP spec revision. Single site, no duplicates.
    /// </summary>
    public const string SupportedVersion = "2024-11-05";

    /// <summary>
    /// JSON-RPC <c>method</c> names for MCP requests the server handles.
    /// </summary>
    public static class Methods
    {
        public const string Initialize = "initialize";
        public const string ToolsList = "tools/list";
        public const string ToolsCall = "tools/call";
        public const string Shutdown = "shutdown";
    }

    /// <summary>
    /// JSON-RPC <c>method</c> names for MCP notifications the server
    /// recognises. Notifications never receive a response body per
    /// INV-MCP-002, but a server that does not <i>recognise</i> the
    /// notification logs it as an unknown-notification warning, which
    /// noisy clients would flood.
    /// </summary>
    public static class Notifications
    {
        /// <summary>Canonical spec form — clients should send this.</summary>
        public const string Initialized = "notifications/initialized";

        /// <summary>
        /// Legacy alias still accepted by the server during the deprecation
        /// window. New clients MUST use <see cref="Initialized"/>; the
        /// ratchet test asserts no first-party client sends this value.
        /// </summary>
        public const string InitializedLegacyAlias = "initialized";

        public const string Cancelled = "notifications/cancelled";
        public const string CancelRequest = "$/cancelRequest";
    }

    /// <summary>
    /// All notification method names the dispatcher recognises as valid
    /// MCP traffic. Built from <see cref="Notifications"/>. The server's
    /// <c>McpDispatcher.KnownNotifications</c> set is derived directly
    /// from this enumeration so the two can never drift.
    /// </summary>
    public static IReadOnlyCollection<string> AllKnownNotifications { get; } = new HashSet<string>(System.StringComparer.Ordinal)
    {
        Notifications.Initialized,
        Notifications.InitializedLegacyAlias,
        Notifications.Cancelled,
        Notifications.CancelRequest,
    };

    /// <summary>
    /// Notification names that are first-class canonical spec forms.
    /// First-party clients MUST send notifications from this set only.
    /// </summary>
    public static IReadOnlyCollection<string> CanonicalNotifications { get; } = new HashSet<string>(System.StringComparer.Ordinal)
    {
        Notifications.Initialized,
        Notifications.Cancelled,
        Notifications.CancelRequest,
    };

    /// <summary>
    /// Notification names that are accepted for backwards compatibility
    /// only. Sending one of these from a first-party client is a lint
    /// violation — use the canonical form.
    /// </summary>
    public static IReadOnlyCollection<string> LegacyNotificationAliases { get; } = new HashSet<string>(System.StringComparer.Ordinal)
    {
        Notifications.InitializedLegacyAlias,
    };
}
