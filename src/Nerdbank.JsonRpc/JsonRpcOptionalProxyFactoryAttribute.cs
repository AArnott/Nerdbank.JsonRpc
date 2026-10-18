// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;

namespace Nerdbank.JsonRpc;

/// <summary>
/// A base class for source-generated attributes that create a client proxy for an RPC-marshalable interface
/// which also implements a particular set of optional interfaces.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class JsonRpcOptionalProxyFactoryAttribute : Attribute
{
	/// <summary>Initializes a new instance of the <see cref="JsonRpcOptionalProxyFactoryAttribute"/> class.</summary>
	/// <param name="interfaceIds">The sorted optional interface IDs implemented by the proxy.</param>
	protected JsonRpcOptionalProxyFactoryAttribute(params int[] interfaceIds)
	{
		this.InterfaceIds = interfaceIds;
	}

	/// <summary>Gets the sorted optional interface IDs implemented by the proxy.</summary>
	public IReadOnlyList<int> InterfaceIds { get; }

	/// <summary>
	/// Creates a new instance of the generated proxy.
	/// </summary>
	/// <param name="client">The client the proxy sends requests through.</param>
	/// <param name="options">Options controlling the proxy's behavior.</param>
	/// <returns>The new proxy, which implements the interface this attribute is applied to and the optional interfaces identified by <see cref="InterfaceIds"/>.</returns>
	public abstract object CreateProxy(IJsonRpcClient client, JsonRpcProxyOptions options);
}
