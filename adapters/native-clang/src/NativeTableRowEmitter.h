#pragma once

#include <clang-c/Index.h>

#include <map>
#include <optional>
#include <set>
#include <string>

namespace lifeblood::native_clang
{
class ClangSourceMapper;
class NativeGraphSink;

class NativeTableRowEmitter
{
public:
    NativeTableRowEmitter(
        std::string buildProfile,
        NativeGraphSink& graph,
        const ClangSourceMapper& sourceMap);

    void AddMethodGroupCell(
        CXCursor cursor,
        const std::string& tableId,
        std::optional<unsigned> rowOrdinal,
        const std::string& methodId);

private:
    unsigned ResolveRowOrdinal(
        const std::string& tableId,
        std::optional<unsigned> rowOrdinal);
    void EnsureRow(CXCursor cursor, const std::string& tableId, unsigned rowOrdinal);
    void AddCell(
        CXCursor cursor,
        const std::string& tableId,
        unsigned rowOrdinal,
        const std::string& methodId);

    void DecorateTable(const std::string& tableId);
    void DecorateRow(const std::string& rowId);

    static std::string RowId(const std::string& tableId, unsigned rowOrdinal);
    static std::string CellId(
        const std::string& tableId,
        unsigned rowOrdinal,
        unsigned cellOrdinal);
    static std::string TableName(const std::string& tableId);

    std::string buildProfile_;
    NativeGraphSink& graph_;
    const ClangSourceMapper& sourceMap_;
    std::map<std::string, unsigned> fallbackRowOrdinals_;
    std::map<std::string, unsigned> rowCellCounts_;
    std::map<std::string, std::set<unsigned>> tableRows_;
};
}
