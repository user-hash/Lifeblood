#include "JsonGraphWriter.h"

#include <algorithm>
#include <map>
#include <ostream>
#include <sstream>
#include <tuple>

namespace lifeblood::native_clang
{
namespace
{
std::string JsonEscape(const std::string& value)
{
    std::ostringstream output;
    for (char ch : value)
    {
        switch (ch)
        {
            case '\\': output << "\\\\"; break;
            case '"': output << "\\\""; break;
            case '\b': output << "\\b"; break;
            case '\f': output << "\\f"; break;
            case '\n': output << "\\n"; break;
            case '\r': output << "\\r"; break;
            case '\t': output << "\\t"; break;
            default:
                const auto byte = static_cast<unsigned char>(ch);
                if (byte < 0x20)
                {
                    const char* digits = "0123456789abcdef";
                    output << "\\u00"
                           << digits[(byte >> 4) & 0x0f]
                           << digits[byte & 0x0f];
                }
                else
                {
                    output << ch;
                }
                break;
        }
    }
    return output.str();
}

void WriteProperties(
    std::ostream& output,
    const std::map<std::string, std::string>& properties,
    const std::string& indent)
{
    if (properties.empty()) return;
    output << ",\n" << indent << "\"properties\": {";
    bool first = true;
    for (const auto& [key, value] : properties)
    {
        if (!first) output << ",";
        first = false;
        output << "\n" << indent << "  \"" << JsonEscape(key) << "\": \""
               << JsonEscape(value) << "\"";
    }
    output << "\n" << indent << "}";
}

void WriteSymbol(std::ostream& output, const Symbol& symbol)
{
    output << "    {\n";
    output << "      \"id\": \"" << JsonEscape(symbol.id) << "\",\n";
    output << "      \"name\": \"" << JsonEscape(symbol.name) << "\",\n";
    output << "      \"qualifiedName\": \"" << JsonEscape(symbol.qualifiedName) << "\",\n";
    output << "      \"kind\": \"" << symbol.kind << "\"";
    if (!symbol.filePath.empty())
        output << ",\n      \"filePath\": \"" << JsonEscape(symbol.filePath) << "\"";
    if (symbol.line > 0)
        output << ",\n      \"line\": " << symbol.line;
    if (!symbol.parentId.empty())
        output << ",\n      \"parentId\": \"" << JsonEscape(symbol.parentId) << "\"";
    output << ",\n      \"visibility\": \"" << symbol.visibility << "\"";
    if (symbol.isStatic)
        output << ",\n      \"isStatic\": true";
    WriteProperties(output, symbol.properties, "      ");
    output << "\n    }";
}

void WriteEdge(std::ostream& output, const Edge& edge)
{
    output << "    {\n";
    output << "      \"sourceId\": \"" << JsonEscape(edge.sourceId) << "\",\n";
    output << "      \"targetId\": \"" << JsonEscape(edge.targetId) << "\",\n";
    output << "      \"kind\": \"" << edge.kind << "\",\n";
    output << "      \"evidence\": {\n";
    output << "        \"kind\": \"" << edge.evidence.kind << "\",\n";
    output << "        \"adapterName\": \"" << edge.evidence.adapterName << "\",\n";
    output << "        \"sourceSpan\": \"" << JsonEscape(edge.evidence.sourceSpan) << "\",\n";
    output << "        \"confidence\": \"" << edge.evidence.confidence << "\"\n";
    output << "      }";
    if (edge.callSite)
    {
        output << ",\n      \"callSite\": {\n";
        output << "        \"filePath\": \"" << JsonEscape(edge.callSite->filePath) << "\",\n";
        output << "        \"line\": " << edge.callSite->line << ",\n";
        output << "        \"column\": " << edge.callSite->column << ",\n";
        output << "        \"endLine\": " << edge.callSite->endLine << ",\n";
        output << "        \"endColumn\": " << edge.callSite->endColumn << ",\n";
        output << "        \"containingSymbolId\": \""
               << JsonEscape(edge.callSite->containingSymbolId) << "\"\n";
        output << "      }";
    }
    WriteProperties(output, edge.properties, "      ");
    output << "\n    }";
}
}

void WriteJsonGraph(std::ostream& output, const NativeGraph& graph)
{
    output << "{\n";
    output << "  \"version\": \"1.0\",\n";
    output << "  \"language\": \"c\",\n";
    output << "  \"adapter\": {\n";
    output << "    \"name\": \"native-clang\",\n";
    output << "    \"version\": \"0.1.0-dev\",\n";
    output << "    \"capabilities\": {\n";
    output << "      \"discoverSymbols\": true,\n";
    output << "      \"typeResolution\": \"proven\",\n";
    output << "      \"callResolution\": \"proven\",\n";
    output << "      \"implementationResolution\": \"none\",\n";
    output << "      \"crossModuleReferences\": \"none\",\n";
    output << "      \"overrideResolution\": \"none\"\n";
    output << "    }\n";
    output << "  },\n";

    output << "  \"symbols\": [\n";
    bool first = true;
    for (const auto& [_, symbol] : graph.symbols)
    {
        if (!first) output << ",\n";
        first = false;
        WriteSymbol(output, symbol);
    }
    output << "\n  ],\n";

    std::vector<Edge> edges = graph.edges;
    std::sort(edges.begin(), edges.end(), [](const Edge& a, const Edge& b) {
        return std::tie(a.sourceId, a.targetId, a.kind) <
               std::tie(b.sourceId, b.targetId, b.kind);
    });

    output << "  \"edges\": [\n";
    first = true;
    for (const auto& edge : edges)
    {
        if (!first) output << ",\n";
        first = false;
        WriteEdge(output, edge);
    }
    output << "\n  ]\n";
    output << "}\n";
}
}
