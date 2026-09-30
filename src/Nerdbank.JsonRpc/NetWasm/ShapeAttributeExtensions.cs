// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if !NETWASM
using System.Reflection;
#endif

namespace Nerdbank.JsonRpc;

/// <summary>Reads attributes declared on the type that a shape describes.</summary>
internal static class ShapeAttributeExtensions
{
	/// <summary>Gets an attribute applied to the shaped type.</summary>
	/// <typeparam name="TAttribute">The attribute type.</typeparam>
	/// <param name="shape">The type shape.</param>
	/// <returns>The attribute, if present.</returns>
	internal static TAttribute? GetTypeAttribute<TAttribute>(this ITypeShape shape)
		where TAttribute : Attribute
#if NETWASM
		// NetWasm has no reflection metadata; PolyType's source generator supplies the attributes.
		=> shape.AttributeProvider.GetCustomAttribute<TAttribute>(inherit: false);
#else
		=> shape.Type.GetCustomAttribute<TAttribute>();
#endif

	/// <summary>Gets all attributes of a type applied to the shaped type.</summary>
	/// <typeparam name="TAttribute">The attribute type.</typeparam>
	/// <param name="shape">The type shape.</param>
	/// <returns>The attributes.</returns>
	internal static IEnumerable<TAttribute> GetTypeAttributes<TAttribute>(this ITypeShape shape)
		where TAttribute : Attribute
#if NETWASM
		=> shape.AttributeProvider.GetCustomAttributes<TAttribute>(inherit: false);
#else
		=> shape.Type.GetCustomAttributes<TAttribute>();
#endif
}
