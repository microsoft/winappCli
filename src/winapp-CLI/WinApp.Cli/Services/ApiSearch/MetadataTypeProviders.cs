// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// The generic parameter names in scope while a signature is decoded, passed as the
/// <c>genericContext</c> to <see cref="SimpleTypeProvider"/>.
/// <para>
/// A signature blob records a generic parameter positionally (<c>!0</c>, <c>!!0</c>), not
/// by name, so without this a decoder can only invent one. Inventing it renders a real API
/// as <c>DenseTensor&lt;TMethod0&gt; ToTensor(TMethod0[] array)</c> — an identifier that
/// exists nowhere in the SDK and does not compile. The declared names live in the
/// GenericParam rows of the method and its declaring type.
/// </para>
/// </summary>
internal sealed record GenericNameContext(ImmutableArray<string> TypeParameters, ImmutableArray<string> MethodParameters)
{
    internal static readonly GenericNameContext Empty = new([], []);
}

/// <summary>
/// Decodes IL metadata signatures into readable type-name strings for the
/// <see cref="WinMdParser"/>. AOT-safe: pure metadata reads, no reflection.
/// </summary>
internal sealed class SimpleTypeProvider : ISignatureTypeProvider<string, object?>, IConstructedTypeProvider<string>, ISZArrayTypeProvider<string>, ISimpleTypeProvider<string>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        return typeCode switch
        {
            PrimitiveTypeCode.Boolean => "Boolean",
            PrimitiveTypeCode.Byte => "Byte",
            PrimitiveTypeCode.SByte => "SByte",
            PrimitiveTypeCode.Char => "Char",
            PrimitiveTypeCode.Int16 => "Int16",
            PrimitiveTypeCode.UInt16 => "UInt16",
            PrimitiveTypeCode.Int32 => "Int32",
            PrimitiveTypeCode.UInt32 => "UInt32",
            PrimitiveTypeCode.Int64 => "Int64",
            PrimitiveTypeCode.UInt64 => "UInt64",
            PrimitiveTypeCode.Single => "Single",
            PrimitiveTypeCode.Double => "Double",
            PrimitiveTypeCode.String => "String",
            PrimitiveTypeCode.Object => "Object",
            PrimitiveTypeCode.Void => "void",
            PrimitiveTypeCode.IntPtr => "IntPtr",
            PrimitiveTypeCode.UIntPtr => "UIntPtr",
            PrimitiveTypeCode.TypedReference => "TypedReference",
            _ => typeCode.ToString(),
        };
    }

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        WinMdParser.BuildFullTypeName(reader, reader.GetTypeDefinition(handle));

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        BuildReferenceName(reader, handle, depth: 0);

    /// <summary>
    /// Names a referenced type the way <see cref="WinMdParser.BuildFullTypeName"/> names a
    /// defined one. A reference to a nested type carries only its own simple name and
    /// resolves its parent through <see cref="TypeReference.ResolutionScope"/>, so reading
    /// its namespace alone renders a field or parameter as a bare <c>Inner</c> — a name
    /// that matches no indexed type, or matches an arbitrary same-named one.
    /// </summary>
    internal static string BuildReferenceName(MetadataReader reader, TypeReferenceHandle handle, int depth)
    {
        TypeReference typeReference = reader.GetTypeReference(handle);
        string name = reader.GetString(typeReference.Name);
        if (depth < MaxNestingDepth && typeReference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            string outer = BuildReferenceName(reader, (TypeReferenceHandle)typeReference.ResolutionScope, depth + 1);
            return outer + "." + name;
        }
        string ns = reader.GetString(typeReference.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    /// <summary>Bounds the resolution-scope walk against malformed or circular metadata.</summary>
    private const int MaxNestingDepth = 32;

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

    public string GetByReferenceType(string elementType) => "ref " + elementType;

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        // A nested generic is named 'Dictionary`2.KeyCollection', so truncating at the
        // first suffix would drop everything after it; the arguments belong to the
        // segment that declares them. See WinMdParser.ApplyGenericArguments.
        WinMdParser.ApplyGenericArguments(genericType, typeArguments, "<", ", ", ">");

    public string GetGenericMethodParameter(object? genericContext, int index) =>
            genericContext is GenericNameContext ctx && index < ctx.MethodParameters.Length
                ? ctx.MethodParameters[index]
                : $"TMethod{index}";

        public string GetGenericTypeParameter(object? genericContext, int index) =>
            genericContext is GenericNameContext ctx && index < ctx.TypeParameters.Length
                ? ctx.TypeParameters[index]
                : $"T{index}";

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}

/// <summary>
/// Decodes IL metadata signatures into the type names used by XML documentation IDs,
/// so a parsed member can be looked up in a compiler-generated documentation file.
/// <para>
/// This is deliberately separate from <see cref="SimpleTypeProvider"/>, which renders
/// names for a human to read. The two formats disagree on nearly every primitive
/// (<c>System.String</c> versus <c>String</c>), on by-reference parameters (a trailing
/// <c>@</c> versus a leading keyword), and on generics (<c>{T}</c> versus
/// <c>&lt;T&gt;</c>), so a key built from display names misses any method that takes a
/// primitive — which is most of them.
/// </para>
/// </summary>
internal sealed class DocIdTypeProvider : ISignatureTypeProvider<string, object?>, IConstructedTypeProvider<string>, ISZArrayTypeProvider<string>, ISimpleTypeProvider<string>
{
    public static DocIdTypeProvider Instance { get; } = new DocIdTypeProvider();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        return typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.Void => "System.Void",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            _ => typeCode.ToString(),
        };
    }

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        WinMdParser.BuildFullTypeName(reader, reader.GetTypeDefinition(handle));

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        SimpleTypeProvider.BuildReferenceName(reader, handle, depth: 0);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    // A doc ID spells out each dimension's lower bound, so rank 2 is "[0:,0:]".
    public string GetArrayType(string elementType, ArrayShape shape) =>
        elementType + "[" + string.Join(",", Enumerable.Repeat("0:", shape.Rank)) + "]";

    public string GetByReferenceType(string elementType) => elementType + "@";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        // A doc ID spells a nested generic as 'Dictionary{TKey,TValue}.KeyCollection', so
        // the arguments attach to the segment that declares them rather than replacing
        // everything after the first arity suffix.
        WinMdParser.ApplyGenericArguments(genericType, typeArguments, "{", ",", "}");

    public string GetGenericMethodParameter(object? genericContext, int index) => "``" + index.ToString(CultureInfo.InvariantCulture);

    public string GetGenericTypeParameter(object? genericContext, int index) => "`" + index.ToString(CultureInfo.InvariantCulture);

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetFunctionPointerType(MethodSignature<string> signature) => "System.IntPtr";

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}

/// <summary>
/// Minimal custom-attribute decoder used to pull the message out of
/// <c>[Deprecated]</c>/<c>[Obsolete]</c> attributes.
/// </summary>
internal sealed class CustomAttributeTypeProvider : ICustomAttributeTypeProvider<object?>
{
    public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
    public object? GetSystemType() => null;
    public object? GetSZArrayType(object? elementType) => null;
    public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => null;
    public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => null;
    public object? GetTypeFromSerializedName(string name) => null;
    public PrimitiveTypeCode GetUnderlyingEnumType(object? type) => PrimitiveTypeCode.Int32;
    public bool IsSystemType(object? type) => false;
}
