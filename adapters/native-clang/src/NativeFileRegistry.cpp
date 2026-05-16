#include "NativeFileRegistry.h"

#include "ClangUtilities.h"

#include <filesystem>
#include <utility>

namespace fs = std::filesystem;

namespace lifeblood::native_clang
{
NativeFileRegistry::NativeFileRegistry(
    std::string moduleName,
    std::string moduleId,
    std::string buildProfile,
    NativeGraphSink& graph)
    : moduleName_(std::move(moduleName)),
      moduleId_(std::move(moduleId)),
      buildProfile_(std::move(buildProfile)),
      graph_(graph)
{
}

void NativeFileRegistry::EnsureFileSymbol(const std::string& relativePath)
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
    symbol.properties["native.buildProfile"] = buildProfile_;
    graph_.AddSymbol(symbol);
}
}
