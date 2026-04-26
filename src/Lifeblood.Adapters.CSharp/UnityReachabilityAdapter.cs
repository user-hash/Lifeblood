using System.Collections.Generic;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Reference implementation of <see cref="IUnityReachabilityProvider"/>.
/// Classifies a symbol as runtime-reachable when it matches one of the
/// Unity framework-dispatch patterns that Roslyn cannot see:
///
/// <list type="number">
///   <item><b>Entrypoint attributes.</b> Methods marked with one of the
///     Unity entrypoint attributes (<c>RuntimeInitializeOnLoadMethod</c>,
///     <c>InitializeOnLoadMethod</c>, <c>MenuItem</c>, etc.) are invoked
///     directly by the Unity engine via reflection.</item>
///   <item><b>MonoBehaviour magic methods.</b> Methods with the magic
///     names Unity dispatches (<c>Awake</c>, <c>Update</c>, ...) on
///     types that derive transitively from <c>UnityEngine.MonoBehaviour</c>
///     or <c>UnityEngine.ScriptableObject</c>. Detected via the graph's
///     <see cref="EdgeKind.Inherits"/> chain.</item>
/// </list>
///
/// The provider reads the C# extractor's <c>Properties["attributes"]</c>
/// payload (semicolon-separated simple attribute names) so it does not
/// need a Roslyn compilation at query time — it works against any graph
/// that was extracted by the C# adapter, including JSON-graph imports
/// of previously-analyzed Unity workspaces. Phase P3 (2026-04-26).
///
/// See <c>INV-UNITY-001</c> in CLAUDE.md.
/// </summary>
public sealed class UnityReachabilityAdapter : IUnityReachabilityProvider
{
    /// <summary>
    /// Roster of attribute names that mark a method as a Unity entry
    /// point. Stored without the "Attribute" suffix because the
    /// extractor strips it before recording. Sorted alphabetically for
    /// reviewer scanning; lookup uses the hashset.
    /// </summary>
    private static readonly HashSet<string> EntrypointAttributes = new(System.StringComparer.Ordinal)
    {
        "ContextMenu",
        "ContextMenuItem",
        "CustomEditor",
        "CustomPropertyDrawer",
        "DidReloadScripts",
        "InitializeOnEnterPlayMode",
        "InitializeOnLoad",
        "InitializeOnLoadMethod",
        "MenuItem",
        "PostProcessBuild",
        "PostProcessScene",
        "PostProcessSceneAttribute",
        "PreserveAttribute",
        "PropertyDrawer",
        "RuntimeInitializeOnLoadMethod",
        "ScriptedImporter",
        "Test",                // NUnit: any test runner-invoked method
        "TestCase",            // NUnit
        "TestFixture",         // NUnit
        "UnityTest",           // Unity Test Framework
    };

    /// <summary>
    /// MonoBehaviour magic-method names. Reachable when the containing
    /// type's transitive Inherits chain reaches MonoBehaviour or
    /// ScriptableObject. Unity dispatches these via the engine; no
    /// static call site exists. Sourced from Unity's documented message
    /// catalog plus the EditorWindow / ScriptableObject lifecycle.
    /// </summary>
    private static readonly HashSet<string> MonoBehaviourMessages = new(System.StringComparer.Ordinal)
    {
        // Lifecycle
        "Awake", "Start", "OnEnable", "OnDisable", "OnDestroy",
        "Reset", "OnValidate",
        // Per-frame
        "Update", "FixedUpdate", "LateUpdate",
        // Application
        "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit",
        // Rendering / GUI
        "OnGUI", "OnDrawGizmos", "OnDrawGizmosSelected",
        "OnRenderImage", "OnPostRender", "OnPreRender", "OnPreCull",
        "OnBecameVisible", "OnBecameInvisible",
        "OnWillRenderObject", "OnRenderObject",
        // Physics — 3D
        "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay",
        "OnTriggerEnter",   "OnTriggerExit",   "OnTriggerStay",
        "OnControllerColliderHit",
        // Physics — 2D
        "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
        "OnTriggerEnter2D",   "OnTriggerExit2D",   "OnTriggerStay2D",
        // Audio
        "OnAudioFilterRead",
        // Mouse
        "OnMouseEnter", "OnMouseExit", "OnMouseDown", "OnMouseUp",
        "OnMouseUpAsButton", "OnMouseDrag", "OnMouseOver",
        // Particles
        "OnParticleCollision", "OnParticleSystemStopped", "OnParticleTrigger",
        // Misc lifecycle
        "OnTransformChildrenChanged", "OnTransformParentChanged",
        "OnApplicationGetFocus", "OnDisconnectedFromServer", "OnConnectedToServer",
        // Editor / ScriptableObject lifecycle (also magic on those bases)
        "OnInspectorGUI", "OnSceneGUI",
    };

    /// <summary>
    /// Type names whose presence in the inheritance chain marks a type
    /// as a "Unity message receiver". Stored as fully-qualified names
    /// because that's how the graph's Inherits edges are keyed (after
    /// the extractor strips namespaces it's still the QualifiedName).
    /// </summary>
    private static readonly HashSet<string> UnityMessageReceiverBases = new(System.StringComparer.Ordinal)
    {
        "UnityEngine.MonoBehaviour",
        "UnityEngine.ScriptableObject",
        "UnityEditor.Editor",
        "UnityEditor.EditorWindow",
        "UnityEngine.StateMachineBehaviour",
    };

    public bool IsRuntimeReachable(SemanticGraph graph, Symbol sym, out string reason)
    {
        reason = "";

        // 1. Entrypoint attribute on the symbol itself (method or type).
        if (HasEntrypointAttribute(sym, out var attrName))
        {
            reason = $"Reachable via [{attrName}] runtime entrypoint";
            return true;
        }

        // 2. MonoBehaviour magic method — only meaningful for methods
        //    on a type whose inheritance chain reaches a Unity base.
        if (sym.Kind == SymbolKind.Method
            && MonoBehaviourMessages.Contains(sym.Name)
            && !string.IsNullOrEmpty(sym.ParentId)
            && InheritsFromUnityMessageReceiver(graph, sym.ParentId))
        {
            reason = $"MonoBehaviour magic method '{sym.Name}' on a Unity-message-receiver type";
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the symbol declares any of the Unity entrypoint
    /// attributes in its extractor-recorded
    /// <c>Properties["attributes"]</c> string.
    /// </summary>
    private static bool HasEntrypointAttribute(Symbol sym, out string matchedName)
    {
        matchedName = "";
        if (sym.Properties == null) return false;
        if (!sym.Properties.TryGetValue("attributes", out var attrs)) return false;
        if (string.IsNullOrEmpty(attrs)) return false;
        foreach (var name in attrs.Split(';'))
        {
            if (EntrypointAttributes.Contains(name))
            {
                matchedName = name;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="typeId"/> transitively inherits from a
    /// known Unity message-receiver base. Walks the chain via the
    /// extractor-recorded <c>Properties["baseType"]</c> on each type —
    /// works even when the base lives in a different assembly
    /// (UnityEngine.MonoBehaviour from UnityEngine.dll) because the
    /// graph drops dangling Inherits edges to non-loaded targets but
    /// the property carries the FQN regardless. Bounded hop cap to
    /// shrug off malformed-graph cycles; Unity inheritance chains
    /// never exceed single digits in practice.
    /// </summary>
    private static bool InheritsFromUnityMessageReceiver(SemanticGraph graph, string typeId)
    {
        const int maxHops = 32;
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var currentSym = graph.GetSymbol(typeId);
        for (int i = 0; i < maxHops && currentSym != null; i++)
        {
            if (!seen.Add(currentSym.Id)) return false; // cycle guard

            // Read base FQN from the type's extractor-recorded property.
            // Empty / missing means we've hit a root or an extractor that
            // didn't record it (older snapshots). In that case, fall back
            // to walking Inherits edges to in-graph targets.
            string? baseFqn = null;
            if (currentSym.Properties != null
                && currentSym.Properties.TryGetValue("baseType", out var b)
                && !string.IsNullOrEmpty(b))
            {
                baseFqn = b;
            }

            if (!string.IsNullOrEmpty(baseFqn))
            {
                if (UnityMessageReceiverBases.Contains(baseFqn))
                    return true;
                // Look up the next link in the chain by FQN. If the base
                // is in-graph the lookup finds the symbol; if external
                // the FQN already failed the receiver match above so
                // we're done (external base that isn't a Unity receiver
                // is the chain-terminator).
                currentSym = graph.GetSymbol("type:" + baseFqn);
                continue;
            }

            // Fallback: walk the first outgoing Inherits edge.
            string? nextId = null;
            foreach (int idx in graph.GetOutgoingEdgeIndexes(currentSym.Id))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind != EdgeKind.Inherits) continue;
                var rawTarget = edge.TargetId;
                var qualifiedFromId = StripTypePrefix(rawTarget);
                if (UnityMessageReceiverBases.Contains(qualifiedFromId))
                    return true;
                var targetSym = graph.GetSymbol(rawTarget);
                if (targetSym != null && UnityMessageReceiverBases.Contains(targetSym.QualifiedName))
                    return true;
                nextId = rawTarget;
                break;
            }
            if (nextId == null) return false;
            currentSym = graph.GetSymbol(nextId);
        }
        return false;
    }

    private static string StripTypePrefix(string id)
        => id.StartsWith("type:", System.StringComparison.Ordinal) ? id.Substring(5) : id;
}
