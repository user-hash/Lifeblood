using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Lifeblood.Application.Ports.Infrastructure;
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
///     types that derive transitively from a known Unity message-receiver
///     root such as <c>UnityEngine.MonoBehaviour</c> or
///     <c>UnityEditor.EditorWindow</c>. Detected via the graph's
///     <see cref="EdgeKind.Inherits"/> chain and extractor-recorded base
///     type facts.</item>
/// </list>
///
/// The provider reads the C# extractor's <c>Properties["attributes"]</c>
/// payload (semicolon-separated simple attribute names) so it does not
/// need a Roslyn compilation at query time — it works against any graph
/// that was extracted by the C# adapter, including JSON-graph imports
/// of previously-analyzed Unity workspaces. See <c>INV-UNITY-001</c>.
/// </summary>
public sealed class UnityReachabilityAdapter : IUnityReachabilityProvider
{
    private readonly IFileSystem _fs;
    private readonly ConditionalWeakTable<SemanticGraph, UnityAssetReachabilityIndex> _assetIndexes = new();

    public UnityReachabilityAdapter()
        : this(new PhysicalFileSystem())
    {
    }

    public UnityReachabilityAdapter(IFileSystem fs)
    {
        _fs = fs;
    }

    /// <summary>
    /// Roster of attribute names that mark a method as a Unity entry
    /// point. Stored without the "Attribute" suffix because the
    /// extractor strips it before recording. Sorted alphabetically for
    /// reviewer scanning; lookup uses the hashset.
    /// </summary>
    private static readonly HashSet<string> EntrypointAttributes = new(System.StringComparer.Ordinal)
    {
        // ── Unity Editor — reflection-discovered entry points ──
        "ContextMenu",
        "ContextMenuItem",
        "CustomEditor",
        "CustomPropertyDrawer",
        "DidReloadScripts",
        "InitializeOnEnterPlayMode",
        "InitializeOnLoad",
        "InitializeOnLoadMethod",
        "MenuItem",
        "OnOpenAsset",         // Editor: invoked when a matching asset is double-clicked
        "PostProcessBuild",
        "PostProcessScene",
        "PostProcessSceneAttribute",
        "PreserveAttribute",
        "PropertyDrawer",
        "RuntimeInitializeOnLoadMethod",
        "ScriptedImporter",
        "SettingsProvider",    // Editor: returns a SettingsProvider for Project Settings / Preferences
        "SettingsProviderGroup",
        "Shortcut",            // Editor: keyboard shortcut binding
        "ShortcutAttribute",
        // ── Native interop ──
        "BurstCompile",        // Burst-compiled at build time; method body is the compile target
        "MonoPInvokeCallback", // Reverse P/Invoke target — invoked from native
        // ── NUnit / Unity Test Framework ──
        "Test",                // NUnit: any test runner-invoked method
        "TestCase",            // NUnit
        "TestCaseSource",      // NUnit
        "TestFixture",         // NUnit
        "TestFixtureSource",   // NUnit
        "Theory",              // NUnit
        "SetUp",               // NUnit fixture lifecycle
        "TearDown",
        "OneTimeSetUp",
        "OneTimeTearDown",
        "UnityTest",           // Unity Test Framework
        "UnitySetUp",
        "UnityTearDown",
    };

    /// <summary>
    /// MonoBehaviour magic-method names. Reachable when the containing
    /// type's transitive Inherits chain reaches a known Unity message
    /// receiver. Unity dispatches these via the engine; no static call
    /// site exists. Sourced from Unity's documented message catalog plus
    /// the EditorWindow / ScriptableObject lifecycle.
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

        // 3. Type liveness via contained entrypoint child. Unity Editor
        //    reflection (e.g. `[SettingsProvider]`, `[MenuItem]`,
        //    `[InitializeOnLoadMethod]`) targets a STATIC METHOD; the
        //    containing type has no incoming edges and would otherwise
        //    surface as a false-positive dead type. A type is reachable
        //    if ANY of its contained methods (or, conservatively, any
        //    contained member) declares an entrypoint attribute. Walks
        //    only direct Contains-children — sufficient for the standard
        //    Unity pattern (entrypoint on a static method one level deep
        //    inside the host type) and bounded.
        if (sym.Kind == SymbolKind.Type)
        {
            foreach (var child in graph.ChildrenOf(sym.Id))
            {
                if (HasEntrypointAttribute(child, out var childAttr))
                {
                    reason = $"Type contains member tagged [{childAttr}] (Unity reflection entrypoint)";
                    return true;
                }
            }
        }

        // 4. Unity serialized asset reachability. Persistent UnityEvent
        //    calls live in .prefab/.unity/.asset YAML, not in source. When
        //    a call names a method target, that method and its declaring type
        //    are runtime-reachable even with zero semantic incoming edges.
        //    The index is built lazily per graph and cached weakly so repeated
        //    dead_code checks do not re-scan the Unity asset tree.
        var assetIndex = GetAssetReachabilityIndex(graph, sym);
        if (assetIndex.TryGetReason(sym.Id, out var assetReason))
        {
            reason = assetReason;
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
        if (!sym.Properties.TryGetValue(SymbolPropertyKeys.Attributes, out var attrs)) return false;
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
            if (HasUnityReceiverInRecordedBaseChain(currentSym))
                return true;

            string? baseFqn = null;
            if (currentSym.Properties != null
                && currentSym.Properties.TryGetValue(SymbolPropertyKeys.BaseType, out var b)
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

    private static bool HasUnityReceiverInRecordedBaseChain(Symbol sym)
    {
        if (sym.Properties == null) return false;
        if (!sym.Properties.TryGetValue(SymbolPropertyKeys.BaseTypeChain, out var chain)
            || string.IsNullOrEmpty(chain))
            return false;

        foreach (var baseFqn in chain.Split(';'))
        {
            if (UnityMessageReceiverBases.Contains(baseFqn))
                return true;
        }

        return false;
    }

    private static string StripTypePrefix(string id)
        => id.StartsWith("type:", System.StringComparison.Ordinal) ? id.Substring(5) : id;

    private UnityAssetReachabilityIndex GetAssetReachabilityIndex(SemanticGraph graph, Symbol sym)
    {
        if (!TryInferUnityProjectRoot(sym.FilePath, out var projectRoot))
            return UnityAssetReachabilityIndex.Empty;

        return _assetIndexes.GetValue(graph, _ => BuildAssetReachabilityIndex(graph, projectRoot));
    }

    private UnityAssetReachabilityIndex BuildAssetReachabilityIndex(SemanticGraph graph, string projectRoot)
    {
        var assetsRoot = System.IO.Path.Combine(projectRoot, "Assets");
        if (!_fs.DirectoryExists(assetsRoot))
            return UnityAssetReachabilityIndex.Empty;

        var typeSymbolsByQualifiedName = new Dictionary<string, List<Symbol>>(System.StringComparer.Ordinal);
        var typeSymbolsByFile = new Dictionary<string, List<Symbol>>(System.StringComparer.OrdinalIgnoreCase);
        var methodSymbolsByTypeAndName = new Dictionary<string, List<Symbol>>(System.StringComparer.Ordinal);
        foreach (var symbol in graph.Symbols)
        {
            if (symbol.Kind == SymbolKind.Type && IsUnderAssets(projectRoot, symbol.FilePath))
            {
                if (!typeSymbolsByQualifiedName.TryGetValue(symbol.QualifiedName, out var byName))
                {
                    byName = new List<Symbol>();
                    typeSymbolsByQualifiedName[symbol.QualifiedName] = byName;
                }
                byName.Add(symbol);

                if (!string.IsNullOrEmpty(symbol.FilePath))
                {
                    var abs = System.IO.Path.GetFullPath(symbol.FilePath);
                    if (!typeSymbolsByFile.TryGetValue(abs, out var byFile))
                    {
                        byFile = new List<Symbol>();
                        typeSymbolsByFile[abs] = byFile;
                    }
                    byFile.Add(symbol);
                }
            }
            else if (symbol.Kind == SymbolKind.Method && !string.IsNullOrEmpty(symbol.ParentId))
            {
                var parent = graph.GetSymbol(symbol.ParentId);
                if (parent == null || string.IsNullOrEmpty(parent.QualifiedName)) continue;
                var key = MethodKey(parent.QualifiedName, symbol.Name);
                if (!methodSymbolsByTypeAndName.TryGetValue(key, out var methods))
                {
                    methods = new List<Symbol>();
                    methodSymbolsByTypeAndName[key] = methods;
                }
                methods.Add(symbol);
            }
        }

        var scriptGuidToTypes = BuildScriptGuidTypeMap(typeSymbolsByFile);
        if (scriptGuidToTypes.Count == 0)
            return UnityAssetReachabilityIndex.Empty;

        var reasons = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var assetPath in EnumerateUnityAssetFiles(assetsRoot))
        {
            string text;
            try { text = _fs.ReadAllText(assetPath); }
            catch { continue; }
            if (string.IsNullOrEmpty(text)) continue;

            var fileIdToTypeNames = BuildFileIdTypeMap(text, scriptGuidToTypes);
            foreach (var call in EnumeratePersistentCalls(text))
            {
                if (string.IsNullOrWhiteSpace(call.MethodName)) continue;

                var targetTypeNames = new List<string>();
                if (!string.IsNullOrWhiteSpace(call.AssemblyTypeName))
                    targetTypeNames.Add(NormalizeUnityAssemblyTypeName(call.AssemblyTypeName));
                else if (!string.IsNullOrWhiteSpace(call.TargetFileId)
                         && fileIdToTypeNames.TryGetValue(call.TargetFileId, out var mapped))
                    targetTypeNames.AddRange(mapped);

                foreach (var typeName in targetTypeNames)
                {
                    if (string.IsNullOrWhiteSpace(typeName)) continue;
                    var methodKey = MethodKey(typeName, call.MethodName);
                    if (!methodSymbolsByTypeAndName.TryGetValue(methodKey, out var methodSymbols))
                        continue;

                    foreach (var method in methodSymbols)
                    {
                        var reason = $"Reachable via UnityEvent persistent call '{call.MethodName}' in {ToRelativeUnityPath(projectRoot, assetPath)}";
                        reasons[method.Id] = reason;
                        if (!string.IsNullOrEmpty(method.ParentId))
                            reasons[method.ParentId] = $"Type contains UnityEvent persistent-call target in {ToRelativeUnityPath(projectRoot, assetPath)}";
                    }
                }
            }
        }

        return reasons.Count == 0
            ? UnityAssetReachabilityIndex.Empty
            : new UnityAssetReachabilityIndex(reasons);
    }

    private Dictionary<string, List<string>> BuildScriptGuidTypeMap(Dictionary<string, List<Symbol>> typeSymbolsByFile)
    {
        var map = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (sourcePath, typeSymbols) in typeSymbolsByFile)
        {
            var metaPath = sourcePath + ".meta";
            if (!_fs.FileExists(metaPath)) continue;

            string meta;
            try { meta = _fs.ReadAllText(metaPath); }
            catch { continue; }

            var guid = ExtractGuid(meta);
            if (string.IsNullOrEmpty(guid)) continue;

            var fileStem = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
            var preferred = typeSymbols
                .Where(t => string.Equals(t.Name, fileStem, System.StringComparison.Ordinal))
                .ToList();
            var candidates = preferred.Count > 0 ? preferred : typeSymbols;
            map[guid] = candidates
                .Select(t => t.QualifiedName)
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Distinct(System.StringComparer.Ordinal)
                .ToList();
        }
        return map;
    }

    private IEnumerable<string> EnumerateUnityAssetFiles(string assetsRoot)
    {
        foreach (var pattern in new[] { "*.prefab", "*.unity", "*.asset" })
        {
            string[] files;
            try { files = _fs.FindFiles(assetsRoot, pattern, recursive: true); }
            catch { continue; }
            foreach (var file in files)
                yield return file;
        }
    }

    private static Dictionary<string, List<string>> BuildFileIdTypeMap(
        string yaml,
        Dictionary<string, List<string>> scriptGuidToTypes)
    {
        var map = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
        foreach (Match match in MonoBehaviourBlockRegex.Matches(yaml))
        {
            var fileId = match.Groups["fileId"].Value;
            var body = match.Groups["body"].Value;
            var scriptMatch = ScriptGuidRegex.Match(body);
            if (!scriptMatch.Success) continue;
            var guid = scriptMatch.Groups["guid"].Value;
            if (scriptGuidToTypes.TryGetValue(guid, out var typeNames))
                map[fileId] = typeNames;
        }
        return map;
    }

    private static IEnumerable<PersistentCall> EnumeratePersistentCalls(string yaml)
    {
        foreach (Match match in PersistentCallRegex.Matches(yaml))
        {
            yield return new PersistentCall(
                TargetFileId: match.Groups["target"].Value.Trim(),
                AssemblyTypeName: match.Groups["type"].Value.Trim(),
                MethodName: match.Groups["method"].Value.Trim());
        }
    }

    private static bool TryInferUnityProjectRoot(string filePath, out string projectRoot)
    {
        projectRoot = "";
        if (string.IsNullOrEmpty(filePath)) return false;
        var full = System.IO.Path.GetFullPath(filePath).Replace('\\', '/');
        var marker = "/Assets/";
        var idx = full.IndexOf(marker, System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        projectRoot = full.Substring(0, idx).Replace('/', System.IO.Path.DirectorySeparatorChar);
        return !string.IsNullOrEmpty(projectRoot);
    }

    private static bool IsUnderAssets(string projectRoot, string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var full = System.IO.Path.GetFullPath(filePath).Replace('\\', '/');
        var root = System.IO.Path.GetFullPath(projectRoot).Replace('\\', '/').TrimEnd('/');
        return full.StartsWith(root + "/Assets/", System.StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractGuid(string metaText)
    {
        var match = MetaGuidRegex.Match(metaText);
        return match.Success ? match.Groups["guid"].Value : "";
    }

    private static string NormalizeUnityAssemblyTypeName(string raw)
    {
        var typeName = raw.Split(',')[0].Trim();
        return typeName;
    }

    private static string MethodKey(string typeName, string methodName) => typeName + "::" + methodName;

    private static string ToRelativeUnityPath(string projectRoot, string path)
    {
        var fullRoot = System.IO.Path.GetFullPath(projectRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (fullPath.StartsWith(fullRoot, System.StringComparison.OrdinalIgnoreCase))
            return fullPath.Substring(fullRoot.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).Replace('\\', '/');
        return path.Replace('\\', '/');
    }

    private sealed record PersistentCall(string TargetFileId, string AssemblyTypeName, string MethodName);

    private sealed class UnityAssetReachabilityIndex
    {
        public static readonly UnityAssetReachabilityIndex Empty = new(new Dictionary<string, string>(System.StringComparer.Ordinal));

        private readonly Dictionary<string, string> _reasons;

        public UnityAssetReachabilityIndex(Dictionary<string, string> reasons) => _reasons = reasons;

        public bool TryGetReason(string symbolId, out string reason) => _reasons.TryGetValue(symbolId, out reason!);
    }

    private static readonly Regex MetaGuidRegex = new(
        @"(?m)^\s*guid:\s*(?<guid>[A-Za-z0-9]+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MonoBehaviourBlockRegex = new(
        @"(?ms)^--- !u!114 &(?<fileId>-?\d+)\s*(?<body>.*?)(?=^--- !u!|\z)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ScriptGuidRegex = new(
        @"m_Script:\s*\{[^}]*\bguid:\s*(?<guid>[A-Za-z0-9]+)[^}]*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PersistentCallRegex = new(
        @"(?ms)m_Target:\s*\{fileID:\s*(?<target>-?\d+)[^}]*\}\s*\r?\n\s*m_TargetAssemblyTypeName:\s*(?<type>[^\r\n]*)\r?\n\s*m_MethodName:\s*(?<method>[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
