#include "ClangIndex.h"

namespace lifeblood::native_clang
{
ClangIndex::ClangIndex()
    : index_(clang_createIndex(/*excludeDeclarationsFromPCH*/ 0, /*displayDiagnostics*/ 0))
{
}

ClangIndex::~ClangIndex()
{
    if (index_ != nullptr)
        clang_disposeIndex(index_);
}
}
