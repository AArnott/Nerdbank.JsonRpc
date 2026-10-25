// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Defines generated construction support for a service proxy.</summary>
/// <typeparam name="T">The primary service interface.</typeparam>
public interface IJsonRpcDescriptor<T> : IJsonRpcDescriptor
	where T : class
{
	/// <summary>Registers the typed service implementation and its supported optional contracts.</summary>
	/// <param name="connection">The unstarted connection.</param>
	/// <param name="target">The service implementation.</param>
	/// <param name="additionalInterfaces">The supported optional interfaces.</param>
	void AddRpcTarget(JsonRpc connection, T target, IReadOnlyList<ServiceInterface> additionalInterfaces);

	/// <summary>Constructs a typed server-side forwarding target for asynchronous activation.</summary>
	/// <param name="context">The activation and lifetime context.</param>
	/// <param name="additionalInterfaces">The supported optional interfaces.</param>
	/// <returns>The generated server target.</returns>
	new T CreateLocalTarget(ServiceProxyContext context, IReadOnlyList<ServiceInterface> additionalInterfaces);

	/// <summary>Constructs an RPC proxy before its connection is started.</summary>
	/// <param name="connection">The connection owned by the proxy.</param>
	/// <param name="additionalInterfaces">The supported additional interfaces.</param>
	/// <param name="options">Activation options, including a callback target.</param>
	/// <returns>The generated service proxy.</returns>
	T CreateProxy(JsonRpc connection, IReadOnlyList<ServiceInterface> additionalInterfaces, ServiceActivationOptions options = default);

	/// <summary>Constructs a direct, generated service proxy with explicit ownership.</summary>
	/// <param name="context">The context that supplies a leased target and boundary policy.</param>
	/// <param name="additionalInterfaces">The supported additional interfaces.</param>
	/// <returns>The generated forwarding proxy.</returns>
	T CreateLocalProxy(ServiceProxyContext context, IReadOnlyList<ServiceInterface> additionalInterfaces);
}
