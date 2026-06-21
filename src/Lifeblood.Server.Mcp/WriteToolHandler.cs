using System.Text.Json;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Handles write-side MCP tool calls (require compilation state).
/// Execute, Diagnose, CompileCheck, FindReferences, Rename, Format.
///
/// Symbol-id-bearing handlers (FindReferences, FindDefinition, FindImplementations,
/// Documentation, Rename) route the user's input through <see cref="ISymbolResolver"/>
/// before passing the canonical id to the live workspace tools. See
/// INV-RESOLVER-001 in CLAUDE.md.
/// </summary>
internal sealed class WriteToolHandler
{
    private readonly GraphSession _session;
    private readonly JsonSerializerOptions _jsonOpts;
    private readonly ISymbolResolver _resolver;

    public WriteToolHandler(GraphSession session, JsonSerializerOptions jsonOpts, ISymbolResolver resolver)
    {
        _session = session;
        _jsonOpts = jsonOpts;
        _resolver = resolver;
    }

    public McpToolResult HandleExecute(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var code = GetString(args, "code");
        if (string.IsNullOrEmpty(code))
            return ErrorResult("code is required");

        var timeoutMs = GetInt(args, "timeoutMs") ?? 5000;
        string[]? imports = null;
        if (args?.TryGetProperty("imports", out var importsEl) == true && importsEl.ValueKind == JsonValueKind.Array)
            imports = importsEl.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s != "").ToArray();

        var targetProfile = GetString(args, "targetProfile");
        var request = new Lifeblood.Application.Ports.Left.CodeExecutionRequest
        {
            Code = code,
            Imports = imports,
            TimeoutMs = timeoutMs,
            TargetProfile = string.IsNullOrEmpty(targetProfile) ? "host" : targetProfile,
        };
        var result = _session.CodeExecutor!.Execute(request);
        return TextResult(JsonSerializer.Serialize(result, _jsonOpts));
    }

    public McpToolResult HandleDiagnose(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var moduleName = GetString(args, "moduleName");
        var filePath = GetString(args, "filePath");

        var request = new Lifeblood.Application.Ports.Left.DiagnosticsRequest
        {
            FilePath = string.IsNullOrEmpty(filePath) ? null : filePath,
            ModuleName = string.IsNullOrEmpty(moduleName) ? null : moduleName,
        };
        // Use the report shape so the wire carries definesActive +
        // resolvedModule alongside the diagnostics. Callers no longer
        // have to re-run with a different define set to tell Editor-
        // only noise apart from release-build risk.
        // INV-DIAGNOSTIC-ENVELOPE-DEFINES-001 / LB-INBOX-008.
        var report = _session.CompilationHost!.GetDiagnosticsReport(request);

        // S5b: diagnose has no auto-refresh path (compile_check has it
        // via MaybeRefreshIfStale). If the graph is older than the
        // requested scope's source on disk, the diagnostics returned
        // may not match the file state the user just edited. Surface
        // an explicit `possiblyStale` flag on the response body so
        // callers don't have to parse envelope.filesChangedSinceAnalyze
        // out of band. Scope-aware: file scope checks just that file,
        // module scope walks files parented to that module, project
        // scope walks every tracked File symbol.
        // INV-DIAGNOSE-FRESHNESS-002.
        bool possiblyStale = ComputePossiblyStale(filePath, moduleName);

        // INV-DIAGNOSTIC-ENVELOPE-VERBOSITY-001 / LB-TRACK-20260530-030.
        // Compact mode drops the full definesActive[] list (150+ entries on a
        // Unity profile) while keeping the count + resolvedModule, so repeated
        // focused checks stay readable. Default verbose preserves the byte-stable
        // wire shape. Wire-shaping is a connector concern — lives here, not in
        // the Domain DTO or the Roslyn adapter.
        var (definesActiveCount, definesActive) = ProjectDefines(report.DefinesActive, IsCompactVerbosity(args));

        return TextResult(JsonSerializer.Serialize(new
        {
            scope = !string.IsNullOrEmpty(filePath) ? "file" : (!string.IsNullOrEmpty(moduleName) ? "module" : "project"),
            filePath,
            moduleName,
            resolvedModule = string.IsNullOrEmpty(report.ResolvedModule) ? null : report.ResolvedModule,
            count = report.Diagnostics.Length,
            definesActiveCount,
            definesActive,
            possiblyStale,
            diagnostics = report.Diagnostics,
        }, _jsonOpts));
    }

    /// <summary>
    /// Reads the optional <c>verbosity</c> argument. <c>"compact"</c> (case-
    /// insensitive) drops verbose payload such as the full preprocessor-symbol
    /// list; anything else (including absent) preserves the default verbose wire
    /// shape for back-compat. INV-DIAGNOSTIC-ENVELOPE-VERBOSITY-001.
    /// </summary>
    private static bool IsCompactVerbosity(JsonElement? args)
        => string.Equals(GetString(args, "verbosity"), "compact", System.StringComparison.OrdinalIgnoreCase);

    private static bool IsCompactVerbosity(string? verbosity)
        => string.Equals(verbosity, "compact", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pure wire-shaping for the preprocessor-symbol envelope field. Verbose
    /// keeps the full list; compact returns the count only and a null list so
    /// the 150+-entry Unity profile payload is dropped from repeated focused
    /// checks. The count is ALWAYS surfaced so a compact caller still knows the
    /// scope's define breadth. INV-DIAGNOSTIC-ENVELOPE-VERBOSITY-001 /
    /// LB-TRACK-20260530-030. Behavior is verified end-to-end via dogfood
    /// (Server.Mcp exposes no internals to the test project by design — see
    /// McpDispatcher); the input contract is pinned by ToolSchemaSnapshotTests.
    /// </summary>
    private static (int Count, string[]? List) ProjectDefines(string[] definesActive, bool compact)
        => (definesActive.Length, compact ? null : definesActive);

    /// <summary>
    /// True iff the requested diagnose scope has at least one tracked
    /// source file whose on-disk mtime is newer than the loaded graph's
    /// analyze timestamp. Scope-aware:
    /// <list type="bullet">
    /// <item><c>file</c> scope: mtime-checks the resolved file path only.</item>
    /// <item><c>module</c> scope: walks every <c>File</c> symbol whose
    ///   parent is <c>mod:&lt;moduleName&gt;</c>.</item>
    /// <item><c>project</c> scope: walks every <c>File</c> symbol in the graph.</item>
    /// </list>
    /// Returns false when there's no analyze timestamp (JSON-graph
    /// import), no file system, or no graph. Filesystem stat failures
    /// are swallowed silently — a missing/inaccessible file is not
    /// stale, just gone. INV-DIAGNOSE-FRESHNESS-002.
    /// </summary>
    private bool ComputePossiblyStale(string? filePath, string? moduleName)
    {
        var analyzedAt = _session.AnalyzedAtUtc;
        if (!analyzedAt.HasValue) return false;
        var fs = _session.FileSystem;
        if (fs == null) return false;

        if (!string.IsNullOrEmpty(filePath))
        {
            // File scope: mtime-check just this file. Cheap.
            var resolved = ResolveWorkspacePath(filePath);
            try
            {
                if (!fs.FileExists(resolved)) return false;
                return fs.GetLastWriteTimeUtc(resolved) > analyzedAt.Value;
            }
            catch
            {
                return false;
            }
        }

        // Module or project scope: walk File symbols. Module scope
        // additionally filters by ParentId == "mod:<moduleName>".
        if (_session.Graph == null) return false;
        var root = _session.ProjectRoot;
        var moduleParentId = !string.IsNullOrEmpty(moduleName) ? $"mod:{moduleName}" : null;

        foreach (var sym in _session.Graph.Symbols)
        {
            if (sym.Kind != Lifeblood.Domain.Graph.SymbolKind.File) continue;
            if (string.IsNullOrEmpty(sym.FilePath)) continue;
            if (moduleParentId != null
                && !string.Equals(sym.ParentId, moduleParentId, System.StringComparison.Ordinal))
                continue;

            var path = System.IO.Path.IsPathRooted(sym.FilePath) || string.IsNullOrEmpty(root)
                ? sym.FilePath
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, sym.FilePath));
            try
            {
                if (fs.GetLastWriteTimeUtc(path) > analyzedAt.Value) return true;
            }
            catch
            {
                // File missing / inaccessible — not stale, just gone.
            }
        }
        return false;
    }

    public McpToolResult HandleCompileCheck(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var toolRequest = ToolRequestBinder.BindCompileCheck(args);
        var code = toolRequest.Code;
        var filePath = toolRequest.FilePath;

        // BUG-015: accept either inline `code` or a `filePath`. Exactly one
        // is required; both being set is a caller error because the result
        // would silently depend on which one wins in the handler.
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(filePath))
            return ErrorResult("Either 'code' or 'filePath' is required.");
        if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(filePath))
            return ErrorResult("'code' and 'filePath' are mutually exclusive — supply exactly one.");

        // File-mode hand-off: the host owns owning-compilation detection
        // AND tree-swapping. Reading the file off
        // disk and stuffing it through the snippet path is what produced
        // the "every type unresolved" failure on Unity files — adding the
        // same file as a fresh tree alongside its own existing tree means
        // the compilation has zero references to the file's siblings,
        // so every cross-file type lookup emits CS0246. The host instead
        // ReplaceSyntaxTree's the file's own tree inside the file's own
        // module compilation, preserving every reference.
        string? overrideCode = null;
        if (!string.IsNullOrEmpty(filePath))
        {
            var resolvedPath = ResolveWorkspacePath(filePath);
            if (!_session.FileSystem.FileExists(resolvedPath))
                return ErrorResult($"File not found: {filePath} (resolved to '{resolvedPath}'). Pass an absolute path or one relative to the loaded project root.");
            try
            {
                // Read the on-disk content so the host can swap it in for
                // the existing tree. Stale-refresh below covers other
                // edited files; this read covers THIS file's edits.
                overrideCode = _session.FileSystem.ReadAllText(resolvedPath);
            }
            catch (System.IO.IOException ex)
            {
                return ErrorResult($"Could not read '{filePath}': {ex.Message}");
            }
        }

        var moduleName = toolRequest.ModuleName;

        // Auto-refresh the workspace if source has been edited since the
        // last analyze. Prevents stale-source errors when a user edits a
        // file then runs compile_check. Opt-out via `staleRefresh:false`
        // for callers that explicitly want the pinned-workspace check.
        var staleRefresh = toolRequest.EffectiveStaleRefresh;
        var refreshed = staleRefresh ? _session.MaybeRefreshIfStale() : null;

        var request = new CompileCheckRequest
        {
            Code = !string.IsNullOrEmpty(filePath) ? overrideCode : code,
            FilePath = !string.IsNullOrEmpty(filePath) ? filePath : null,
            ModuleName = moduleName,
        };
        var result = _session.CompilationHost!.CompileCheck(request);

        // INV-COMPILE-CHECK-FILE-RESOLUTION-001 / LB-TRACK-20260530-028.
        // The handler proved the path exists on disk above (FileExists guard),
        // so a NotInAnyCompilation result in file-mode can ONLY mean "on disk
        // but not in any loaded compilation" — the stale-descriptor case. The
        // host stays disk-agnostic; the disk-aware hint is composed here where
        // the IFileSystem port lives.
        var staleDescriptorHint =
            !string.IsNullOrEmpty(filePath)
            && result.FileResolution == CompileCheckFileResolution.NotInAnyCompilation
            ? "File exists on disk but is not in any loaded compilation. Project descriptors are " +
              "likely stale (e.g. a freshly-added Unity file before import). Regenerate project " +
              "files / refresh the editor, then re-run lifeblood_analyze."
            : null;

        var (definesActiveCount, definesActiveList) = ProjectDefines(result.DefinesActive, IsCompactVerbosity(toolRequest.Verbosity));

        var commonShape = new
        {
            result.Success,
            result.Diagnostics,
            source = !string.IsNullOrEmpty(filePath) ? "filePath" : "code",
            filePath,
            resolvedModule = string.IsNullOrEmpty(result.ResolvedModule) ? null : result.ResolvedModule,
            existingTreeReplaced = result.ExistingTreeReplaced,
            // INV-DIAGNOSTIC-ENVELOPE-DEFINES-001 / LB-INBOX-008.
            // INV-DIAGNOSTIC-ENVELOPE-VERBOSITY-001 / LB-TRACK-20260530-030:
            // compact mode keeps the count but drops the full list.
            definesActiveCount,
            definesActive = definesActiveList,
            // INV-COMPILE-CHECK-FILE-RESOLUTION-001 / LB-TRACK-20260530-028.
            fileResolution = result.FileResolution.ToString(),
            staleDescriptorHint,
        };

        if (refreshed is int changedFileCount)
        {
            return TextResult(JsonSerializer.Serialize(new
            {
                commonShape.Success,
                commonShape.Diagnostics,
                commonShape.source,
                commonShape.filePath,
                commonShape.resolvedModule,
                commonShape.existingTreeReplaced,
                commonShape.definesActiveCount,
                commonShape.definesActive,
                commonShape.fileResolution,
                commonShape.staleDescriptorHint,
                autoRefreshed = true,
                changedFileCount,
            }, _jsonOpts));
        }
        return TextResult(JsonSerializer.Serialize(commonShape, _jsonOpts));
    }

    /// <summary>
    /// Resolve a user-supplied file path against the loaded workspace.
    /// Absolute paths pass through unchanged. Relative paths are joined to
    /// the session's project root. Empty project root (e.g. JSON-graph
    /// mode) leaves the path unchanged so the caller's relative form is
    /// preserved for the FileSystem call.
    /// </summary>
    private string ResolveWorkspacePath(string path)
    {
        if (System.IO.Path.IsPathRooted(path)) return path;
        var root = _session.ProjectRoot;
        if (string.IsNullOrEmpty(root)) return path;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
    }

    public McpToolResult HandleFindReferences(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;
        if (CheckProfileScope(args) is { } scopeError) return scopeError;

        var raw = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        // Plan v4 Seam #1: resolver canonicalizes the input first.
        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        // Plan v4 §2.6 / Correction 2: declaration inclusion is operation
        // policy, modeled as an explicit FindReferencesOptions on the host.
        var includeDecls = GetBool(args, "includeDeclarations") ?? false;
        var options = new Lifeblood.Application.Ports.Left.FindReferencesOptions
        {
            IncludeDeclarations = includeDecls,
        };

        var locations = _session.CompilationHost!.FindReferences(resolved.CanonicalId, options);
        return TextResult(JsonSerializer.Serialize(new
        {
            symbolId = resolved.CanonicalId,
            count = locations.Length,
            locations,
            analyzedUnderProfile = _session.RetainedProfileName,
            limitations = WriteSideRetainedProfileLimitations(),
        }, _jsonOpts));
    }

    public McpToolResult HandleRename(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;
        if (CheckProfileScope(args) is { } scopeError) return scopeError;

        var raw = GetString(args, "symbolId");
        var newName = GetString(args, "newName");
        if (string.IsNullOrEmpty(raw)) return ErrorResult("symbolId is required");
        if (string.IsNullOrEmpty(newName)) return ErrorResult("newName is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var edits = _session.Refactoring!.Rename(resolved.CanonicalId, newName);
        return TextResult(JsonSerializer.Serialize(new
        {
            symbolId = resolved.CanonicalId,
            newName,
            editCount = edits.Length,
            edits,
            analyzedUnderProfile = _session.RetainedProfileName,
            limitations = WriteSideRetainedProfileLimitations(),
        }, _jsonOpts));
    }

    public McpToolResult HandleFormat(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var code = GetString(args, "code");
        if (string.IsNullOrEmpty(code))
            return ErrorResult("code is required");

        var formatted = _session.Refactoring!.Format(code);
        return TextResult(formatted);
    }

    public McpToolResult HandleFindDefinition(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;
        if (CheckProfileScope(args) is { } scopeError) return scopeError;

        var raw = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var def = _session.CompilationHost!.FindDefinition(resolved.CanonicalId);
        if (def == null)
            return ErrorResult($"Definition not found: {resolved.CanonicalId}");

        // Wrap so analyzedUnderProfile + limitations surface alongside the
        // definition payload. INV-MULTI-DEFINE-WRITESIDE-001.
        return TextResult(JsonSerializer.Serialize(new
        {
            definition = def,
            analyzedUnderProfile = _session.RetainedProfileName,
            limitations = WriteSideRetainedProfileLimitations(),
        }, _jsonOpts));
    }

    public McpToolResult HandleFindImplementations(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;
        if (CheckProfileScope(args) is { } scopeError) return scopeError;

        var raw = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var impls = _session.CompilationHost!.FindImplementations(resolved.CanonicalId);
        return TextResult(JsonSerializer.Serialize(new
        {
            symbolId = resolved.CanonicalId,
            count = impls.Length,
            implementations = impls,
            analyzedUnderProfile = _session.RetainedProfileName,
            limitations = WriteSideRetainedProfileLimitations(),
        }, _jsonOpts));
    }

    /// <summary>
    /// INV-MULTI-DEFINE-IOP-001 + INV-MULTI-DEFINE-WRITESIDE-001. Every
    /// Roslyn-write-side tool that walks the retained compilation host
    /// (find_references, find_definition, find_implementations, rename,
    /// enum_coverage, static_tables, assignment_coverage) operates against
    /// the retained profile's compilations only. profileScope must match the
    /// retained profile name when set; mismatched values fail loudly so the
    /// caller never silently consumes the wrong profile's view.
    /// </summary>
    private McpToolResult? CheckProfileScope(JsonElement? args)
    {
        var requested = GetString(args, "profileScope");
        if (string.IsNullOrEmpty(requested)) return null;
        var retained = _session.RetainedProfileName;
        if (string.IsNullOrEmpty(retained))
            return ErrorResult($"profileScope='{requested}' but no profile is retained. Call lifeblood_analyze with `defineProfiles` first.");
        if (!string.Equals(requested, retained, StringComparison.Ordinal))
            return ErrorResult($"profileScope='{requested}' is not the retained profile ('{retained}'). Roslyn write-side tools currently support only the first / retained profile per INV-MULTI-DEFINE-WRITESIDE-001 (memory: cross-profile retention would multiply peak RAM per profile retained). Re-analyze with the requested profile FIRST in `defineProfiles` to switch.");
        return null;
    }

    /// <summary>
    /// INV-MULTI-DEFINE-WRITESIDE-001. On a multi-profile snapshot, every
    /// write-side Roslyn tool response advertises that its results reflect
    /// the retained (first) profile only — call sites guarded by
    /// preprocessor symbols active under OTHER profiles are invisible to
    /// the current compilation host. Graph-side tools (`dependants`,
    /// `dependencies`) honor `profileFilter` over the union graph; write-
    /// side tools require an explicit re-analyze with the desired profile
    /// first in `defineProfiles` to see those sites. Returned as
    /// <c>limitations[]</c> on the tool payload (separate from the
    /// truth-envelope's own limitations) so the caller's wire-shape parse
    /// sees the constraint inline with the data it qualifies.
    /// </summary>
    private string[] WriteSideRetainedProfileLimitations()
    {
        var profiles = _session.RetainedProfileNames;
        if (profiles.Count <= 1) return System.Array.Empty<string>();
        var retained = _session.RetainedProfileName ?? profiles[0];
        var others = string.Join(", ", profiles.Where(p => !string.Equals(p, retained, StringComparison.Ordinal)));
        return new[]
        {
            $"Graph is multi-profile ({string.Join(", ", profiles)}); this tool answered against retained profile '{retained}' only. Call sites guarded by preprocessor symbols active under [{others}] are NOT in this response. Use `lifeblood_dependants` / `lifeblood_dependencies` with `profileFilter` for union-graph reference queries, or re-analyze with the target profile first in `defineProfiles` to switch the retained profile.",
        };
    }

    public McpToolResult HandleEnumCoverage(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;
        if (CheckProfileScope(args) is { } scopeError) return scopeError;

        var raw = GetString(args, "enumTypeId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("enumTypeId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var report = _session.CompilationHost!.GetEnumCoverage(resolved.CanonicalId);
        if (report == null)
            return ErrorResult($"Not an enum type: {resolved.CanonicalId}");

        return TextResult(JsonSerializer.Serialize(new
        {
            enumTypeId = report.EnumTypeId,
            enumTypeName = report.EnumTypeName,
            memberCount = report.Members.Length,
            unproducedCount = report.UnproducedCount,
            unreferencedCount = report.UnreferencedCount,
            members = report.Members,
            analyzedUnderProfile = _session.RetainedProfileName,
        }, _jsonOpts));
    }

    public McpToolResult HandleStaticTables(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;
        if (CheckProfileScope(args) is { } scopeError) return scopeError;

        var raw = GetString(args, "typeId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("typeId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var options = new StaticTablesOptions
        {
            MemberName = GetString(args, "memberName"),
            MaxRows = GetInt(args, "maxRows"),
            MaxTables = GetInt(args, "maxTables"),
            Summarize = GetBool(args, "summarize"),
        };

        var report = _session.CompilationHost!.GetStaticTables(resolved.CanonicalId, options);
        if (report == null)
            return ErrorResult($"Type not found in source: {resolved.CanonicalId}");

        return TextResult(JsonSerializer.Serialize(new
        {
            report.TypeId,
            report.Tables,
            report.TablesTruncated,
            analyzedUnderProfile = _session.RetainedProfileName,
        }, _jsonOpts));
    }

    public McpToolResult HandleAssignmentCoverage(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;
        if (CheckProfileScope(args) is { } scopeError) return scopeError;

        var raw = GetString(args, "targetTypeId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("targetTypeId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var options = new AssignmentCoverageOptions
        {
            IncludeDelegateFields = GetBool(args, "includeDelegateFields") ?? true,
            IncludeDelegateProperties = GetBool(args, "includeDelegateProperties") ?? true,
            IncludePublicMutableFields = GetBool(args, "includePublicMutableFields") ?? false,
            IncludePublicMutableProperties = GetBool(args, "includePublicMutableProperties") ?? false,
            SlotName = GetString(args, "slotName"),
            MaxSites = GetInt(args, "maxSites"),
        };

        var report = _session.CompilationHost!.GetAssignmentCoverage(resolved.CanonicalId, options);
        if (report == null)
            return ErrorResult($"Type not found in source: {resolved.CanonicalId}");

        return TextResult(JsonSerializer.Serialize(new
        {
            report.TargetTypeId,
            report.AllSlots,
            report.Sites,
            analyzedUnderProfile = _session.RetainedProfileName,
        }, _jsonOpts));
    }

    public McpToolResult HandleCallsiteArguments(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;
        if (CheckProfileScope(args) is { } scopeError) return scopeError;

        var raw = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var options = new CallsiteArgumentsOptions
        {
            ModuleScope = GetString(args, "moduleScope"),
            MaxSites = GetInt(args, "maxSites"),
            ExcludeTests = GetBool(args, "excludeTests") ?? false,
        };

        var report = _session.CompilationHost!.GetCallsiteArguments(resolved.CanonicalId, options);
        if (report == null)
            return ErrorResult($"Not a method or constructor in source: {resolved.CanonicalId}");

        return TextResult(JsonSerializer.Serialize(new
        {
            report.TargetId,
            report.TargetDisplay,
            report.CallSiteCount,
            report.SitesTruncated,
            report.ParameterSummaries,
            report.Sites,
            analyzedUnderProfile = _session.RetainedProfileName,
        }, _jsonOpts));
    }

    public McpToolResult HandleWireAudit(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;
        if (CheckProfileScope(args) is { } scopeError) return scopeError;

        // typeId is an optional output filter — resolve to canonical when set so
        // a short/qualified name matches the extractor's canonical declaringType.
        string? typeId = null;
        var rawType = GetString(args, "typeId");
        if (!string.IsNullOrEmpty(rawType))
        {
            var resolved = _resolver.Resolve(_session.Graph!, rawType);
            if (resolved.CanonicalId == null)
                return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {rawType}");
            typeId = resolved.CanonicalId;
        }

        var options = new WireAuditOptions
        {
            TypeId = typeId,
            ModuleScope = GetString(args, "moduleScope"),
            IncludeFieldReadWithoutWrite = GetBool(args, "includeFieldReadWithoutWrite") ?? true,
            IncludeDelegateSlots = GetBool(args, "includeDelegateSlots") ?? true,
            MaxFindings = GetInt(args, "maxFindings"),
        };

        var report = _session.CompilationHost!.GetWireAudit(options);

        return TextResult(JsonSerializer.Serialize(new
        {
            report.Scope,
            report.FindingCount,
            report.Truncated,
            report.KindBreakdown,
            report.Findings,
            warning = "Findings are ADVISORY. A 'never assigned' / 'read without write' member can still be wired through reflection, Unity serialized (prefab/scene/asset YAML) UnityEvent or [SerializeField] injection, or runtime-procedural assignment — none visible to static analysis. Verify against those sources before deleting or re-wiring.",
            analyzedUnderProfile = _session.RetainedProfileName,
        }, _jsonOpts));
    }

    public McpToolResult HandleFeatureSwitchAudit(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;
        if (CheckProfileScope(args) is { } scopeError) return scopeError;

        // typeId is an optional output filter — resolve to canonical when set so
        // a short/qualified name matches the extractor's canonical declaringType.
        string? typeId = null;
        var rawType = GetString(args, "typeId");
        if (!string.IsNullOrEmpty(rawType))
        {
            var resolved = _resolver.Resolve(_session.Graph!, rawType);
            if (resolved.CanonicalId == null)
                return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {rawType}");
            typeId = resolved.CanonicalId;
        }

        var options = new FeatureSwitchAuditOptions
        {
            TypeId = typeId,
            ModuleScope = GetString(args, "moduleScope"),
            RequireBranchCondition = GetBool(args, "requireBranchCondition") ?? true,
            IncludeProperties = GetBool(args, "includeProperties") ?? true,
            MaxFindings = GetInt(args, "maxFindings"),
        };

        var report = _session.CompilationHost!.GetFeatureSwitchAudit(options);

        return TextResult(JsonSerializer.Serialize(new
        {
            report.Scope,
            report.SwitchCount,
            report.Truncated,
            report.VerdictBreakdown,
            report.Switches,
            warning = "Verdicts reflect ONLY in-graph activation. An 'AlwaysDefaultInGraph' / 'TestOnlyActivation' switch can still be flipped through reflection, Unity serialized (prefab/scene/asset YAML / UnityEvent / [SerializeField]), config or save-state deserialization, or a public mutator called from outside the analyzed compilation set — none visible to static analysis. Reachability uses DIRECT call sites only (plus ctors/initializers), not transitive or entry-point dispatch. Verify before declaring a feature dead.",
            analyzedUnderProfile = _session.RetainedProfileName,
        }, _jsonOpts));
    }

    public McpToolResult HandleGetSymbolAtPosition(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var filePath = GetString(args, "filePath");
        var line = GetInt(args, "line");
        var column = GetInt(args, "column");
        if (string.IsNullOrEmpty(filePath) || line == null || column == null)
            return ErrorResult("filePath, line, and column are required");

        var symbol = _session.CompilationHost!.GetSymbolAtPosition(filePath, line.Value, column.Value);
        if (symbol == null)
            return ErrorResult($"No symbol at {filePath}:{line}:{column}");

        return TextResult(JsonSerializer.Serialize(symbol, _jsonOpts));
    }

    public McpToolResult HandleGetDocumentation(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var raw = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var doc = _session.CompilationHost!.GetDocumentation(resolved.CanonicalId);
        return TextResult(string.IsNullOrEmpty(doc)
            ? $"No documentation found for {resolved.CanonicalId}"
            : doc);
    }

    private McpToolResult? CompilationStateError()
    {
        if (_session.HasCompilationState) return null;
        return ErrorResult("Write-side tools require loading via projectPath (Roslyn adapter). Call lifeblood_analyze with projectPath first.");
    }

    internal static string? GetString(JsonElement? args, string key)
    {
        if (args == null) return null;
        if (args.Value.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    internal static int? GetInt(JsonElement? args, string key)
    {
        if (args == null) return null;
        if (args.Value.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return null;
    }

    internal static bool? GetBool(JsonElement? args, string key)
    {
        if (args == null) return null;
        if (args.Value.TryGetProperty(key, out var val) &&
            (val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False))
            return val.GetBoolean();
        return null;
    }

    private static McpToolResult TextResult(string text) => new()
    {
        Content = new[] { new McpContent { Type = "text", Text = text } },
    };

    private static McpToolResult ErrorResult(string message) => new()
    {
        Content = new[] { new McpContent { Type = "text", Text = message } },
        IsError = true,
    };
}
