// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NET
using System.Diagnostics.CodeAnalysis;
#endif

namespace Nerdbank.JsonRpc;

/// <summary>
/// Identifies the generated client proxy type for an RPC contract interface.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class JsonRpcProxyImplementationAttribute : Attribute
{
	/// <summary>
	/// Initializes a new instance of the <see cref="JsonRpcProxyImplementationAttribute"/> class.
	/// </summary>
	/// <param name="proxyType">The generated proxy type for the annotated RPC contract interface.</param>
	public JsonRpcProxyImplementationAttribute(
#if NET
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type proxyType)
#else
		Type proxyType)
#endif
	{
		this.ProxyType = proxyType;
	}

	/// <summary>
	/// Gets the generated proxy type for the annotated RPC contract interface.
	/// </summary>
#if NET
	[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
#endif
	public Type ProxyType { get; }
}
