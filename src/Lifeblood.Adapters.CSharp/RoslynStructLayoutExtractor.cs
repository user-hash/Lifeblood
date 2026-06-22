using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Struct-layout calculator for C# source/metadata structs. The exact lane is
/// intentionally narrow: blittable fields, known primitive / enum / nested
/// struct sizes, and Sequential or Explicit layout. Any layout fact Roslyn cannot
/// prove is still surfaced, but the report downgrades to Advisory with a named
/// limitation. INV-STRUCT-LAYOUT-001.
/// </summary>
internal static class RoslynStructLayoutExtractor
{
    private const int DefaultPack = 8;
    private static readonly int PointerSize = IntPtr.Size;

    internal static StructLayoutReport? Extract(string canonicalTypeId, INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct) return null;

        var layout = GetStructLayout(type, new HashSet<string>(StringComparer.Ordinal));
        return new StructLayoutReport
        {
            TypeId = canonicalTypeId,
            TypeName = Display(type),
            LayoutKind = layout.LayoutKind,
            Pack = layout.Pack,
            DeclaredSize = layout.DeclaredSize == 0 ? null : layout.DeclaredSize,
            Size = layout.Size,
            Alignment = layout.Alignment,
            PointerSize = PointerSize,
            IsUnmanaged = type.IsUnmanagedType,
            IsBlittable = layout.IsBlittable,
            Confidence = layout.IsExact ? StructLayoutConfidence.Exact : StructLayoutConfidence.Advisory,
            Limitations = layout.Limitations.Distinct(StringComparer.Ordinal).ToArray(),
            Fields = layout.Fields,
        };
    }

    private static LayoutInfo GetStructLayout(INamedTypeSymbol type, HashSet<string> stack)
    {
        var key = Display(type);
        if (!stack.Add(key))
        {
            return AdvisoryPrimitive(PointerSize, PointerSize,
                $"Recursive struct layout reference for {key}; using pointer-size advisory fallback.");
        }

        var attr = ReadLayoutAttribute(type);
        var fields = type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && !f.IsConst)
            .ToArray();

        var limitations = new List<string>();
        if (attr.Kind == StructLayoutKind.Auto)
            limitations.Add("LayoutKind.Auto has no stable field-offset contract; offsets and total size are advisory.");

        var fieldReports = new List<StructLayoutField>();
        var maxAlign = 1;
        var running = 0;
        var exact = attr.Kind != StructLayoutKind.Auto;
        var blittable = true;

        foreach (var field in fields)
        {
            var fieldLayout = GetFieldLayout(field, stack);
            foreach (var limitation in fieldLayout.Limitations) limitations.Add($"Field {field.Name}: {limitation}");
            if (!fieldLayout.IsExact) exact = false;
            if (!fieldLayout.IsBlittable) blittable = false;

            var fieldAlign = Math.Max(1, Math.Min(fieldLayout.Alignment ?? PointerSize, attr.Pack));
            maxAlign = Math.Max(maxAlign, fieldAlign);

            int? offset = null;
            if (attr.Kind == StructLayoutKind.Sequential)
            {
                running = AlignUp(running, fieldAlign);
                offset = running;
                running += fieldLayout.Size ?? 0;
            }
            else if (attr.Kind == StructLayoutKind.Explicit)
            {
                offset = ReadFieldOffset(field);
                if (offset == null)
                {
                    exact = false;
                    limitations.Add($"Field {field.Name}: explicit-layout field is missing FieldOffsetAttribute.");
                    offset = 0;
                }

                running = Math.Max(running, offset.Value + (fieldLayout.Size ?? 0));
            }

            var (file, line, column) = SourceLocation(field);
            fieldReports.Add(new StructLayoutField
            {
                Name = field.Name,
                Type = Display(field.Type),
                Offset = offset,
                Size = fieldLayout.Size,
                Alignment = fieldLayout.Alignment,
                FixedBufferLength = field.IsFixedSizeBuffer ? field.FixedSize : (int?)null,
                FilePath = file,
                Line = line,
                Column = column,
                Limitations = fieldLayout.Limitations.Distinct(StringComparer.Ordinal).ToArray(),
            });
        }

        int? size = null;
        int? alignment = attr.Kind == StructLayoutKind.Auto ? null : maxAlign;
        if (attr.Kind is StructLayoutKind.Sequential or StructLayoutKind.Explicit)
        {
            var minimum = Math.Max(running, attr.DeclaredSize);
            if (fields.Length == 0) minimum = Math.Max(minimum, 1);
            size = AlignUp(minimum, maxAlign);
        }

        stack.Remove(key);
        return new LayoutInfo
        {
            LayoutKind = attr.Kind,
            Pack = attr.Pack,
            DeclaredSize = attr.DeclaredSize,
            Size = size,
            Alignment = alignment,
            IsExact = exact,
            IsBlittable = blittable,
            Limitations = limitations,
            Fields = fieldReports.ToArray(),
        };
    }

    private static LayoutInfo GetFieldLayout(IFieldSymbol field, HashSet<string> stack)
    {
        if (field.IsFixedSizeBuffer)
        {
            var elementType = field.Type is IPointerTypeSymbol pointer ? pointer.PointedAtType : field.Type;
            var element = GetTypeLayout(elementType, stack);
            var size = element.Size.HasValue ? element.Size.Value * field.FixedSize : (int?)null;
            return element with
            {
                Size = size,
                Limitations = element.Limitations,
            };
        }

        return GetTypeLayout(field.Type, stack);
    }

    private static LayoutInfo GetTypeLayout(ITypeSymbol type, HashSet<string> stack)
    {
        if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
            return ExactPrimitive(PointerSize, PointerSize);

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
            return enumType.EnumUnderlyingType == null
                ? ExactPrimitive(4, 4)
                : GetTypeLayout(enumType.EnumUnderlyingType, stack);

        switch (type.SpecialType)
        {
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
                return ExactPrimitive(1, 1);
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
                return ExactPrimitive(2, 2);
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Single:
                return ExactPrimitive(4, 4);
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Double:
                return ExactPrimitive(8, 8);
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
                return ExactPrimitive(PointerSize, PointerSize);
            case SpecialType.System_Boolean:
                return AdvisoryPrimitive(1, 1, "bool is non-blittable; managed field size is reported as 1 byte.");
            case SpecialType.System_Char:
                return AdvisoryPrimitive(2, 2, "char is non-blittable; managed field size is reported as 2 bytes.");
            case SpecialType.System_Decimal:
                return AdvisoryPrimitive(16, 4, "decimal layout is runtime-defined; size/alignment are advisory.");
        }

        if (type is INamedTypeSymbol named && named.TypeKind == TypeKind.Struct)
            return GetStructLayout(named, stack);

        if (type.IsReferenceType)
            return AdvisoryPrimitive(PointerSize, PointerSize, "reference-bearing field; using pointer-size advisory approximation.");

        return AdvisoryPrimitive(PointerSize, PointerSize,
            $"Unknown layout for type {Display(type)}; using pointer-size advisory fallback.");
    }

    private static LayoutInfo ExactPrimitive(int size, int alignment)
        => new()
        {
            LayoutKind = StructLayoutKind.Sequential,
            Pack = DefaultPack,
            Size = size,
            Alignment = alignment,
            IsExact = true,
            IsBlittable = true,
        };

    private static LayoutInfo AdvisoryPrimitive(int size, int alignment, string limitation)
        => new()
        {
            LayoutKind = StructLayoutKind.Sequential,
            Pack = DefaultPack,
            Size = size,
            Alignment = alignment,
            IsExact = false,
            IsBlittable = false,
            Limitations = new List<string> { limitation },
        };

    private static LayoutAttributeInfo ReadLayoutAttribute(INamedTypeSymbol type)
    {
        var kind = StructLayoutKind.Sequential;
        var pack = DefaultPack;
        var declaredSize = 0;

        var attr = type.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.StructLayoutAttribute");
        if (attr == null)
            return new LayoutAttributeInfo(kind, pack, declaredSize);

        if (attr.ConstructorArguments.Length > 0)
            kind = LayoutKindName(attr.ConstructorArguments[0].Value);

        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "Pack")
                pack = NormalizePack(ToInt(named.Value.Value));
            else if (named.Key == "Size")
                declaredSize = Math.Max(0, ToInt(named.Value.Value));
        }

        return new LayoutAttributeInfo(kind, pack, declaredSize);
    }

    private static int? ReadFieldOffset(IFieldSymbol field)
    {
        var attr = field.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.FieldOffsetAttribute");
        if (attr == null || attr.ConstructorArguments.Length == 0) return null;
        return ToInt(attr.ConstructorArguments[0].Value);
    }

    private static int NormalizePack(int pack)
        => pack <= 0 ? DefaultPack : pack;

    private static int ToInt(object? value)
        => value switch
        {
            null => 0,
            int i => i,
            short s => s,
            byte b => b,
            long l => checked((int)l),
            _ => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture),
        };

    private static string LayoutKindName(object? value)
        => ToInt(value) switch
        {
            2 => StructLayoutKind.Explicit,
            3 => StructLayoutKind.Auto,
            _ => StructLayoutKind.Sequential,
        };

    private static int AlignUp(int value, int alignment)
    {
        if (alignment <= 1) return value;
        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    private static string Display(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);

    private static (string? File, int? Line, int? Column) SourceLocation(ISymbol symbol)
    {
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc == null) return (null, null, null);
        var span = loc.GetMappedLineSpan();
        return (span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private sealed record LayoutAttributeInfo(string Kind, int Pack, int DeclaredSize);

    private sealed record LayoutInfo
    {
        public string LayoutKind { get; init; } = StructLayoutKind.Sequential;
        public int Pack { get; init; } = DefaultPack;
        public int DeclaredSize { get; init; }
        public int? Size { get; init; }
        public int? Alignment { get; init; }
        public bool IsExact { get; init; }
        public bool IsBlittable { get; init; }
        public List<string> Limitations { get; init; } = new();
        public StructLayoutField[] Fields { get; init; } = Array.Empty<StructLayoutField>();
    }
}
