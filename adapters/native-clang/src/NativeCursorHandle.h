#pragma once

#include <clang-c/Index.h>

namespace lifeblood::native_clang
{
/// <summary>
/// Transitional libclang cursor handle for adapter-edge seams. Facades that do
/// not inspect cursor internals can depend on this wrapper while leaf emitters
/// keep the raw <c>CXCursor</c> work until the next DTO extraction atom.
/// INV-NATIVE-CLANG-LIBCLANG-001.
/// </summary>
struct NativeCursorHandle
{
    CXCursor cursor;
};
}
