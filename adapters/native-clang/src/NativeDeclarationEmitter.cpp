#include "NativeDeclarationEmitter.h"
#include <utility>

namespace lifeblood::native_clang
{
NativeDeclarationEmitter::NativeDeclarationEmitter(
    std::string buildProfile,
    NativeGraphSink& graph,
    const ClangSourceMapper& sourceMap,
    NativeFileRegistry& files)
    : types_(buildProfile, graph, sourceMap, files),
      globals_(buildProfile, graph, sourceMap, files, types_),
      functions_(std::move(buildProfile), graph, sourceMap, files, types_)
{
}

bool NativeDeclarationEmitter::AddRecordType(CXCursor cursor, const std::string& nativeKind)
{
    return types_.AddRecordType(cursor, nativeKind);
}

bool NativeDeclarationEmitter::AddTypedefType(CXCursor cursor)
{
    return types_.AddTypedefType(cursor);
}

bool NativeDeclarationEmitter::AddEnumConstant(CXCursor cursor, const std::string& enumTypeId)
{
    return types_.AddEnumConstant(cursor, enumTypeId);
}

void NativeDeclarationEmitter::AddField(CXCursor cursor, const std::string& ownerTypeId)
{
    types_.AddField(cursor, ownerTypeId);
}

bool NativeDeclarationEmitter::AddGlobalVariable(CXCursor cursor)
{
    return globals_.AddGlobalVariable(cursor);
}

bool NativeDeclarationEmitter::AddFunction(CXCursor cursor)
{
    return functions_.AddFunction(cursor);
}
}
