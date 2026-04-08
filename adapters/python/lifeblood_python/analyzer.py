"""
Core analysis: discover Python files, extract symbols and edges using ast.
No external dependencies — uses only Python's built-in ast module.
"""

import ast
import os
from typing import Any


def analyze_project(project_root: str) -> tuple[list[dict], list[dict]]:
    """Analyze a Python project. Returns (symbols, edges)."""
    symbols: list[dict] = []
    edges: list[dict] = []

    module_name = os.path.basename(project_root)
    module_id = f"mod:{module_name}"

    symbols.append({
        "id": module_id,
        "name": module_name,
        "qualifiedName": module_name,
        "kind": "module",
    })

    # Discover .py files (exclude __pycache__, .venv, .git, etc.)
    py_files = _discover_files(project_root)

    # Build a map of qualified names → symbol IDs for edge resolution
    known_types: dict[str, str] = {}

    # First pass: extract symbols from all files
    for abs_path in sorted(py_files):
        rel_path = os.path.relpath(abs_path, project_root).replace("\\", "/")
        file_id = f"file:{rel_path}"

        symbols.append({
            "id": file_id,
            "name": os.path.basename(abs_path),
            "qualifiedName": f"{module_name}/{rel_path}",
            "kind": "file",
            "filePath": rel_path,
            "parentId": module_id,
        })

        try:
            with open(abs_path, encoding="utf-8", errors="replace") as f:
                source = f.read()
            tree = ast.parse(source, filename=abs_path)
        except SyntaxError:
            continue

        # Derive Python module path from file path
        py_module = _file_to_module(rel_path)

        file_symbols = _extract_symbols(tree, py_module, rel_path, file_id)
        symbols.extend(file_symbols)

        for sym in file_symbols:
            if sym["kind"] == "type":
                # Qualified name is authoritative; short name is convenience fallback.
                # If two classes share a short name, qualified name always resolves correctly.
                known_types[sym["qualifiedName"]] = sym["id"]
                short_name = sym["qualifiedName"].rsplit(".", 1)[-1]
                if short_name not in known_types:
                    known_types[short_name] = sym["id"]

    # Second pass: extract edges (needs known_types for resolution)
    for abs_path in sorted(py_files):
        rel_path = os.path.relpath(abs_path, project_root).replace("\\", "/")
        py_module = _file_to_module(rel_path)

        try:
            with open(abs_path, encoding="utf-8", errors="replace") as f:
                source = f.read()
            tree = ast.parse(source, filename=abs_path)
        except SyntaxError:
            continue

        file_edges = _extract_edges(tree, py_module, known_types)
        edges.extend(file_edges)

    return symbols, edges


def _discover_files(root: str) -> list[str]:
    """Find all .py files, excluding build artifacts and virtual environments."""
    skip_dirs = {
        "__pycache__", ".venv", "venv", ".git", ".tox",
        "node_modules", ".mypy_cache", ".pytest_cache",
        "build", "dist", ".eggs",
    }
    result = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [
            d for d in dirnames
            if d not in skip_dirs and not d.endswith(".egg-info")
        ]
        for f in filenames:
            if f.endswith(".py"):
                result.append(os.path.join(dirpath, f))
    return result


def _file_to_module(rel_path: str) -> str:
    """Convert a relative file path to a Python module path."""
    # foo/bar/baz.py → foo.bar.baz
    # foo/bar/__init__.py → foo.bar
    mod = rel_path.replace("/", ".").replace("\\", ".")
    if mod.endswith(".__init__.py"):
        mod = mod[:-len(".__init__.py")]
    elif mod.endswith(".py"):
        mod = mod[:-3]
    return mod


def _make_evidence() -> dict:
    return {
        "kind": "semantic",
        "adapterName": "Python",
        "confidence": "bestEffort",
    }


def _extract_symbols(
    tree: ast.Module,
    py_module: str,
    rel_path: str,
    file_id: str,
) -> list[dict]:
    """Extract type, method, and field symbols from an AST."""
    symbols: list[dict] = []

    for node in ast.iter_child_nodes(tree):
        if isinstance(node, ast.ClassDef):
            _extract_class(node, py_module, rel_path, file_id, symbols)
        elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            _extract_function(node, py_module, rel_path, file_id, symbols)

    return symbols


def _extract_class(
    node: ast.ClassDef,
    py_module: str,
    rel_path: str,
    file_id: str,
    symbols: list[dict],
) -> None:
    """Extract a class and its members."""
    qualified = f"{py_module}.{node.name}"
    type_id = f"type:{qualified}"

    is_abstract = any(
        isinstance(base, ast.Attribute) and base.attr == "ABC"
        or isinstance(base, ast.Name) and base.id in ("ABC", "ABCMeta")
        for base in node.bases
    )

    symbols.append({
        "id": type_id,
        "name": node.name,
        "qualifiedName": qualified,
        "kind": "type",
        "filePath": rel_path,
        "line": node.lineno,
        "parentId": file_id,
        "visibility": "private" if node.name.startswith("_") else "public",
        "isAbstract": is_abstract,
        "isStatic": False,
    })

    for item in ast.iter_child_nodes(node):
        if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)):
            method_qualified = f"{qualified}.{item.name}"
            method_id = _method_id(qualified, item)

            is_static = any(
                isinstance(d, ast.Name) and d.id == "staticmethod"
                for d in item.decorator_list
            )

            symbols.append({
                "id": method_id,
                "name": item.name,
                "qualifiedName": method_qualified,
                "kind": "method",
                "filePath": rel_path,
                "line": item.lineno,
                "parentId": type_id,
                "visibility": "private" if item.name.startswith("_") and not item.name.startswith("__") else "public",
                "isAbstract": False,
                "isStatic": is_static,
            })

        elif isinstance(item, ast.AnnAssign) and isinstance(item.target, ast.Name):
            field_name = item.target.id
            field_qualified = f"{qualified}.{field_name}"
            symbols.append({
                "id": f"field:{field_qualified}",
                "name": field_name,
                "qualifiedName": field_qualified,
                "kind": "field",
                "filePath": rel_path,
                "line": item.lineno,
                "parentId": type_id,
                "visibility": "private" if field_name.startswith("_") else "public",
            })


def _extract_function(
    node: ast.FunctionDef | ast.AsyncFunctionDef,
    py_module: str,
    rel_path: str,
    file_id: str,
    symbols: list[dict],
) -> None:
    """Extract a module-level function."""
    qualified = f"{py_module}.{node.name}"
    params = ", ".join(
        arg.arg for arg in node.args.args if arg.arg != "self"
    )
    method_id = f"method:{qualified}({params})"

    symbols.append({
        "id": method_id,
        "name": node.name,
        "qualifiedName": qualified,
        "kind": "method",
        "filePath": rel_path,
        "line": node.lineno,
        "parentId": file_id,
        "visibility": "private" if node.name.startswith("_") else "public",
        "isAbstract": False,
        "isStatic": True,
    })


def _extract_edges(
    tree: ast.Module,
    py_module: str,
    known_types: dict[str, str],
) -> list[dict]:
    """Extract inheritance, reference, and call edges from an AST."""
    edges: list[dict] = []
    evidence = _make_evidence()

    for node in ast.walk(tree):
        if isinstance(node, ast.ClassDef):
            type_id = f"type:{py_module}.{node.name}"

            # Inheritance edges
            for base in node.bases:
                base_name = _resolve_name(base)
                if base_name and base_name in known_types:
                    edges.append({
                        "sourceId": type_id,
                        "targetId": known_types[base_name],
                        "kind": "inherits",
                        "evidence": evidence,
                    })

            # Method calls to known types (constructor calls inside methods)
            for child in ast.walk(node):
                if isinstance(child, ast.Call):
                    call_name = _resolve_name(child.func)
                    if call_name and call_name in known_types:
                        # Find containing method
                        containing_method = _find_containing_method(child, node, py_module)
                        if containing_method:
                            edges.append({
                                "sourceId": containing_method,
                                "targetId": known_types[call_name],
                                "kind": "references",
                                "evidence": evidence,
                            })

            # Type annotations referencing known types
            for child in ast.walk(node):
                if isinstance(child, ast.AnnAssign) and child.annotation:
                    ann_name = _resolve_name(child.annotation)
                    if ann_name and ann_name in known_types and known_types[ann_name] != type_id:
                        edges.append({
                            "sourceId": type_id,
                            "targetId": known_types[ann_name],
                            "kind": "references",
                            "evidence": evidence,
                        })

        # Module-level imports referencing known types
        if isinstance(node, ast.ImportFrom):
            if node.names:
                for alias in node.names:
                    name = alias.name
                    if name in known_types:
                        # Edge from file to referenced type
                        file_module = py_module
                        edges.append({
                            "sourceId": f"file:{py_module.replace('.', '/')}.py",
                            "targetId": known_types[name],
                            "kind": "references",
                            "evidence": evidence,
                        })

    return edges


def _resolve_name(node: ast.expr | None) -> str | None:
    """Resolve an AST expression to a dotted name string."""
    if node is None:
        return None
    if isinstance(node, ast.Name):
        return node.id
    if isinstance(node, ast.Attribute):
        prefix = _resolve_name(node.value)
        if prefix:
            return f"{prefix}.{node.attr}"
    if isinstance(node, ast.Subscript):
        # Handle Generic[T], Optional[X], etc.
        return _resolve_name(node.value)
    return None


def _find_containing_method(
    node: ast.AST,
    class_node: ast.ClassDef,
    py_module: str,
) -> str | None:
    """Find the method ID containing a given node within a class."""
    class_qualified = f"{py_module}.{class_node.name}"
    for item in ast.iter_child_nodes(class_node):
        if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)):
            # Check if node is within this method's line range
            if hasattr(item, "lineno") and hasattr(item, "end_lineno"):
                if hasattr(node, "lineno") and item.lineno <= node.lineno <= (item.end_lineno or item.lineno + 100):
                    return _method_id(class_qualified, item)
    return None


def _method_id(class_qualified: str, node: ast.FunctionDef | ast.AsyncFunctionDef) -> str:
    """Generate a method symbol ID."""
    params = ", ".join(
        arg.arg for arg in node.args.args if arg.arg != "self" and arg.arg != "cls"
    )
    return f"method:{class_qualified}.{node.name}({params})"
