#include "NativeTableRowEmitter.h"

#include "ClangSourceMapper.h"
#include "ClangUtilities.h"
#include "GraphModel.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeGraphSink.h"
#include "NativeKindNames.h"
#include "NativePropertyWriter.h"
#include "NativeTableValueKinds.h"

#include <utility>

namespace lifeblood::native_clang
{
NativeTableRowEmitter::NativeTableRowEmitter(
    std::string buildProfile,
    NativeGraphSink& graph,
    const ClangSourceMapper& sourceMap)
    : buildProfile_(std::move(buildProfile)),
      graph_(graph),
      sourceMap_(sourceMap)
{
}

void NativeTableRowEmitter::AddMethodGroupCell(
    CXCursor cursor,
    const std::string& tableId,
    std::optional<unsigned> rowOrdinal,
    const std::string& methodId)
{
    const unsigned resolvedRowOrdinal = ResolveRowOrdinal(tableId, rowOrdinal);
    EnsureRow(cursor, tableId, resolvedRowOrdinal);
    AddMethodGroupCellSymbol(cursor, tableId, resolvedRowOrdinal, methodId);
}

void NativeTableRowEmitter::AddStringCell(
    CXCursor cursor,
    const std::string& tableId,
    std::optional<unsigned> rowOrdinal)
{
    const unsigned resolvedRowOrdinal = ResolveRowOrdinal(tableId, rowOrdinal);
    EnsureRow(cursor, tableId, resolvedRowOrdinal);
    AddStringCellSymbol(cursor, tableId, resolvedRowOrdinal);
}

unsigned NativeTableRowEmitter::ResolveRowOrdinal(
    const std::string& tableId,
    std::optional<unsigned> rowOrdinal)
{
    if (rowOrdinal) return *rowOrdinal;
    return fallbackRowOrdinals_[tableId]++;
}

void NativeTableRowEmitter::EnsureRow(
    CXCursor cursor,
    const std::string& tableId,
    unsigned rowOrdinal)
{
    const std::string id = RowId(tableId, rowOrdinal);
    const bool isNewRow = tableRows_[tableId].insert(rowOrdinal).second;
    if (!isNewRow)
        return;

    const std::string tableName = TableName(tableId);
    Symbol row;
    row.id = id;
    row.name = tableName + "[" + std::to_string(rowOrdinal) + "]";
    row.qualifiedName = row.name;
    row.kind = "field";
    if (auto file = sourceMap_.SourceFile(cursor))
        row.filePath = *file;
    row.line = sourceMap_.Line(cursor);
    row.parentId = tableId;
    NativePropertyWriter::Set(row, NativeGraphPropertyKeys::NativeKind, NativeKindNames::TableRow);
    NativePropertyWriter::Set(row, NativeGraphPropertyKeys::TableOwnerId, tableId);
    NativePropertyWriter::Set(
        row,
        NativeGraphPropertyKeys::TableRowOrdinal,
        std::to_string(rowOrdinal));
    NativePropertyWriter::Set(row, NativeGraphPropertyKeys::TableCellCount, "0");
    NativePropertyWriter::Set(row, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddSymbol(row);

    DecorateTable(tableId);
}

void NativeTableRowEmitter::AddMethodGroupCellSymbol(
    CXCursor cursor,
    const std::string& tableId,
    unsigned rowOrdinal,
    const std::string& methodId)
{
    const std::string rowId = RowId(tableId, rowOrdinal);
    const unsigned cellOrdinal = rowCellCounts_[rowId]++;
    const std::string tableName = TableName(tableId);

    Symbol cell;
    cell.id = CellId(tableId, rowOrdinal, cellOrdinal);
    cell.name = "cell:" + std::to_string(cellOrdinal);
    cell.qualifiedName = tableName + "[" + std::to_string(rowOrdinal) + "]." + cell.name;
    cell.kind = "field";
    if (auto file = sourceMap_.SourceFile(cursor))
        cell.filePath = *file;
    cell.line = sourceMap_.Line(cursor);
    cell.parentId = rowId;
    NativePropertyWriter::Set(cell, NativeGraphPropertyKeys::NativeKind, NativeKindNames::TableCell);
    NativePropertyWriter::Set(cell, NativeGraphPropertyKeys::TableOwnerId, tableId);
    NativePropertyWriter::Set(
        cell,
        NativeGraphPropertyKeys::TableRowOrdinal,
        std::to_string(rowOrdinal));
    NativePropertyWriter::Set(
        cell,
        NativeGraphPropertyKeys::TableCellOrdinal,
        std::to_string(cellOrdinal));
    NativePropertyWriter::Set(
        cell,
        NativeGraphPropertyKeys::TableValueKind,
        NativeTableValueKinds::MethodGroup);
    NativePropertyWriter::Set(cell, NativeGraphPropertyKeys::MethodGroupId, methodId);
    NativePropertyWriter::Set(cell, NativeGraphPropertyKeys::CallbackTargetId, methodId);
    NativePropertyWriter::Set(cell, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddSymbol(cell);

    DecorateRow(rowId);
}

void NativeTableRowEmitter::AddStringCellSymbol(
    CXCursor cursor,
    const std::string& tableId,
    unsigned rowOrdinal)
{
    const std::string rowId = RowId(tableId, rowOrdinal);
    const unsigned cellOrdinal = rowCellCounts_[rowId]++;
    const std::string tableName = TableName(tableId);

    Symbol cell;
    cell.id = CellId(tableId, rowOrdinal, cellOrdinal);
    cell.name = "cell:" + std::to_string(cellOrdinal);
    cell.qualifiedName = tableName + "[" + std::to_string(rowOrdinal) + "]." + cell.name;
    cell.kind = "field";
    if (auto file = sourceMap_.SourceFile(cursor))
        cell.filePath = *file;
    cell.line = sourceMap_.Line(cursor);
    cell.parentId = rowId;
    NativePropertyWriter::Set(cell, NativeGraphPropertyKeys::NativeKind, NativeKindNames::TableCell);
    NativePropertyWriter::Set(cell, NativeGraphPropertyKeys::TableOwnerId, tableId);
    NativePropertyWriter::Set(
        cell,
        NativeGraphPropertyKeys::TableRowOrdinal,
        std::to_string(rowOrdinal));
    NativePropertyWriter::Set(
        cell,
        NativeGraphPropertyKeys::TableCellOrdinal,
        std::to_string(cellOrdinal));
    NativePropertyWriter::Set(
        cell,
        NativeGraphPropertyKeys::TableValueKind,
        NativeTableValueKinds::String);
    NativePropertyWriter::Set(
        cell,
        NativeGraphPropertyKeys::StringValue,
        StringLiteralValue(cursor));
    NativePropertyWriter::Set(cell, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddSymbol(cell);

    DecorateRow(rowId);
}

void NativeTableRowEmitter::DecorateTable(const std::string& tableId)
{
    graph_.UpdateSymbol(tableId, [&](Symbol& table) {
        NativePropertyWriter::Set(
            table,
            NativeGraphPropertyKeys::TableRowCount,
            std::to_string(tableRows_[tableId].size()));
    });
}

void NativeTableRowEmitter::DecorateRow(const std::string& rowId)
{
    graph_.UpdateSymbol(rowId, [&](Symbol& row) {
        NativePropertyWriter::Set(
            row,
            NativeGraphPropertyKeys::TableCellCount,
            std::to_string(rowCellCounts_[rowId]));
    });
}

std::string NativeTableRowEmitter::RowId(
    const std::string& tableId,
    unsigned rowOrdinal)
{
    return tableId + ":row:" + std::to_string(rowOrdinal);
}

std::string NativeTableRowEmitter::CellId(
    const std::string& tableId,
    unsigned rowOrdinal,
    unsigned cellOrdinal)
{
    return RowId(tableId, rowOrdinal) + ":cell:" + std::to_string(cellOrdinal);
}

std::string NativeTableRowEmitter::TableName(const std::string& tableId)
{
    constexpr const char* prefix = "field:";
    const std::string prefixText = prefix;
    return tableId.rfind(prefixText, 0) == 0
        ? tableId.substr(prefixText.size())
        : tableId;
}

std::string NativeTableRowEmitter::StringLiteralValue(CXCursor cursor)
{
    CXEvalResult result = clang_Cursor_Evaluate(cursor);
    std::string value;
    if (result != nullptr && clang_EvalResult_getKind(result) == CXEval_StrLiteral)
    {
        const char* text = clang_EvalResult_getAsStr(result);
        if (text != nullptr)
            value = text;
    }
    if (result != nullptr)
        clang_EvalResult_dispose(result);
    if (!value.empty())
        return value;

    CXTranslationUnit unit = clang_Cursor_getTranslationUnit(cursor);
    CXToken* tokens = nullptr;
    unsigned tokenCount = 0;
    clang_tokenize(unit, clang_getCursorExtent(cursor), &tokens, &tokenCount);
    if (tokenCount > 0)
        value = ToString(clang_getTokenSpelling(unit, tokens[0]));
    clang_disposeTokens(unit, tokens, tokenCount);

    const auto firstQuote = value.find('"');
    const auto lastQuote = value.find_last_of('"');
    if (firstQuote != std::string::npos &&
        lastQuote != std::string::npos &&
        firstQuote < lastQuote)
    {
        return value.substr(firstQuote + 1, lastQuote - firstQuote - 1);
    }

    return value;
}
}
