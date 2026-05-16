#include "ClangUtilities.h"
#include "ClangCompileCommandReader.h"
#include "ClangSourceMapper.h"
#include "LibClangExtractor.h"
#include "NativeGraphBuilder.h"
#include "NativeGraphSink.h"
#include "NativeSymbolIds.h"

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
        NativeGraphSink& graph)
        : options_(std::move(options)),
          projectRoot_(fs::weakly_canonical(options_.projectRoot)),
          compilationDatabaseDir_(ResolvePath(options_.compilationDatabaseDir)),
          commandReader_(compilationDatabaseDir_),
          sourceMap_(projectRoot_),
          moduleName_(BaseName(projectRoot_)),
          moduleId_("mod:" + moduleName_),
          graph_(graph)
    {
        Symbol module;
        module.id = moduleId_;
        module.name = moduleName_;
        module.qualifiedName = moduleName_;
        module.kind = "module";
        module.properties["native.kind"] = "library";
        module.properties["native.buildProfile"] = options_.profile;
        graph_.AddSymbol(module);
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
        NativeCompileCommand compileCommand = commandReader_.Read(command);
        ApplyCommandLineMacros(compileCommand);
        UpdateModuleBuildProperties();

        std::vector<const char*> cArgs;
        cArgs.reserve(compileCommand.parseArguments.size());
        for (const auto& arg : compileCommand.parseArguments)
            cArgs.push_back(arg.c_str());

        CXTranslationUnit unit = nullptr;
        const unsigned parseOptions = CXTranslationUnit_DetailedPreprocessingRecord;
        CXErrorCode parseResult = clang_parseTranslationUnit2(
            index,
            compileCommand.sourcePath.string().c_str(),
            cArgs.data(),
            static_cast<int>(cArgs.size()),
            nullptr,
            0,
            parseOptions,
            &unit);

        if (parseResult != CXError_Success || unit == nullptr)
        {
            std::cerr << "Failed to parse " << compileCommand.sourcePath.string()
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

    void ApplyCommandLineMacros(const NativeCompileCommand& compileCommand)
    {
        for (const auto& define : compileCommand.defines)
        {
            commandLineDefines_[define.name] = define.value;
            AddMacroSymbol(define.name, std::nullopt, 0, "commandLine", define.value);
        }

        for (const auto& name : compileCommand.undefines)
            commandLineUndefines_.insert(name);
    }

    void UpdateModuleBuildProperties()
    {
        graph_.UpdateSymbol(moduleId_, [this](Symbol& module) {
            module.properties["native.translationUnitCount"] = std::to_string(translationUnitCount_);
            if (!commandLineDefines_.empty())
                module.properties["native.defines"] = JoinDefines();
            if (!commandLineUndefines_.empty())
                module.properties["native.undefines"] = Join(commandLineUndefines_);
        });
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
        auto sourceFile = sourceMap_.SourceFile(cursor);
        if (!sourceFile) return;

        CXFile included = clang_getIncludedFile(cursor);
        if (included == nullptr) return;
        auto includedPath = sourceMap_.RelativePath(included);
        if (!includedPath) return;

        EnsureFileSymbol(*sourceFile);
        EnsureFileSymbol(*includedPath);

        Edge edge;
        edge.sourceId = "file:" + *sourceFile;
        edge.targetId = "file:" + *includedPath;
        edge.kind = "references";
        edge.evidence = sourceMap_.EvidenceFor(cursor, "syntax");
        edge.callSite = sourceMap_.CallSiteFor(cursor, edge.sourceId);
        edge.properties["native.kind"] = "include";
        edge.properties["native.include"] = fs::path(*includedPath).filename().string();
        edge.properties["native.buildProfile"] = options_.profile;
        graph_.AddEdge(edge);
    }

    void ProcessMacroDefinition(CXCursor cursor)
    {
        auto file = sourceMap_.SourceFile(cursor);
        if (!file) return;

        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return;

        AddMacroSymbol(name, file, sourceMap_.Line(cursor), "source", MacroReplacement(cursor));
    }

    void ProcessMacroExpansion(CXCursor cursor)
    {
        auto file = sourceMap_.SourceFile(cursor);
        if (!file) return;

        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return;

        std::string targetId = MacroId(name);
        if (!graph_.HasSymbol(targetId))
            AddMacroSymbol(name, std::nullopt, 0, "unknown", "");

        EnsureFileSymbol(*file);

        Edge edge;
        edge.sourceId = "file:" + *file;
        edge.targetId = targetId;
        edge.kind = "references";
        edge.evidence = sourceMap_.EvidenceFor(cursor, "syntax");
        edge.callSite = sourceMap_.CallSiteFor(cursor, edge.sourceId);
        edge.properties["native.referenceKind"] = "macroExpansion";
        edge.properties["native.buildProfile"] = options_.profile;
        graph_.AddEdge(edge);
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
        graph_.AddSymbol(symbol);
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
        auto file = sourceMap_.SourceFile(cursor);
        if (!file) return false;

        EnsureFileSymbol(*file);

        Symbol symbol;
        symbol.id = TypeId(cursor);
        symbol.name = name;
        symbol.qualifiedName = name;
        symbol.kind = "type";
        symbol.filePath = *file;
        symbol.line = sourceMap_.Line(cursor);
        symbol.parentId = "file:" + *file;
        symbol.visibility = "public";
        symbol.properties["native.kind"] = nativeKind;
        symbol.properties["native.linkage"] = "none";
        symbol.properties["native.buildProfile"] = options_.profile;
        graph_.AddSymbol(symbol);
        return true;
    }

    bool AddTypedefType(CXCursor cursor)
    {
        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return false;
        auto file = sourceMap_.SourceFile(cursor);
        if (!file) return false;

        EnsureFileSymbol(*file);

        Symbol symbol;
        symbol.id = TypeId(cursor);
        symbol.name = name;
        symbol.qualifiedName = name;
        symbol.kind = "type";
        symbol.filePath = *file;
        symbol.line = sourceMap_.Line(cursor);
        symbol.parentId = "file:" + *file;
        symbol.visibility = "public";
        symbol.properties["native.kind"] = "typedef";
        symbol.properties["native.underlyingType"] = NormalizeTypeForId(
            ToString(clang_getTypeSpelling(clang_getTypedefDeclUnderlyingType(cursor))));
        symbol.properties["native.buildProfile"] = options_.profile;
        graph_.AddSymbol(symbol);

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
        auto file = sourceMap_.SourceFile(cursor);
        if (!file) return false;
        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return false;

        std::string enumName = OwnerNameFromTypeId(enumTypeId);

        Symbol symbol;
        symbol.id = "field:" + enumName + "." + name;
        symbol.name = name;
        symbol.qualifiedName = enumName + "." + name;
        symbol.kind = "field";
        symbol.filePath = *file;
        symbol.line = sourceMap_.Line(cursor);
        symbol.parentId = enumTypeId;
        symbol.visibility = "public";
        symbol.isStatic = true;
        symbol.properties["native.kind"] = "enumMember";
        symbol.properties["native.enumValue"] = std::to_string(clang_getEnumConstantDeclValue(cursor));
        symbol.properties["native.buildProfile"] = options_.profile;
        graph_.AddSymbol(symbol);
        return true;
    }

    void ProcessField(CXCursor cursor, const VisitState& state)
    {
        if (state.currentTypeId.empty()) return;
        auto file = sourceMap_.SourceFile(cursor);
        if (!file) return;
        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return;

        std::string owner = OwnerNameFromTypeId(state.currentTypeId);

        Symbol field;
        field.id = "field:" + owner + "." + name;
        field.name = name;
        field.qualifiedName = owner + "." + name;
        field.kind = "field";
        field.filePath = *file;
        field.line = sourceMap_.Line(cursor);
        field.parentId = state.currentTypeId;
        field.visibility = "public";
        field.properties["native.kind"] = "structField";
        field.properties["native.fieldType"] = NormalizeTypeForId(
            ToString(clang_getTypeSpelling(clang_getCursorType(cursor))));
        field.properties["native.buildProfile"] = options_.profile;
        graph_.AddSymbol(field);

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

        auto file = sourceMap_.SourceFile(cursor);
        if (!file) return false;
        std::string name = ToString(clang_getCursorSpelling(cursor));
        if (name.empty()) return false;

        EnsureFileSymbol(*file);

        const auto storage = clang_Cursor_getStorageClass(cursor);
        const std::string symbolId = GlobalVariableId(cursor);
        const Symbol* existing = graph_.FindSymbol(symbolId);

        Symbol symbol;
        symbol.id = symbolId;
        symbol.name = name;
        symbol.qualifiedName = name;
        symbol.kind = "field";
        symbol.filePath = *file;
        symbol.line = sourceMap_.Line(cursor);
        symbol.parentId = "file:" + *file;
        symbol.visibility = storage == CX_SC_Static ? "private" : "public";
        symbol.isStatic = storage == CX_SC_Static;
        symbol.properties["native.kind"] =
            existing != nullptr &&
            existing->properties.find("native.kind") != existing->properties.end() &&
            existing->properties.at("native.kind") == "callbackTable"
                ? "callbackTable"
                : "global";
        symbol.properties["native.linkage"] = storage == CX_SC_Static ? "internal" : "external";
        symbol.properties["native.fieldType"] = NormalizeTypeForId(
            ToString(clang_getTypeSpelling(clang_getCursorType(cursor))));
        symbol.properties["native.buildProfile"] = options_.profile;
        if (symbol.properties["native.kind"] == "callbackTable")
            symbol.properties["native.callbackTable"] = "true";
        graph_.AddSymbol(symbol);

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
        auto file = sourceMap_.SourceFile(cursor);
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
        symbol.line = sourceMap_.Line(cursor);
        symbol.parentId = "file:" + *file;
        symbol.visibility = storage == CX_SC_Static ? "private" : "public";
        symbol.isStatic = storage == CX_SC_Static;
        symbol.properties["native.kind"] = "function";
        symbol.properties["native.linkage"] = storage == CX_SC_Static ? "internal" : "external";
        symbol.properties["native.signature"] = Signature(cursor);
        symbol.properties["native.buildProfile"] = options_.profile;
        graph_.AddSymbol(symbol);

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
        edge.evidence = sourceMap_.EvidenceFor(evidenceCursor, "semantic");
        edge.callSite = sourceMap_.CallSiteFor(evidenceCursor, sourceId);
        edge.properties["native.referenceKind"] = referenceKind;
        edge.properties["native.buildProfile"] = options_.profile;
        graph_.AddEdge(edge);
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
        edge.evidence = sourceMap_.EvidenceFor(cursor, "semantic");
        edge.callSite = sourceMap_.CallSiteFor(cursor, state.currentFunctionId);
        edge.properties["native.callKind"] = "direct";
        edge.properties["native.buildProfile"] = options_.profile;
        graph_.AddEdge(edge);
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
        edge.evidence = sourceMap_.EvidenceFor(cursor, "semantic");
        edge.callSite = sourceMap_.CallSiteFor(cursor, state.currentFunctionId);
        edge.properties["native.referenceKind"] = "fieldAccess";
        edge.properties["native.buildProfile"] = options_.profile;
        graph_.AddEdge(edge);
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
        edge.evidence = sourceMap_.EvidenceFor(cursor, "semantic");
        edge.callSite = sourceMap_.CallSiteFor(cursor, sourceId);
        edge.properties["native.referenceKind"] = referenceKind;
        edge.properties["native.buildProfile"] = options_.profile;
        graph_.AddEdge(edge);
    }

    void MarkCallbackTable(const std::string& symbolId)
    {
        graph_.UpdateSymbol(symbolId, [](Symbol& symbol) {
            symbol.properties["native.kind"] = "callbackTable";
            symbol.properties["native.callbackTable"] = "true";
        });
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

    void EnsureFileSymbol(const std::string& relativePath)
    {
        std::string id = "file:" + relativePath;
        if (graph_.HasSymbol(id)) return;

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
        graph_.AddSymbol(symbol);
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
    ClangCompileCommandReader commandReader_;
    ClangSourceMapper sourceMap_;
    std::string moduleName_;
    std::string moduleId_;
    NativeGraphSink& graph_;
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
    NativeGraphBuilder graphBuilder(graph_);
    graphBuilder.Clear();

    ExtractionSession session(options_, graphBuilder);
    return session.Run();
}
}
