#pragma once

#include "NativeDeclarationEmitter.h"
#include "NativePreprocessorEmitter.h"
#include "NativeReferenceEmitter.h"

#include <clang-c/Index.h>

#include <map>
#include <optional>
#include <string>

namespace lifeblood::native_clang
{
class NativeAstVisitor
{
public:
    NativeAstVisitor(
        NativeDeclarationEmitter& declarations,
        NativeReferenceEmitter& references,
        NativePreprocessorEmitter& preprocessor);

    void Visit(CXCursor root, CXTranslationUnit unit);

private:
    struct VisitState
    {
        std::string currentFunctionId;
        std::string currentTypeId;
        std::string currentInitializerOwnerId;
        unsigned initializerListDepth = 0;
        std::optional<unsigned> currentInitializerRowOrdinal;
    };

    struct ChildVisitPayload
    {
        NativeAstVisitor* visitor;
        CXTranslationUnit unit;
        VisitState state;
    };

    static CXChildVisitResult VisitChild(CXCursor cursor, CXCursor parent, CXClientData data);

    void VisitCursor(CXCursor cursor, CXTranslationUnit unit, VisitState state);
    VisitState ProcessCursor(CXCursor cursor, CXTranslationUnit unit, VisitState state);

    void ProcessEnumConstant(CXCursor cursor, const VisitState& state);
    VisitState ProcessInitializerList(VisitState state);
    std::optional<unsigned> InitializerRowOrdinalForValue(VisitState state);
    std::string ProcessVariable(CXCursor cursor, const VisitState& state);

    NativeDeclarationEmitter& declarations_;
    NativeReferenceEmitter& references_;
    NativePreprocessorEmitter& preprocessor_;
    std::map<std::string, unsigned> initializerRowOrdinals_;
};
}
