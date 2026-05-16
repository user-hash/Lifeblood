#pragma once

#include <map>
#include <optional>
#include <string>
#include <vector>

namespace lifeblood::native_clang
{
struct CallSite
{
    std::string filePath;
    unsigned line = 0;
    unsigned column = 0;
    unsigned endLine = 0;
    unsigned endColumn = 0;
    std::string containingSymbolId;
};

struct Evidence
{
    std::string kind;
    std::string adapterName = "native-clang";
    std::string sourceSpan;
    std::string confidence = "proven";
};

struct Symbol
{
    std::string id;
    std::string name;
    std::string qualifiedName;
    std::string kind;
    std::string filePath;
    unsigned line = 0;
    std::string parentId;
    std::string visibility = "internal";
    bool isStatic = false;
    std::map<std::string, std::string> properties;
};

struct Edge
{
    std::string sourceId;
    std::string targetId;
    std::string kind;
    Evidence evidence;
    std::optional<CallSite> callSite;
    std::map<std::string, std::string> properties;
};

struct NativeGraph
{
    std::map<std::string, Symbol> symbols;
    std::vector<Edge> edges;
};
}
