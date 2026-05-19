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
      typeMemberFacts_(sourceMap),
      typeMembers_(buildProfile, graph),
      globalFacts_(sourceMap),
      globals_(buildProfile, graph, files),
      functionFacts_(sourceMap),
      functions_(std::move(buildProfile), graph, files)
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
    auto facts = typeMemberFacts_.CollectEnumConstant(cursor, enumTypeId);
    return facts ? typeMembers_.AddEnumConstant(*facts) : false;
}

void NativeDeclarationEmitter::AddField(NativeCursorHandle cursor, const std::string& ownerTypeId)
{
    auto facts = typeMemberFacts_.CollectField(cursor, ownerTypeId);
    if (!facts) return;
    if (!typeMembers_.AddField(*facts)) return;

    typeMemberFacts_.AddFieldTypeReference(cursor, facts->symbolId, types_);
}

bool NativeDeclarationEmitter::AddGlobalVariable(NativeCursorHandle cursor)
{
    auto facts = globalFacts_.Collect(cursor);
    if (!facts) return false;
    if (!globals_.AddGlobalVariable(*facts)) return false;

    globalFacts_.AddTypeReference(cursor, facts->symbolId, types_);
    return true;
}

bool NativeDeclarationEmitter::AddFunction(NativeCursorHandle cursor)
{
    auto facts = functionFacts_.Collect(cursor);
    if (!facts) return false;

    auto status = functions_.AddFunction(*facts);
    if (status == NativeFunctionEmissionStatus::Rejected)
        return false;
    if (status == NativeFunctionEmissionStatus::Emitted)
        functionFacts_.AddTypeReferences(cursor, facts->symbolId, types_);

    return true;
}
}
