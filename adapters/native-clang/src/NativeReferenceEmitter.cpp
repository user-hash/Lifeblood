#include "NativeReferenceEmitter.h"

#include "ClangUtilities.h"
#include "NativeSymbolIds.h"

#include <utility>

namespace lifeblood::native_clang
{
NativeReferenceEmitter::NativeReferenceEmitter(
    std::string buildProfile,
    NativeGraphSink& graph,
    const ClangSourceMapper& sourceMap,
    NativeDeclarationEmitter& declarations)
    : edges_(std::move(buildProfile), graph, sourceMap),
      declarations_(declarations)
{
}

void NativeReferenceEmitter::AddDirectCall(
    CXCursor cursor,
    const std::string& currentFunctionId)
{
    if (currentFunctionId.empty()) return;
    CXCursor referenced = clang_getCursorReferenced(cursor);
    if (clang_Cursor_isNull(referenced)) return;
    if (clang_getCursorKind(referenced) != CXCursor_FunctionDecl) return;
    if (!declarations_.AddFunction(referenced)) return;

    edges_.AddDirectCall(cursor, currentFunctionId, FunctionId(referenced));
}

void NativeReferenceEmitter::AddDeclarationReference(
    CXCursor cursor,
    const std::string& currentFunctionId,
    const std::string& initializerOwnerId)
{
    CXCursor referenced = clang_getCursorReferenced(cursor);
    if (clang_Cursor_isNull(referenced)) return;

    if (!initializerOwnerId.empty())
    {
        if (clang_getCursorKind(referenced) == CXCursor_FunctionDecl &&
            declarations_.AddFunction(referenced))
        {
            edges_.MarkCallbackTable(initializerOwnerId);
            edges_.AddReference(cursor, initializerOwnerId, FunctionId(referenced), "callbackTarget");
        }
        return;
    }

    if (currentFunctionId.empty()) return;

    switch (clang_getCursorKind(referenced))
    {
        case CXCursor_VarDecl:
            if (declarations_.AddGlobalVariable(referenced))
                edges_.AddReference(cursor, currentFunctionId, GlobalVariableId(referenced), "globalAccess");
            break;
        case CXCursor_EnumConstantDecl:
        {
            CXCursor parent = clang_getCursorSemanticParent(referenced);
            if (clang_Cursor_isNull(parent) || !declarations_.AddRecordType(parent, "enum"))
                return;

            std::string enumTypeId = TypeId(parent);
            if (declarations_.AddEnumConstant(referenced, enumTypeId))
                edges_.AddReference(
                    cursor,
                    currentFunctionId,
                    EnumConstantId(referenced, enumTypeId),
                    "enumMember");
            break;
        }
        default:
            break;
    }
}

void NativeReferenceEmitter::AddMemberReference(
    CXCursor cursor,
    const std::string& currentFunctionId)
{
    if (currentFunctionId.empty()) return;
    CXCursor referenced = clang_getCursorReferenced(cursor);
    if (clang_Cursor_isNull(referenced)) return;
    if (clang_getCursorKind(referenced) != CXCursor_FieldDecl) return;

    CXCursor owner = clang_getCursorSemanticParent(referenced);
    if (clang_Cursor_isNull(owner)) return;
    if (!declarations_.AddRecordType(owner, "struct")) return;

    declarations_.AddField(referenced, TypeId(owner));

    std::string fieldName = ToString(clang_getCursorSpelling(referenced));
    std::string ownerName = ToString(clang_getCursorSpelling(owner));
    if (fieldName.empty() || ownerName.empty()) return;

    edges_.AddReference(cursor, currentFunctionId, "field:" + ownerName + "." + fieldName, "fieldAccess");
}
}
