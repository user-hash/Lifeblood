#include "NativeExtractionSession.h"

#include <utility>

namespace fs = std::filesystem;

namespace lifeblood::native_clang
{
namespace
{
std::string BaseName(const fs::path& path)
{
    auto name = path.filename().string();
    return name.empty() ? "native-project" : name;
}
}

NativeExtractionSession::NativeExtractionSession(
    Options options,
    NativeGraphSink& graph)
    : options_(std::move(options)),
      projectRoot_(fs::weakly_canonical(options_.projectRoot)),
      compilationDatabaseDir_(ResolvePath(options_.compilationDatabaseDir)),
      commandReader_(compilationDatabaseDir_),
      unitParser_(),
      sourceMap_(projectRoot_),
      graph_(graph),
      module_(BaseName(projectRoot_), options_.profile, graph_),
      files_(module_.ModuleName(), module_.ModuleId(), options_.profile, graph_),
      declarations_(options_.profile, graph_, sourceMap_, files_),
      references_(options_.profile, graph_, sourceMap_, declarations_),
      preprocessor_(module_.ModuleId(), options_.profile, graph_, sourceMap_, files_),
      astVisitor_(declarations_, references_, preprocessor_)
{
}

bool NativeExtractionSession::Run()
{
    ClangCompilationDatabase database(compilationDatabaseDir_);
    if (!database.IsValid()) return false;

    ClangIndex index;
    bool ok = true;
    for (unsigned i = 0; i < database.Count(); i++)
    {
        CXCompileCommand command = database.CommandAt(i);
        ok = ParseCommand(index.Get(), command) && ok;
    }

    return ok;
}

fs::path NativeExtractionSession::ResolvePath(const fs::path& path) const
{
    fs::path resolved = path;
    if (!resolved.is_absolute())
        resolved = fs::absolute(resolved);

    std::error_code ec;
    auto canonical = fs::weakly_canonical(resolved, ec);
    return ec ? resolved.lexically_normal() : canonical;
}

bool NativeExtractionSession::ParseCommand(CXIndex index, CXCompileCommand command)
{
    NativeCompileCommand compileCommand = commandReader_.Read(command);
    module_.BeginTranslationUnit(compileCommand);

    auto unit = unitParser_.Parse(index, compileCommand);
    if (!unit)
    {
        module_.RecordTranslationUnitFailed();
        return false;
    }

    module_.RecordTranslationUnitParsed();
    astVisitor_.Visit(clang_getTranslationUnitCursor(unit.Get()), unit.Get());
    return true;
}
}
