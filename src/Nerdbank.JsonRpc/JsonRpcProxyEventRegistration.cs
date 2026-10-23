// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using Microsoft;

namespace Nerdbank.JsonRpc;

/// <summary>Stores reusable argument binding and dispatch metadata for a generated proxy event.</summary>
/// <remarks>Intended for generated code. Cache one instance per event shape; it does not retain a proxy, connection, or serializer.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class JsonRpcProxyEventRegistration
{
	/// <summary>Initializes a new instance of the <see cref="JsonRpcProxyEventRegistration"/> class.</summary>
	/// <param name="handlerShape">The synchronous, void-returning event handler shape.</param>
	public JsonRpcProxyEventRegistration(ITypeShape handlerShape)
	{
		Requires.NotNull(handlerShape);
		this.Invoker = handlerShape.Accept(ProxyEventVisitor.Instance) as MethodInvoker
			?? throw new NotSupportedException($"The shape for '{handlerShape.Type}' is not an event handler delegate.");
	}

	/// <summary>Gets the shared dispatch callback, which binds its target and serializer from each request.</summary>
	internal MethodInvoker Invoker { get; }
}
