#pragma once

#include <clang-c/Index.h>

namespace lifeblood::native_clang
{
class ClangIndex
{
public:
    ClangIndex();
    ~ClangIndex();

    ClangIndex(const ClangIndex&) = delete;
    ClangIndex& operator=(const ClangIndex&) = delete;

    ClangIndex(ClangIndex&&) = delete;
    ClangIndex& operator=(ClangIndex&&) = delete;

    CXIndex Get() const { return index_; }

private:
    CXIndex index_ = nullptr;
};
}
