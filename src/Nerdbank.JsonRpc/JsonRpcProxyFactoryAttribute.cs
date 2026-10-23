// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;

namespace Nerdbank.JsonRpc;

/// <summary>
/// A base class for source-generated attributes that create the client proxy for an RPC contract interface.
/// </summary>
/// <remarks>
/// The source generator emits a derived attribute for each generated proxy and applies it to the RPC contract interface.
/// This lets the proxy be created without constructor reflection, and works even when the proxy type is not accessible to the caller.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class JsonRpcProxyFactoryAttribute : Attribute
{
	/// <summary>Initializes a new instance of the <see cref="JsonRpcProxyFactoryAttribute"/> class for a base-only proxy.</summary>
	protected JsonRpcProxyFactoryAttribute()
		: this([])
	{
	}

	/// <summary>Initializes a new instance of the <see cref="JsonRpcProxyFactoryAttribute"/> class for an interface group.</summary>
	/// <param name="additionalInterfaces">The interfaces implemented in addition to the primary contract.</param>
	protected JsonRpcProxyFactoryAttribute(params Type[] additionalInterfaces)
	{
		this.AdditionalInterfaces = Array.AsReadOnly(additionalInterfaces);
	}

	/// <summary>Gets the interfaces implemented in addition to the primary contract.</summary>
	public IReadOnlyList<Type> AdditionalInterfaces { get; }

	/// <summary>
	/// Creates a new instance of the generated proxy.
	/// </summary>
	/// <param name="client">The client the proxy sends requests through.</param>
	/// <param name="options">Options controlling the proxy's behavior.</param>
	/// <returns>The new proxy, which implements the interface this attribute is applied to.</returns>
	public abstract object CreateProxy(IJsonRpcClient client, JsonRpcProxyOptions options);
}
