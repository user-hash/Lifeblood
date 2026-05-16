#include "ClangUtilities.h"
#include "LibClangExtractor.h"

#include <clang-c/CXCompilationDatabase.h>
#include <clang-c/Index.h>

#include <filesystem>
#include <iostream>
#include <map>
#include <optional>
#include <set>
#include <sstream>
#include <utility>
#include <vector>

namespace fs = std::filesystem;

namespace lifeblood::native_clang
{
namespace
{
struct VisitState
{
    std::string currentFunctionId;
    std::string currentTypeId;
    std::string currentInitializerOwnerId;
};

std::string BaseName(const fs::path& path)
{
    auto name = path.filename().string();
    return name.empty() ? "native-project" : name;
}

class ExtractionSession
{
public:
    ExtractionSession(
        Options options,
        NativeGraph& graph,
        std::set<std::tuple<std::string, std::string, std::string>>& edgeKeys)
        : options_(std::move(options)),
          projectRoot_(fs::weakly_canonical(options_.projectRoot)),
          compilationDatabaseDir_(ResolvePath(options_.compilationDatabaseDir)),
          moduleName_(BaseName(projectRoot_)),
          moduleId_("mod:" + moduleName_),
          graph_(graph),
          edgeKeys_(edgeKeys)
    {
        Symbol module;
        module.id = moduleId_;
        module.name = moduleName_;
        module.qualifiedName = moduleName_;
        module.kind = "module";
        module.properties["native.kind"] = "library";
        module.properties["native.buildProfile"] = options_.profile;
        AddSymbol(module);
    }

    bool Run()
    {
        CXCompilationDatabase_Error error = CXCompilationDatabase_NoError;
        CXCompilationDatabase database = clang_CompilationDatabase_fromDirectory(
            compilationDatabaseDir_.string().c_str(),
            &error);
        if (error != CXCompilationDatabase_NoError || database == nullptr)
        {
            std::cerr << "Failed to read compile_commands.json from "
                      << compilationDatabaseDir_.string() << "\n";
            return false;
        }

        CXCompileCommands commands = clang_CompilationDatabase_getAllCompileCommands(database);
        const unsigned commandCount = clang_CompileCommands_getSize(commands);
        if (commandCount == 0)
        {
            std::cerr << "Compilation database contains no commands\n";
            clang_CompileCommands_dispose(commands);
            clang_CompilationDatabase_dispose(database);
            return false;
        }

        CXIndex index = clang_createIndex(/*excludeDeclarationsFromPCH*/ 0, /*displayDiagnostics*/ 0);
        bool ok = true;
        for (unsigned i = 0; i < commandCount; i++)
        {
            CXCompileCommand command = clang_CompileCommands_getCommand(commands, i);
            ok = ParseCommand(index, command) && ok;
        }

        clang_disposeIndex(index);
        clang_CompileCommands_dispose(commands);
        clang_CompilationDatabase_dispose(database);
        return ok;
    }

private:
    fs::path ResolvePath(const fs::path& path)
    {
        fs::path resolved = path;
        if (!resolved.is_absolute())
            resolved = fs::absolute(resolved);

        std::error_code ec;
        auto canonical = fs::weakly_canonical(resolved, ec);
        return ec ? resolved.lexically_normal() : canonical;
    }

    bool ParseCommand(CXIndex index, CXCompileCommand command)
    {
        translationUnitCount_++;
        CollectCommandLineMacros(command);
        UpdateModuleBuildProperties();

        auto directory = fs::path(ToString(clang_CompileCommand_getDirectory(command)));
        if (!directory.is_absolute())
            directory = compilationDatabaseDir_ / directory;
        directory = fs::weakly_canonical(directory);

        auto file = fs::path(ToString(clang_CompileCommand_getFilename(command)));
        fs::path sourcePath = file.is_absolute() ? file : directory / file;
        sourcePath = fs::weakly_canonical(sourcePath);

        std::vector<std::string> args = BuildParseArgs(command, sourcePath, directory);
        std::vector<const char*> cArgs;
        cArgs.reserve(args.size());
        for (const auto& arg : args)
            cArgs.push_back(arg.c_str());

        CXTranslationUnit unit = nullptr;
        const unsigned parseOptions = CXTranslationUnit_DetailedPreprocessingRecord;
        CXErrorCode parseResult = clang_parseTranslationUnit2(
            index,
            sourcePath.string().c_str(),
            cArgs.data(),
            static_cast<int>(cArgs.size()),
            nullptr,
            0,
            parseOptions,
            &unit);

        if (parseResult != CXError_Success || unit == nullptr)
        {
            std::cerr << "Failed to parse " << sourcePath.string()
                      << " (CXErrorCode " << parseResult << ")\n";
            return false;
        }

        const unsigned diagnosticCount = clang_getNumDiagnostics(unit);
        for (unsigned i = 0; i < diagnosticCount; i++)
        {
            CXDiagnostic diagnostic = clang_getDiagnostic(unit, i);
            auto severity = clang_getDiagnosticSeverity(diagnostic);
            if (severity >= CXDiagnostic_Error)
            {
                std::cerr << ToString(clang_formatDiagnostic(
                    diagnostic,
                    clang_defaultDiagnosticDisplayOptions())) << "\n";
            }
            clang_disposeDiagnostic(diagnostic);
        }

        CXCursor root = clang_getTranslationUnitCursor(unit);
        currentUnit_ = unit;
        Visit(root, VisitState{});
        currentUnit_ = nullptr;

        clang_disposeTranslationUnit(unit);
        return true;
    }

    std::vector<std::string> BuildParseArgs(
        CXCompileCommand command,
        const fs::path& sourcePath,
        const fs::path& commandDirectory)
    {
        std::vector<std::string> args;
        const unsigned count = clang_CompileCommand_getNumArgs(command);
        for (unsigned i = 1; i < count; i++)
        {
            std::string arg = ToString(clang_CompileCommand_getArg(command, i));
            if (arg == "-c") continue;
            if (arg == "-o")
            {
                i++;
                continue;
            }

            if (arg == "-I" || arg == "-iquote" || arg == "-isystem")
            {
                args.push_back(arg);
                if (i + 1 < count)
                    args.push_back(NormalizeCommandPathArg(
                        ToString(clang_CompileCommand_getArg(command, ++i)),
                        commandDirectory));
                continue;
            }

            if (arg.rfind("-I", 0) == 0 && arg.size() > 2)
            {
                args.push_back("-I" + NormalizeCommandPathArg(arg.substr(2), commandDirectory));
                continue;
            }

            if (IsSourceArg(arg, sourcePath, commandDirectory))
                continue;

            args.push_back(arg);
        }
        return args;
    }

    void CollectCommandLineMacros(CXCompileCommand command)
    {
        const unsigned count = clang_CompileCommand_getNumArgs(command);
        for (unsigned i = 1; i < count; i++)
        {
            std::string arg = ToString(clang_CompileCommand_getArg(command, i));
            if (arg == "-D")
            {
                if (i + 1 < count)
                    AddCommandLineDefine(ToString(clang_CompileCommand_getArg(command, ++i)));
                continue;
            }

            if (arg.rfind("-D", 0) == 0 && arg.size() > 2)
            {
                AddCommandLineDefine(arg.substr(2));
                continue;
            }

            if (arg == "-U")
            {
                if (i + 1 < count)
                    AddCommandLineUndefine(ToString(clang_CompileCommand_getArg(command, ++i)));
                continue;
            }

            if (arg.rfind("-U", 0) == 0 && arg.size() > 2)
                AddCommandLineUndefine(arg.substr(2));
        }
    }

    void AddCommandLineDefine(const std::string& raw)
    {
        if (raw.empty()) return;

        auto equal = raw.find('=');
        std::string name = equal == std::string::npos ? raw : raw.substr(0, equal);
        std::string value = equal == std::string::npos ? "1" : raw.substr(equal + 1);
        if (name.empty()) return;

        commandLineDefines_[name] = value;
        AddMacroSymbol(name, std::nullopt, 0, "commandLine", value);
    }

    void AddCommandLineUndefine(const std::string& name)
    {
        if (!name.empty())
            commandLineUndefines_.insert(name);
    }

    void UpdateModuleBuildProperties()
    {
        auto it = graph_.symbols.find(moduleId_);
        if (it == graph_.symbols.end()) return;

        it->second.properties["native.translationUnitCount"] = std::to_string(translationUnitCount_);
        if (!commandLineDefines_.empty())
            it->second.properties["native.defines"] = JoinDefines();
        if (!commandLineUndefines_.empty())
            it->second.properties["native.undefines"] = Join(commandLineUndefines_);
    }

    bool IsSourceArg(
        const std::string& arg,
        const fs::path& sourcePath,
        const fs::path& commandDirectory)
    {
        fs::path maybePath(arg);
        if (maybePath.extension() != sourcePath.extension())
            return false;

        if (!maybePath.is_absolute())
            maybePath = commandDirectory / maybePath;

        std::error_code ec;
        auto canonical = fs::weakly_canonical(maybePath, ec);
        return !ec && canonical == sourcePath;
    }

    std::string NormalizeCommandPathArg(
        const std::string& arg,
        const fs::path& commandDirectory)
    {
        fs::path path(arg);
        if (path.is_absolute())
            return SlashPath(path.string());

        return SlashPath((commandDirectory / path).lexically_normal().string());
    }

    struct ChildVisitPayload
    {
        ExtractionSession* extractor;
        VisitState state;
    };

    static CXChildVisitResult VisitChild(CXCursor cursor, CXCursor, CXClientData data)
    {
        auto* payload = static_cast<ChildVisitPayload*>(data);
        payload->extractor->Visit(cursor, payload->state);
        return CXChildVisit_Continue;
    }

    void Visit(CXCursor cursor, VisitState state)
    {
        VisitState childState = ProcessCursor(cursor, state);
        ChildVisitPayload payload{ this, childState };
        clang_visitChildren(cursor, &ExtractionSession::VisitChild, &payload);
    }

    VisitState ProcessCursor(CXCursor cursor, VisitState state)
    {
        switch (clang_getCursorKind(cursor))
        {
            case CXCursor_InclusionDirective:
                ProcessInclude(cursor);
                break;
            case CXCursor_MacroDefinition:
                ProcessMacroDefinition(cursor);
                break;
            case CXCursor_MacroExpansion:
                ProcessMacroExpansion(cursor);
                break;
            case CXCursor_StructDecl:
                if (AddRecordType(cursor, "struct"))
                    state.currentTypeId = TypeId(cursor);
                break;
            case CXCursor_UnionDecl:
                if (AddRecordType(cursor, "union"))
                    state.currentTypeId = TypeId(cursor);
                break;
            case CXCursor_EnumDecl:
                if (AddRecordType(cursor, "enum"))
                    state.currentTypeId = TypeId(cursor);
                break;
            case CXCursor_EnumConstantDecl:
                ProcessEnumConstant(cursor, state);
                break;
            case CXCursor_TypedefDecl:
                AddTypedefType(cursor);
                break;
            case CXCursor_FieldDecl:
                ProcessField(cursor, state);
                break;
            case CXCursor_VarDecl:
            {
                auto initializerOwnerId = ProcessVariable(cursor, state);
                if (!initializerOwnerId.empty())
                    state.currentInitializerOwnerId = initializerOwnerId;
                break;
            }
            case CXCursor_FunctionDecl:
                if (AddFunction(cursor))
                    state.currentFunctionId = FunctionId(cursor);
                break;
            case CXCursor_CallExpr:
                ProcessCall(cursor, state);
                break;
            case CXCursor_DeclRefExpr:
                ProcessDeclarationReference(cursor, state);
                break;
            case CXCursor_MemberRefExpr:
                ProcessMemberRef(cursor, state);
                break;
            default:
                break;
        }
        return state;
    }

    void ProcessInclude(CXCursor cursor)
    {
        auto sourceFile = SourceFile(cursor);
        if (!sourceFile) return;

        CXFile included = clang_getIncludedFile(cursor);
        if (included == nullptr) return;
        auto includedPath = RelPath(included);
        if (!includedPath) return;

        EnsureFileSymbol(*sourceFile);
        EnsureFileSymbol(*includedPath);

        Edge edge;
        edge.sourceId = "file:" + *sourceFile;
        edge.targetId = "file:" + *includedPath;
        edge.kind = "references";
        edge.evidence = EvidenceFor(cursor, "syntax");
        edge.callSite = CallSiteFor(cursor, edge.sourceId);
        edge.properties["native.kind"] = "include";
        edge.properties["native.include"] = fs::path(*includedPath).filename().string();
        edge.properties["native.buildProfile"] = options_.profile;
        AddEdge(edge);
    }

    void ProcessMacroDefinition(CXCursor cursor)
    {
        auto file = SourceFile(cursor);
        if (!file) return;

        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return;

        AddMacroSymbol(name, file, Line(cursor), "source", MacroReplacement(cursor));
    }

    void ProcessMacroExpansion(CXCursor cursor)
    {
        auto file = SourceFile(cursor);
        if (!file) return;

        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return;

        std::string targetId = MacroId(name);
        if (graph_.symbols.find(targetId) == graph_.symbols.end())
            AddMacroSymbol(name, std::nullopt, 0, "unknown", "");

        EnsureFileSymbol(*file);

        Edge edge;
        edge.sourceId = "file:" + *file;
        edge.targetId = targetId;
        edge.kind = "references";
        edge.evidence = EvidenceFor(cursor, "syntax");
        edge.callSite = CallSiteFor(cursor, edge.sourceId);
        edge.properties["native.referenceKind"] = "macroExpansion";
        edge.properties["native.buildProfile"] = options_.profile;
        AddEdge(edge);
    }

    void AddMacroSymbol(
        const std::string& name,
        std::optional<std::string> file,
        unsigned line,
        const std::string& source,
        const std::string& value)
    {
        Symbol symbol;
        symbol.id = MacroId(name);
        symbol.name = name;
        symbol.qualifiedName = name;
        symbol.kind = "field";
        if (file)
        {
            EnsureFileSymbol(*file);
            symbol.filePath = *file;
            symbol.line = line;
            symbol.parentId = "file:" + *file;
        }
        else
        {
            symbol.parentId = moduleId_;
        }
        symbol.visibility = "internal";
        symbol.isStatic = true;
        symbol.properties["native.kind"] = "macro";
        symbol.properties["native.macroSource"] = source;
        symbol.properties["native.macroValue"] = value;
        symbol.properties["native.buildProfile"] = options_.profile;
        AddSymbol(symbol);
    }

    std::string MacroReplacement(CXCursor cursor)
    {
        if (currentUnit_ == nullptr) return "";

        CXToken* tokens = nullptr;
        unsigned tokenCount = 0;
        clang_tokenize(currentUnit_, clang_getCursorExtent(cursor), &tokens, &tokenCount);
        if (tokens == nullptr || tokenCount <= 1)
        {
            if (tokens != nullptr)
                clang_disposeTokens(currentUnit_, tokens, tokenCount);
            return "";
        }

        std::ostringstream value;
        for (unsigned i = 1; i < tokenCount; i++)
        {
            if (i > 1) value << ' ';
            value << ToString(clang_getTokenSpelling(currentUnit_, tokens[i]));
        }

        clang_disposeTokens(currentUnit_, tokens, tokenCount);
        return value.str();
    }

    bool AddRecordType(CXCursor cursor, const std::string& nativeKind)
    {
        if (!clang_isCursorDefinition(cursor)) return false;
        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return false;
        auto file = SourceFile(cursor);
        if (!file) return false;

        EnsureFileSymbol(*file);

        Symbol symbol;
        symbol.id = TypeId(cursor);
        symbol.name = name;
        symbol.qualifiedName = name;
        symbol.kind = "type";
        symbol.filePath = *file;
        symbol.line = Line(cursor);
        symbol.parentId = "file:" + *file;
        symbol.visibility = "public";
        symbol.properties["native.kind"] = nativeKind;
        symbol.properties["native.linkage"] = "none";
        symbol.properties["native.buildProfile"] = options_.profile;
        AddSymbol(symbol);
        return true;
    }

    bool AddTypedefType(CXCursor cursor)
    {
        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return false;
        auto file = SourceFile(cursor);
        if (!file) return false;

        EnsureFileSymbol(*file);

        Symbol symbol;
        symbol.id = TypeId(cursor);
        symbol.name = name;
        symbol.qualifiedName = name;
        symbol.kind = "type";
        symbol.filePath = *file;
        symbol.line = Line(cursor);
        symbol.parentId = "file:" + *file;
        symbol.visibility = "public";
        symbol.properties["native.kind"] = "typedef";
        symbol.properties["native.underlyingType"] = NormalizeTypeForId(
            ToString(clang_getTypeSpelling(clang_getTypedefDeclUnderlyingType(cursor))));
        symbol.properties["native.buildProfile"] = options_.profile;
        AddSymbol(symbol);

        AddTypeReference(symbol.id, cursor, clang_getTypedefDeclUnderlyingType(cursor), "underlyingType");
        return true;
    }

    void ProcessEnumConstant(CXCursor cursor, const VisitState& state)
    {
        if (state.currentTypeId.empty()) return;

        CXCursor parent = clang_getCursorSemanticParent(cursor);
        if (clang_Cursor_isNull(parent) || clang_getCursorKind(parent) != CXCursor_EnumDecl)
            return;
        if (!AddRecordType(parent, "enum")) return;

        AddEnumConstant(cursor, TypeId(parent));
    }

    bool AddEnumConstant(CXCursor cursor, const std::string& enumTypeId)
    {
        auto file = SourceFile(cursor);
        if (!file) return false;
        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return false;

        const std::string prefix = "type:";
        std::string enumName = enumTypeId.rfind(prefix, 0) == 0
            ? enumTypeId.substr(prefix.size())
            : enumTypeId;

        Symbol symbol;
        symbol.id = "field:" + enumName + "." + name;
        symbol.name = name;
        symbol.qualifiedName = enumName + "." + name;
        symbol.kind = "field";
        symbol.filePath = *file;
        symbol.line = Line(cursor);
        symbol.parentId = enumTypeId;
        symbol.visibility = "public";
        symbol.isStatic = true;
        symbol.properties["native.kind"] = "enumMember";
        symbol.properties["native.enumValue"] = std::to_string(clang_getEnumConstantDeclValue(cursor));
        symbol.properties["native.buildProfile"] = options_.profile;
        AddSymbol(symbol);
        return true;
    }

    void ProcessField(CXCursor cursor, const VisitState& state)
    {
        if (state.currentTypeId.empty()) return;
        auto file = SourceFile(cursor);
        if (!file) return;
        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return;

        const std::string prefix = "type:";
        std::string owner = state.currentTypeId.rfind(prefix, 0) == 0
            ? state.currentTypeId.substr(prefix.size())
            : state.currentTypeId;

        Symbol field;
        field.id = "field:" + owner + "." + name;
        field.name = name;
        field.qualifiedName = owner + "." + name;
        field.kind = "field";
        field.filePath = *file;
        field.line = Line(cursor);
        field.parentId = state.currentTypeId;
        field.visibility = "public";
        field.properties["native.kind"] = "structField";
        field.properties["native.fieldType"] = NormalizeTypeForId(
            ToString(clang_getTypeSpelling(clang_getCursorType(cursor))));
        field.properties["native.buildProfile"] = options_.profile;
        AddSymbol(field);

        AddTypeReference(field.id, cursor, clang_getCursorType(cursor), "fieldType");
    }

    std::string ProcessVariable(CXCursor cursor, const VisitState& state)
    {
        if (!state.currentFunctionId.empty() || !state.currentTypeId.empty())
            return "";

        if (!AddGlobalVariable(cursor))
            return "";

        return GlobalVariableId(cursor);
    }

    bool AddGlobalVariable(CXCursor cursor)
    {
        if (!IsFileScopeCursor(cursor)) return false;

        auto file = SourceFile(cursor);
        if (!file) return false;
        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return false;

        EnsureFileSymbol(*file);

        const auto storage = clang_Cursor_getStorageClass(cursor);
        const std::string symbolId = GlobalVariableId(cursor);
        auto existing = graph_.symbols.find(symbolId);

        Symbol symbol;
        symbol.id = symbolId;
        symbol.name = name;
        symbol.qualifiedName = name;
        symbol.kind = "field";
        symbol.filePath = *file;
        symbol.line = Line(cursor);
        symbol.parentId = "file:" + *file;
        symbol.visibility = storage == CX_SC_Static ? "private" : "public";
        symbol.isStatic = storage == CX_SC_Static;
        symbol.properties["native.kind"] =
            existing != graph_.symbols.end() &&
            existing->second.properties.find("native.kind") != existing->second.properties.end() &&
            existing->second.properties.at("native.kind") == "callbackTable"
                ? "callbackTable"
                : "global";
        symbol.properties["native.linkage"] = storage == CX_SC_Static ? "internal" : "external";
        symbol.properties["native.fieldType"] = NormalizeTypeForId(
            ToString(clang_getTypeSpelling(clang_getCursorType(cursor))));
        symbol.properties["native.buildProfile"] = options_.profile;
        if (symbol.properties["native.kind"] == "callbackTable")
            symbol.properties["native.callbackTable"] = "true";
        AddSymbol(symbol);

        AddTypeReference(symbol.id, cursor, clang_getCursorType(cursor), "globalType");
        return true;
    }

    bool IsFileScopeCursor(CXCursor cursor)
    {
        CXCursor parent = clang_getCursorSemanticParent(cursor);
        return !clang_Cursor_isNull(parent) &&
               clang_getCursorKind(parent) == CXCursor_TranslationUnit;
    }

    bool AddFunction(CXCursor cursor)
    {
        auto file = SourceFile(cursor);
        if (!file) return false;
        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return false;

        EnsureFileSymbol(*file);

        const auto storage = clang_Cursor_getStorageClass(cursor);
        Symbol symbol;
        symbol.id = FunctionId(cursor);
        symbol.name = name;
        symbol.qualifiedName = name;
        symbol.kind = "method";
        symbol.filePath = *file;
        symbol.line = Line(cursor);
        symbol.parentId = "file:" + *file;
        symbol.visibility = storage == CX_SC_Static ? "private" : "public";
        symbol.isStatic = storage == CX_SC_Static;
        symbol.properties["native.kind"] = "function";
        symbol.properties["native.linkage"] = storage == CX_SC_Static ? "internal" : "external";
        symbol.properties["native.signature"] = Signature(cursor);
        symbol.properties["native.buildProfile"] = options_.profile;
        AddSymbol(symbol);

        AddParameterTypeReferences(cursor, symbol.id);
        AddTypeReference(symbol.id, cursor, clang_getCursorResultType(cursor), "returnType");
        return true;
    }

    void AddParameterTypeReferences(CXCursor cursor, const std::string& functionId)
    {
        const int count = clang_Cursor_getNumArguments(cursor);
        for (int i = 0; i < count; i++)
        {
            CXCursor arg = clang_Cursor_getArgument(cursor, static_cast<unsigned>(i));
            AddTypeReference(functionId, arg, clang_getCursorType(arg), "parameterType");
        }
    }

    void AddTypeReference(
        const std::string& sourceId,
        CXCursor evidenceCursor,
        CXType sourceType,
        const std::string& referenceKind)
    {
        CXType type = StripPointers(sourceType);
        CXCursor declaration = clang_getTypeDeclaration(type);
        if (clang_Cursor_isNull(declaration)) return;

        if (!EnsureTypeDeclaration(declaration, type)) return;

        Edge edge;
        edge.sourceId = sourceId;
        edge.targetId = TypeId(declaration);
        edge.kind = "references";
        edge.evidence = EvidenceFor(evidenceCursor, "semantic");
        edge.callSite = CallSiteFor(evidenceCursor, sourceId);
        edge.properties["native.referenceKind"] = referenceKind;
        edge.properties["native.buildProfile"] = options_.profile;
        AddEdge(edge);
    }

    bool EnsureTypeDeclaration(CXCursor declaration, CXType type)
    {
        switch (clang_getCursorKind(declaration))
        {
            case CXCursor_StructDecl:
            case CXCursor_UnionDecl:
                return AddRecordType(declaration, NativeKindForType(type));
            case CXCursor_EnumDecl:
                return AddRecordType(declaration, "enum");
            case CXCursor_TypedefDecl:
                return AddTypedefType(declaration);
            default:
                return false;
        }
    }

    void ProcessCall(CXCursor cursor, const VisitState& state)
    {
        if (state.currentFunctionId.empty()) return;
        CXCursor referenced = clang_getCursorReferenced(cursor);
        if (clang_Cursor_isNull(referenced)) return;
        if (clang_getCursorKind(referenced) != CXCursor_FunctionDecl) return;
        if (!AddFunction(referenced)) return;

        Edge edge;
        edge.sourceId = state.currentFunctionId;
        edge.targetId = FunctionId(referenced);
        edge.kind = "calls";
        edge.evidence = EvidenceFor(cursor, "semantic");
        edge.callSite = CallSiteFor(cursor, state.currentFunctionId);
        edge.properties["native.callKind"] = "direct";
        edge.properties["native.buildProfile"] = options_.profile;
        AddEdge(edge);
    }

    void ProcessDeclarationReference(CXCursor cursor, const VisitState& state)
    {
        CXCursor referenced = clang_getCursorReferenced(cursor);
        if (clang_Cursor_isNull(referenced)) return;

        if (!state.currentInitializerOwnerId.empty())
        {
            if (clang_getCursorKind(referenced) == CXCursor_FunctionDecl &&
                AddFunction(referenced))
            {
                MarkCallbackTable(state.currentInitializerOwnerId);
                AddReferenceEdge(
                    cursor,
                    state.currentInitializerOwnerId,
                    FunctionId(referenced),
                    "callbackTarget");
            }
            return;
        }

        if (state.currentFunctionId.empty()) return;

        switch (clang_getCursorKind(referenced))
        {
            case CXCursor_VarDecl:
                if (AddGlobalVariable(referenced))
                    AddReferenceEdge(cursor, state.currentFunctionId, GlobalVariableId(referenced), "globalAccess");
                break;
            case CXCursor_EnumConstantDecl:
            {
                CXCursor parent = clang_getCursorSemanticParent(referenced);
                if (clang_Cursor_isNull(parent) || !AddRecordType(parent, "enum"))
                    return;

                std::string enumTypeId = TypeId(parent);
                if (AddEnumConstant(referenced, enumTypeId))
                    AddReferenceEdge(
                        cursor,
                        state.currentFunctionId,
                        EnumConstantId(referenced, enumTypeId),
                        "enumMember");
                break;
            }
            default:
                break;
        }
    }

    void ProcessMemberRef(CXCursor cursor, const VisitState& state)
    {
        if (state.currentFunctionId.empty()) return;
        CXCursor referenced = clang_getCursorReferenced(cursor);
        if (clang_Cursor_isNull(referenced)) return;
        if (clang_getCursorKind(referenced) != CXCursor_FieldDecl) return;

        CXCursor owner = clang_getCursorSemanticParent(referenced);
        if (clang_Cursor_isNull(owner)) return;
        if (!AddRecordType(owner, "struct")) return;

        VisitState ownerState;
        ownerState.currentTypeId = TypeId(owner);
        ProcessField(referenced, ownerState);

        std::string fieldName = ToString(clang_getCursorSpelling(referenced));
        std::string ownerName = ToString(clang_getCursorSpelling(owner));
        if (fieldName.empty() || ownerName.empty()) return;

        Edge edge;
        edge.sourceId = state.currentFunctionId;
        edge.targetId = "field:" + ownerName + "." + fieldName;
        edge.kind = "references";
        edge.evidence = EvidenceFor(cursor, "semantic");
        edge.callSite = CallSiteFor(cursor, state.currentFunctionId);
        edge.properties["native.referenceKind"] = "fieldAccess";
        edge.properties["native.buildProfile"] = options_.profile;
        AddEdge(edge);
    }

    void AddReferenceEdge(
        CXCursor cursor,
        const std::string& sourceId,
        const std::string& targetId,
        const std::string& referenceKind)
    {
        Edge edge;
        edge.sourceId = sourceId;
        edge.targetId = targetId;
        edge.kind = "references";
        edge.evidence = EvidenceFor(cursor, "semantic");
        edge.callSite = CallSiteFor(cursor, sourceId);
        edge.properties["native.referenceKind"] = referenceKind;
        edge.properties["native.buildProfile"] = options_.profile;
        AddEdge(edge);
    }

    void MarkCallbackTable(const std::string& symbolId)
    {
        auto it = graph_.symbols.find(symbolId);
        if (it == graph_.symbols.end()) return;

        it->second.properties["native.kind"] = "callbackTable";
        it->second.properties["native.callbackTable"] = "true";
    }

    std::string NativeKindForType(CXType type)
    {
        switch (type.kind)
        {
            case CXType_Record:
                return "struct";
            case CXType_Enum:
                return "enum";
            default:
                return "type";
        }
    }

    std::string FunctionId(CXCursor cursor)
    {
        std::string name = ToString(clang_getCursorSpelling(cursor));
        std::vector<std::string> parameters;
        const int count = clang_Cursor_getNumArguments(cursor);
        for (int i = 0; i < count; i++)
        {
            CXCursor arg = clang_Cursor_getArgument(cursor, static_cast<unsigned>(i));
            parameters.push_back(NormalizeTypeForId(
                ToString(clang_getTypeSpelling(clang_getCursorType(arg)))));
        }

        std::ostringstream id;
        id << "method:" << name << "(";
        for (size_t i = 0; i < parameters.size(); i++)
        {
            if (i > 0) id << ",";
            id << parameters[i];
        }
        id << ")";
        return id.str();
    }

    std::string TypeId(CXCursor cursor)
    {
        return "type:" + ToString(clang_getCursorSpelling(cursor));
    }

    std::string GlobalVariableId(CXCursor cursor)
    {
        return "field:" + ToString(clang_getCursorSpelling(cursor));
    }

    std::string MacroId(const std::string& name)
    {
        return "field:macro:" + name;
    }

    std::string EnumConstantId(CXCursor cursor, const std::string& enumTypeId)
    {
        const std::string prefix = "type:";
        std::string enumName = enumTypeId.rfind(prefix, 0) == 0
            ? enumTypeId.substr(prefix.size())
            : enumTypeId;
        return "field:" + enumName + "." + ToString(clang_getCursorSpelling(cursor));
    }

    std::string Signature(CXCursor cursor)
    {
        std::ostringstream signature;
        signature << NormalizeTypeForId(ToString(clang_getTypeSpelling(clang_getCursorResultType(cursor))));
        signature << " (";
        const int count = clang_Cursor_getNumArguments(cursor);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) signature << ", ";
            CXCursor arg = clang_Cursor_getArgument(cursor, static_cast<unsigned>(i));
            signature << NormalizeTypeForId(ToString(clang_getTypeSpelling(clang_getCursorType(arg))));
        }
        signature << ")";
        return signature.str();
    }

    void AddSymbol(Symbol symbol)
    {
        graph_.symbols[symbol.id] = std::move(symbol);
    }

    void AddEdge(Edge edge)
    {
        auto key = std::make_tuple(edge.sourceId, edge.targetId, edge.kind);
        if (edgeKeys_.insert(key).second)
            graph_.edges.push_back(std::move(edge));
    }

    void EnsureFileSymbol(const std::string& relativePath)
    {
        std::string id = "file:" + relativePath;
        if (graph_.symbols.find(id) != graph_.symbols.end()) return;

        Symbol symbol;
        symbol.id = id;
        symbol.name = fs::path(relativePath).filename().string();
        symbol.qualifiedName = moduleName_ + "/" + relativePath;
        symbol.kind = "file";
        symbol.filePath = relativePath;
        symbol.parentId = moduleId_;
        symbol.visibility = "internal";
        symbol.properties["native.kind"] = EndsWith(relativePath, ".h") || EndsWith(relativePath, ".hpp")
            ? "header"
            : "translationUnit";
        symbol.properties["native.buildProfile"] = options_.profile;
        AddSymbol(symbol);
    }

    std::optional<std::string> SourceFile(CXCursor cursor)
    {
        CXSourceLocation location = clang_getCursorLocation(cursor);
        CXFile file = nullptr;
        unsigned line = 0, column = 0, offset = 0;
        clang_getSpellingLocation(location, &file, &line, &column, &offset);
        if (file == nullptr) return std::nullopt;
        return RelPath(file);
    }

    std::optional<std::string> RelPath(CXFile file)
    {
        fs::path path = ToString(clang_getFileName(file));
        if (path.empty()) return std::nullopt;
        return RelPath(path);
    }

    std::optional<std::string> RelPath(const fs::path& input)
    {
        fs::path path = input;
        if (!path.is_absolute())
            path = projectRoot_ / path;

        std::error_code ec;
        fs::path canonical = fs::weakly_canonical(path, ec);
        if (ec) canonical = fs::absolute(path, ec);
        if (ec) return std::nullopt;

        auto rel = fs::relative(canonical, projectRoot_, ec);
        if (ec || rel.empty()) return std::nullopt;
        auto text = SlashPath(rel.generic_string());
        if (text.rfind("..", 0) == 0) return std::nullopt;
        return text;
    }

    unsigned Line(CXCursor cursor)
    {
        CXSourceLocation location = clang_getCursorLocation(cursor);
        CXFile file = nullptr;
        unsigned line = 0, column = 0, offset = 0;
        clang_getSpellingLocation(location, &file, &line, &column, &offset);
        return line;
    }

    Evidence EvidenceFor(CXCursor cursor, const std::string& kind)
    {
        Evidence evidence;
        evidence.kind = kind;
        auto file = SourceFile(cursor);
        if (file)
            evidence.sourceSpan = *file + ":" + std::to_string(Line(cursor));
        return evidence;
    }

    std::optional<CallSite> CallSiteFor(CXCursor cursor, const std::string& containingSymbolId)
    {
        CXSourceRange range = clang_getCursorExtent(cursor);
        CXSourceLocation start = clang_getRangeStart(range);
        CXSourceLocation end = clang_getRangeEnd(range);

        CXFile startFile = nullptr;
        unsigned startLine = 0, startColumn = 0, startOffset = 0;
        clang_getSpellingLocation(start, &startFile, &startLine, &startColumn, &startOffset);
        if (startFile == nullptr) return std::nullopt;

        auto rel = RelPath(startFile);
        if (!rel) return std::nullopt;

        CXFile endFile = nullptr;
        unsigned endLine = 0, endColumn = 0, endOffset = 0;
        clang_getSpellingLocation(end, &endFile, &endLine, &endColumn, &endOffset);

        CallSite site;
        site.filePath = *rel;
        site.line = startLine;
        site.column = startColumn;
        site.endLine = endLine;
        site.endColumn = endColumn;
        site.containingSymbolId = containingSymbolId;
        return site;
    }

    std::string JoinDefines()
    {
        std::vector<std::string> values;
        values.reserve(commandLineDefines_.size());
        for (const auto& [name, value] : commandLineDefines_)
            values.push_back(name + "=" + value);
        return Join(values);
    }

    template <typename T>
    std::string Join(const T& values)
    {
        std::ostringstream output;
        bool first = true;
        for (const auto& value : values)
        {
            if (!first) output << ";";
            first = false;
            output << value;
        }
        return output.str();
    }

    Options options_;
    fs::path projectRoot_;
    fs::path compilationDatabaseDir_;
    std::string moduleName_;
    std::string moduleId_;
    NativeGraph& graph_;
    std::set<std::tuple<std::string, std::string, std::string>>& edgeKeys_;
    CXTranslationUnit currentUnit_ = nullptr;
    unsigned translationUnitCount_ = 0;
    std::map<std::string, std::string> commandLineDefines_;
    std::set<std::string> commandLineUndefines_;
};
}

LibClangExtractor::LibClangExtractor(Options options)
    : options_(std::move(options))
{
}

bool LibClangExtractor::Run()
{
    graph_.symbols.clear();
    graph_.edges.clear();
    edgeKeys_.clear();

    ExtractionSession session(options_, graph_, edgeKeys_);
    return session.Run();
}
}
