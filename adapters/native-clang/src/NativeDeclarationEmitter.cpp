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
      typeMembers_(buildProfile, graph, sourceMap, files, types_),
      globals_(buildProfile, graph, sourceMap, files, types_),
      functions_(std::move(buildProfile), graph, sourceMap, files, types_)
{
}

bool NativeDeclarationEmitter::AddRecordType(NativeCursorHandle cursor, const std::string& nativeKind)
{
    return types_.AddRecordType(cursor.cursor, nativeKind);
}

bool NativeDeclarationEmitter::AddTypedefType(NativeCursorHandle cursor)
{
    return types_.AddTypedefType(cursor.cursor);
}

bool NativeDeclarationEmitter::AddEnumConstant(NativeCursorHandle cursor, const std::string& enumTypeId)
{
    return typeMembers_.AddEnumConstant(cursor.cursor, enumTypeId);
}

void NativeDeclarationEmitter::AddField(NativeCursorHandle cursor, const std::string& ownerTypeId)
{
    typeMembers_.AddField(cursor.cursor, ownerTypeId);
}

bool NativeDeclarationEmitter::AddGlobalVariable(NativeCursorHandle cursor)
{
    return globals_.AddGlobalVariable(cursor.cursor);
}

bool NativeDeclarationEmitter::AddFunction(NativeCursorHandle cursor)
{
    return functions_.AddFunction(cursor.cursor);
}
}
