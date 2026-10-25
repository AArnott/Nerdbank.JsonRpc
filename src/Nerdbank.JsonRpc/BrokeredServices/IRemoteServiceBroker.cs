// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Exposes discovery and service-channel allocation over RPC.</summary>
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IRemoteServiceBroker
{
	/// <summary>Occurs when queried services may have changed.</summary>
	event EventHandler<BrokeredServicesChangedEventArgs> AvailabilityChanged;

	/// <summary>Negotiates a broker protocol version and service-channel transport.</summary>
	/// <param name="connections">The client's supported transports.</param>
	/// <param name="cancellationToken">Cancels negotiation.</param>
	/// <returns>The selected protocol version. Unsupported transport sets fail explicitly.</returns>
	Task<int> HandshakeAsync(RemoteServiceConnections connections, CancellationToken cancellationToken = default);

	/// <summary>Queries registration capabilities without activating a service.</summary>
	/// <param name="moniker">The requested service.</param>
	/// <param name="cancellationToken">Cancels discovery.</param>
	/// <returns>Stable capability identifiers, or null when unavailable.</returns>
	Task<IReadOnlyList<string>?> GetCapabilitiesAsync(ServiceMoniker moniker, CancellationToken cancellationToken = default);

	/// <summary>Allocates a service endpoint and returns its registration's actual capabilities.</summary>
	/// <param name="moniker">The requested service.</param>
	/// <param name="options">Transferable activation arguments and cultures.</param>
	/// <param name="cancellationToken">Cancels allocation.</param>
	/// <returns>The connection instructions, or null if unavailable.</returns>
	Task<RemoteServiceConnectionInfo?> RequestServiceChannelAsync(ServiceMoniker moniker, RemoteServiceActivationOptions options = default, CancellationToken cancellationToken = default);

	/// <summary>Releases an endpoint that the caller cannot or will not connect to.</summary>
	/// <param name="requestId">The allocated request identity.</param>
	/// <param name="cancellationToken">Cancels the operation.</param>
	/// <returns>A task tracking cleanup.</returns>
	Task CancelServiceRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
}
