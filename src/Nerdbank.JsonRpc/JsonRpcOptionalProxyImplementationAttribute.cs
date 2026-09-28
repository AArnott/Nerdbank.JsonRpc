// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NET
using System.Diagnostics.CodeAnalysis;
#endif

namespace Nerdbank.JsonRpc;

/// <summary>Identifies a generated proxy implementation for one optional-interface capability set.</summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = true)]
public sealed class JsonRpcOptionalProxyImplementationAttribute : Attribute
{
	/// <summary>Initializes a new instance of the <see cref="JsonRpcOptionalProxyImplementationAttribute"/> class.</summary>
	/// <param name="proxyType">The generated proxy type.</param>
	/// <param name="interfaceIds">The sorted optional interface IDs implemented by the proxy.</param>
	public JsonRpcOptionalProxyImplementationAttribute(
#if NET
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type proxyType,
#else
		Type proxyType,
#endif
		params int[] interfaceIds)
	{
		this.ProxyType = proxyType;
		this.InterfaceIds = interfaceIds;
	}

	/// <summary>Gets the generated proxy type.</summary>
#if NET
	[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
#endif
	public Type ProxyType { get; }

	/// <summary>Gets the sorted optional interface IDs implemented by the proxy.</summary>
	public IReadOnlyList<int> InterfaceIds { get; }
}
