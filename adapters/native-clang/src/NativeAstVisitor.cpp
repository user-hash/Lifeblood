#include "NativeAstVisitor.h"

#include "NativeKindNames.h"
#include "NativeSymbolIds.h"

namespace lifeblood::native_clang
{
NativeAstVisitor::NativeAstVisitor(
    NativeDeclarationEmitter& declarations,
    NativeReferenceEmitter& references,
    NativePreprocessorEmitter& preprocessor)
    : declarations_(declarations),
      references_(references),
      preprocessor_(preprocessor)
{
}

void NativeAstVisitor::Visit(CXCursor root, CXTranslationUnit unit)
{
    VisitCursor(root, unit, VisitState{});
}

CXChildVisitResult NativeAstVisitor::VisitChild(
    CXCursor cursor,
    CXCursor,
    CXClientData data)
{
    auto* payload = static_cast<ChildVisitPayload*>(data);
    payload->visitor->VisitCursor(cursor, payload->unit, payload->state);
    return CXChildVisit_Continue;
}

void NativeAstVisitor::VisitCursor(
    CXCursor cursor,
    CXTranslationUnit unit,
    VisitState state)
{
    VisitState childState = ProcessCursor(cursor, unit, state);
    ChildVisitPayload payload{ this, unit, childState };
    clang_visitChildren(cursor, &NativeAstVisitor::VisitChild, &payload);
}

NativeAstVisitor::VisitState NativeAstVisitor::ProcessCursor(
    CXCursor cursor,
    CXTranslationUnit unit,
    VisitState state)
{
    switch (clang_getCursorKind(cursor))
    {
        case CXCursor_InclusionDirective:
            preprocessor_.AddInclude(cursor);
            break;
        case CXCursor_MacroDefinition:
            preprocessor_.AddMacroDefinition(cursor, unit);
            break;
        case CXCursor_MacroExpansion:
            preprocessor_.AddMacroExpansion(cursor);
            break;
        case CXCursor_StructDecl:
            if (declarations_.AddRecordType({ cursor }, NativeKindNames::Struct))
                state.currentTypeId = TypeId(cursor);
            break;
        case CXCursor_UnionDecl:
            if (declarations_.AddRecordType({ cursor }, NativeKindNames::Union))
                state.currentTypeId = TypeId(cursor);
            break;
        case CXCursor_EnumDecl:
            if (declarations_.AddRecordType({ cursor }, NativeKindNames::Enum))
                state.currentTypeId = TypeId(cursor);
            break;
        case CXCursor_EnumConstantDecl:
            ProcessEnumConstant(cursor, state);
            break;
        case CXCursor_TypedefDecl:
            declarations_.AddTypedefType({ cursor });
            break;
        case CXCursor_FieldDecl:
            declarations_.AddField({ cursor }, state.currentTypeId);
            break;
        case CXCursor_VarDecl:
        {
            auto initializerOwnerId = ProcessVariable(cursor, state);
            if (!initializerOwnerId.empty())
                state.currentInitializerOwnerId = initializerOwnerId;
            break;
        }
        case CXCursor_InitListExpr:
            state = ProcessInitializerList(state);
            break;
        case CXCursor_FunctionDecl:
            if (declarations_.AddFunction({ cursor }))
                state.currentFunctionId = FunctionId(cursor);
            break;
        case CXCursor_CallExpr:
            references_.AddDirectCall(cursor, state.currentFunctionId);
            break;
        case CXCursor_StringLiteral:
        {
            auto initializerRowOrdinal = InitializerRowOrdinalForValue(state);
            references_.AddInitializerStringLiteral(
                cursor,
                state.currentInitializerOwnerId,
                initializerRowOrdinal);
            break;
        }
        case CXCursor_DeclRefExpr:
        {
            auto initializerRowOrdinal = InitializerRowOrdinalForValue(state);
            references_.AddDeclarationReference(
                cursor,
                state.currentFunctionId,
                state.currentInitializerOwnerId,
                initializerRowOrdinal);
            break;
        }
        case CXCursor_MemberRefExpr:
            references_.AddMemberReference(cursor, state.currentFunctionId);
            break;
        default:
            break;
    }
    return state;
}

void NativeAstVisitor::ProcessEnumConstant(CXCursor cursor, const VisitState& state)
{
    if (state.currentTypeId.empty()) return;

    CXCursor parent = clang_getCursorSemanticParent(cursor);
    if (clang_Cursor_isNull(parent) || clang_getCursorKind(parent) != CXCursor_EnumDecl)
        return;
    if (!declarations_.AddRecordType({ parent }, NativeKindNames::Enum)) return;

    declarations_.AddEnumConstant({ cursor }, TypeId(parent));
}

NativeAstVisitor::VisitState NativeAstVisitor::ProcessInitializerList(VisitState state)
{
    if (state.currentInitializerOwnerId.empty())
        return state;

    if (state.initializerListDepth == 1)
    {
        state.currentInitializerRowOrdinal =
            initializerRowOrdinals_[state.currentInitializerOwnerId]++;
    }

    state.initializerListDepth++;
    return state;
}

std::optional<unsigned> NativeAstVisitor::InitializerRowOrdinalForValue(VisitState state)
{
    if (state.currentInitializerOwnerId.empty())
        return std::nullopt;
    if (state.currentInitializerRowOrdinal)
        return state.currentInitializerRowOrdinal;
    if (state.initializerListDepth == 1)
        return initializerRowOrdinals_[state.currentInitializerOwnerId]++;
    return std::nullopt;
}

std::string NativeAstVisitor::ProcessVariable(CXCursor cursor, const VisitState& state)
{
    if (!state.currentFunctionId.empty() || !state.currentTypeId.empty())
        return "";

    if (!declarations_.AddGlobalVariable({ cursor }))
        return "";

    return GlobalVariableId(cursor);
}
}
