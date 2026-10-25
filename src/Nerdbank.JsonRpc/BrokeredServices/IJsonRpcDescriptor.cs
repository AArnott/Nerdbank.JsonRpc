// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Describes a service's wire contract and constructs its server connection.</summary>
/// <remarks>Implementations must expose only the declared contract, not arbitrary implementation methods.</remarks>
public interface IJsonRpcDescriptor
{
	/// <summary>Gets the identity of the service.</summary>
	ServiceMoniker Moniker { get; }

	/// <summary>Gets known additional contracts and their generated shapes, not implementation capabilities.</summary>
	IReadOnlyList<ServiceInterface> AdditionalInterfaces { get; }

	/// <summary>Gets connection policies, including direct-call exception disclosure.</summary>
	JsonRpcOptions RpcOptions { get; }

	/// <summary>Constructs an unstarted connection. Successful construction transfers pipe ownership.</summary>
	/// <remarks>Implementations that initialize a transport wrapper may transfer ownership before construction completes, including on failure.</remarks>
	/// <param name="pipe">The connected transport.</param>
	/// <param name="context">Runtime infrastructure for this connection.</param>
	/// <returns>The connection, whose owner must dispose it.</returns>
	JsonRpc CreateConnection(IDuplexPipe pipe, ServiceRpcContext? context = null);

	/// <summary>Registers the primary service contract and explicitly registered additional interfaces.</summary>
	/// <remarks>This type-erased infrastructure entry point is for catalog dispatch. Use the typed descriptor's AddRpcTarget method when the contract type is known.</remarks>
	/// <param name="connection">The unstarted connection.</param>
	/// <param name="target">The service implementation.</param>
	/// <param name="additionalInterfaces">The interfaces supported by this registration.</param>
	void AddRpcTargetCore(JsonRpc connection, object target, IReadOnlyList<ServiceInterface> additionalInterfaces);

	/// <summary>Constructs a generated server-side forwarding target for asynchronous activation.</summary>
	/// <param name="context">The activation and lifetime context.</param>
	/// <param name="additionalInterfaces">The registration's supported interfaces.</param>
	/// <returns>The generated server target.</returns>
	object CreateLocalTarget(ServiceProxyContext context, IReadOnlyList<ServiceInterface> additionalInterfaces);

	/// <summary>Constructs the client's callback proxy on an unstarted server connection.</summary>
	/// <param name="connection">The server connection.</param>
	/// <returns>The callback proxy, or null if the service has no callback contract.</returns>
	object? CreateCallbackProxy(JsonRpc connection);

	/// <summary>Constructs a direct callback boundary without owning the caller's target.</summary>
	/// <param name="target">The caller-owned callback implementation.</param>
	/// <param name="logger">The direct callback logger.</param>
	/// <returns>The generated, activation-owned callback boundary.</returns>
	object CreateLocalCallback(object target, ILogger<ServiceProxyContext>? logger = null);
}
