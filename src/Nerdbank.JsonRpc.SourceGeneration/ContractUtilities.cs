// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;

namespace Nerdbank.JsonRpc.SourceGeneration;

/// <summary>Shared semantic rules for RPC contracts.</summary>
internal static class ContractUtilities
{
	/// <summary>Gets whether a type has the specified attribute.</summary>
	/// <param name="symbol">The type to inspect.</param>
	/// <param name="attributeSymbol">The attribute type.</param>
	/// <returns>Whether the attribute is present.</returns>
	internal static bool HasAttribute(INamedTypeSymbol symbol, INamedTypeSymbol? attributeSymbol)
		=> attributeSymbol is not null && symbol.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeSymbol));

	/// <summary>Gets an explicitly assigned wire name, bypassing convention-based transforms.</summary>
	/// <param name="method">The method to inspect.</param>
	/// <param name="methodShapeAttribute">The method shape attribute type.</param>
	/// <returns>The explicit name, or <see langword="null"/>.</returns>
	internal static string? GetExplicitRpcName(IMethodSymbol method, INamedTypeSymbol? methodShapeAttribute)
		=> method.GetAttributes().FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, methodShapeAttribute))?
			.NamedArguments.FirstOrDefault(static argument => argument.Key == KnownApis.MethodShapeName).Value.Value as string;

	/// <summary>Gets the default wire name for a CLR method name.</summary>
	/// <param name="name">The CLR method name.</param>
	/// <returns>The camel-cased name without a trailing Async suffix.</returns>
	internal static string GetDefaultRpcName(string name)
	{
		const string suffix = "Async";
		if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
		{
			name = name.Substring(0, name.Length - suffix.Length);
		}

		return name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
	}

	/// <summary>Gets whether a parameter type is a cancellation token.</summary>
	/// <param name="type">The type to inspect.</param>
	/// <returns>Whether the type is a cancellation token.</returns>
	internal static bool IsCancellationToken(ITypeSymbol type)
		=> type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == KnownApis.CancellationToken;

	/// <summary>Gets whether a type contains an RPC-marshalable interface.</summary>
	/// <param name="type">The type to inspect.</param>
	/// <param name="marshalableAttribute">The marshalable attribute type.</param>
	/// <returns>Whether the type contains a marshalable interface.</returns>
	internal static bool ContainsRpcMarshalableInterface(ITypeSymbol type, INamedTypeSymbol? marshalableAttribute)
	{
		if (type is IArrayTypeSymbol arrayType)
		{
			return ContainsRpcMarshalableInterface(arrayType.ElementType, marshalableAttribute);
		}

		return type is INamedTypeSymbol namedType
			&& ((namedType.TypeKind == TypeKind.Interface && HasAttribute(namedType, marshalableAttribute))
				|| namedType.TypeArguments.Any(argument => ContainsRpcMarshalableInterface(argument, marshalableAttribute)));
	}

	/// <summary>Gets the reason an event cannot be represented as RPC notifications, if any.</summary>
	/// <param name="event">The event to inspect.</param>
	/// <param name="marshalableAttribute">The marshalable attribute type.</param>
	/// <returns>The unsupported event reason, or <see langword="null"/>.</returns>
	internal static string? GetUnsupportedEventReason(IEventSymbol @event, INamedTypeSymbol? marshalableAttribute)
	{
		if (@event.IsStatic || @event.Type is not INamedTypeSymbol { DelegateInvokeMethod: { ReturnsVoid: true } invoke }
			|| invoke.Parameters.Any(static parameter => parameter.RefKind != RefKind.None || parameter.Type.IsRefLikeType || parameter.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer))
		{
			return "proxy events must be instance events with synchronous, void-returning delegates and by-value, non-ref-like parameters";
		}

		return invoke.Parameters.Any(parameter => ContainsRpcMarshalableInterface(parameter.Type, marshalableAttribute))
			? "proxy events cannot accept RPC-marshalable interface parameters"
			: null;
	}

	/// <summary>Gets the reason a method cannot be represented by an RPC proxy, if any.</summary>
	/// <param name="method">The method to inspect.</param>
	/// <param name="isMarshalable">Whether the contract is marshalable.</param>
	/// <param name="marshalableAttribute">The marshalable attribute type.</param>
	/// <returns>The unsupported signature reason, or <see langword="null"/>.</returns>
	internal static string? GetUnsupportedSignatureReason(IMethodSymbol method, bool isMarshalable, INamedTypeSymbol? marshalableAttribute)
	{
		if (method.ReturnsVoid && method.Parameters.Any(parameter => ContainsRpcMarshalableInterface(parameter.Type, marshalableAttribute)))
		{
			return "notification methods cannot accept RPC-marshalable interface parameters";
		}

		if (method.IsGenericMethod)
		{
			return "generic methods are not supported yet";
		}

		if (method.IsStatic)
		{
			return "static methods are not supported";
		}

		bool isDisposeMethod = method.Name == KnownApis.Dispose && method.ContainingType.ToDisplayString() == KnownApis.IDisposable && method.Parameters.Length == 0;
		if (isMarshalable && !isDisposeMethod && method.ReturnsVoid)
		{
			return "marshalable interface methods must return Task or ValueTask";
		}

		foreach (IParameterSymbol parameter in method.Parameters)
		{
			if (parameter.RefKind is not RefKind.None)
			{
				return "ref, out, and in parameters are not supported yet";
			}

			if (parameter.Type.IsRefLikeType || parameter.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
			{
				return "ref-like and pointer parameters are not supported";
			}

			if (IsCancellationToken(parameter.Type) && parameter.Ordinal != method.Parameters.Length - 1)
			{
				return "CancellationToken parameters must appear last";
			}

			if (parameter.IsParams)
			{
				return "params parameters are not supported yet";
			}
		}

		string returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
		bool supported = returnType is KnownApis.Void or KnownApis.Task or KnownApis.ValueTask
			|| (method.ReturnType is INamedTypeSymbol { IsGenericType: true } namedReturnType
				&& namedReturnType.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) is KnownApis.TaskOfT or KnownApis.ValueTaskOfT or KnownApis.IAsyncEnumerableOfT);
		return !supported || method.ReturnsByRef || method.ReturnsByRefReadonly
			? $"return type '{method.ReturnType.ToDisplayString()}' is not supported yet"
			: null;
	}
}
