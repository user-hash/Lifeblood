#include "ClangUtilities.h"
#include "ClangCompileCommandReader.h"
#include "ClangSourceMapper.h"
#include "LibClangExtractor.h"
#include "NativeDeclarationEmitter.h"
#include "NativeFileRegistry.h"
#include "NativeGraphBuilder.h"
#include "NativeGraphSink.h"
#include "NativeModuleTracker.h"
#include "NativePreprocessorEmitter.h"
#include "NativeReferenceEmitter.h"
#include "NativeSymbolIds.h"

#include <clang-c/CXCompilationDatabase.h>
#include <clang-c/Index.h>

#include <filesystem>
#include <iostream>
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
          graph_(graph),
          module_(BaseName(projectRoot_), options_.profile, graph_),
          files_(module_.ModuleName(), module_.ModuleId(), options_.profile, graph_),
          declarations_(options_.profile, graph_, sourceMap_, files_),
          references_(options_.profile, graph_, sourceMap_, declarations_),
          preprocessor_(module_.ModuleId(), options_.profile, graph_, sourceMap_, files_)
    {
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
        NativeCompileCommand compileCommand = commandReader_.Read(command);
        module_.BeginTranslationUnit(compileCommand);

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
                preprocessor_.AddInclude(cursor);
                break;
            case CXCursor_MacroDefinition:
                preprocessor_.AddMacroDefinition(cursor, currentUnit_);
                break;
            case CXCursor_MacroExpansion:
                preprocessor_.AddMacroExpansion(cursor);
                break;
            case CXCursor_StructDecl:
                if (declarations_.AddRecordType(cursor, "struct"))
                    state.currentTypeId = TypeId(cursor);
                break;
            case CXCursor_UnionDecl:
                if (declarations_.AddRecordType(cursor, "union"))
                    state.currentTypeId = TypeId(cursor);
                break;
            case CXCursor_EnumDecl:
                if (declarations_.AddRecordType(cursor, "enum"))
                    state.currentTypeId = TypeId(cursor);
                break;
            case CXCursor_EnumConstantDecl:
                ProcessEnumConstant(cursor, state);
                break;
            case CXCursor_TypedefDecl:
                declarations_.AddTypedefType(cursor);
                break;
            case CXCursor_FieldDecl:
                declarations_.AddField(cursor, state.currentTypeId);
                break;
            case CXCursor_VarDecl:
            {
                auto initializerOwnerId = ProcessVariable(cursor, state);
                if (!initializerOwnerId.empty())
                    state.currentInitializerOwnerId = initializerOwnerId;
                break;
            }
            case CXCursor_FunctionDecl:
                if (declarations_.AddFunction(cursor))
                    state.currentFunctionId = FunctionId(cursor);
                break;
            case CXCursor_CallExpr:
                references_.AddDirectCall(cursor, state.currentFunctionId);
                break;
            case CXCursor_DeclRefExpr:
                references_.AddDeclarationReference(
                    cursor,
                    state.currentFunctionId,
                    state.currentInitializerOwnerId);
                break;
            case CXCursor_MemberRefExpr:
                references_.AddMemberReference(cursor, state.currentFunctionId);
                break;
            default:
                break;
        }
        return state;
    }

    void ProcessEnumConstant(CXCursor cursor, const VisitState& state)
    {
        if (state.currentTypeId.empty()) return;

        CXCursor parent = clang_getCursorSemanticParent(cursor);
        if (clang_Cursor_isNull(parent) || clang_getCursorKind(parent) != CXCursor_EnumDecl)
            return;
        if (!declarations_.AddRecordType(parent, "enum")) return;

        declarations_.AddEnumConstant(cursor, TypeId(parent));
    }

    std::string ProcessVariable(CXCursor cursor, const VisitState& state)
    {
        if (!state.currentFunctionId.empty() || !state.currentTypeId.empty())
            return "";

        if (!declarations_.AddGlobalVariable(cursor))
            return "";

        return GlobalVariableId(cursor);
    }

    Options options_;
    fs::path projectRoot_;
    fs::path compilationDatabaseDir_;
    ClangCompileCommandReader commandReader_;
    ClangSourceMapper sourceMap_;
    NativeGraphSink& graph_;
    NativeModuleTracker module_;
    NativeFileRegistry files_;
    NativeDeclarationEmitter declarations_;
    NativeReferenceEmitter references_;
    NativePreprocessorEmitter preprocessor_;
    CXTranslationUnit currentUnit_ = nullptr;
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
