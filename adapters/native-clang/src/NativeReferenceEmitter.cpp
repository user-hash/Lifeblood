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
    : buildProfile_(std::move(buildProfile)),
      graph_(graph),
      sourceMap_(sourceMap),
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

    Edge edge;
    edge.sourceId = currentFunctionId;
    edge.targetId = FunctionId(referenced);
    edge.kind = "calls";
    edge.evidence = sourceMap_.EvidenceFor(cursor, "semantic");
    edge.callSite = sourceMap_.CallSiteFor(cursor, currentFunctionId);
    edge.properties["native.callKind"] = "direct";
    edge.properties["native.buildProfile"] = buildProfile_;
    graph_.AddEdge(edge);
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
            MarkCallbackTable(initializerOwnerId);
            AddReferenceEdge(cursor, initializerOwnerId, FunctionId(referenced), "callbackTarget");
        }
        return;
    }

    if (currentFunctionId.empty()) return;

    switch (clang_getCursorKind(referenced))
    {
        case CXCursor_VarDecl:
            if (declarations_.AddGlobalVariable(referenced))
                AddReferenceEdge(cursor, currentFunctionId, GlobalVariableId(referenced), "globalAccess");
            break;
        case CXCursor_EnumConstantDecl:
        {
            CXCursor parent = clang_getCursorSemanticParent(referenced);
            if (clang_Cursor_isNull(parent) || !declarations_.AddRecordType(parent, "enum"))
                return;

            std::string enumTypeId = TypeId(parent);
            if (declarations_.AddEnumConstant(referenced, enumTypeId))
                AddReferenceEdge(
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

    Edge edge;
    edge.sourceId = currentFunctionId;
    edge.targetId = "field:" + ownerName + "." + fieldName;
    edge.kind = "references";
    edge.evidence = sourceMap_.EvidenceFor(cursor, "semantic");
    edge.callSite = sourceMap_.CallSiteFor(cursor, currentFunctionId);
    edge.properties["native.referenceKind"] = "fieldAccess";
    edge.properties["native.buildProfile"] = buildProfile_;
    graph_.AddEdge(edge);
}

void NativeReferenceEmitter::AddReferenceEdge(
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
    edge.properties["native.buildProfile"] = buildProfile_;
    graph_.AddEdge(edge);
}

void NativeReferenceEmitter::MarkCallbackTable(const std::string& symbolId)
{
    graph_.UpdateSymbol(symbolId, [](Symbol& symbol) {
        symbol.properties["native.kind"] = "callbackTable";
        symbol.properties["native.callbackTable"] = "true";
    });
}
}
