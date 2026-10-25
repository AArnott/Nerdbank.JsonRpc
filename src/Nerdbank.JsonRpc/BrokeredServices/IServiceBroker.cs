// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Activates services by contract, without exposing where they are implemented.</summary>
public interface IServiceBroker
{
	/// <summary>Occurs when previously queried service availability may have changed.</summary>
	event EventHandler<BrokeredServicesChangedEventArgs>? AvailabilityChanged;

	/// <summary>Acquires a service proxy whose type is inferred from its descriptor.</summary>
	/// <typeparam name="T">The service interface.</typeparam>
	/// <param name="descriptor">The descriptor, including generated proxy support.</param>
	/// <param name="options">Options for this activation.</param>
	/// <param name="cancellationToken">Cancels this request.</param>
	/// <returns>A caller-owned proxy, or null if the service is unavailable.</returns>
	ValueTask<T?> GetProxyAsync<T>(IJsonRpcDescriptor<T> descriptor, ServiceActivationOptions options = default, CancellationToken cancellationToken = default)
		where T : class;

	/// <summary>Acquires a service's raw RPC pipe.</summary>
	/// <param name="moniker">The requested service.</param>
	/// <param name="options">Options for this activation.</param>
	/// <param name="cancellationToken">Cancels this request.</param>
	/// <returns>A caller-owned pipe, or null if the service is unavailable. Complete both ends when finished.</returns>
	ValueTask<IDuplexPipe?> GetPipeAsync(ServiceMoniker moniker, ServiceActivationOptions options = default, CancellationToken cancellationToken = default);

	/// <summary>Acquires a channel and capabilities from the same winning registration.</summary>
	/// <param name="moniker">The requested identity.</param>
	/// <param name="options">Options for the activation.</param>
	/// <param name="cancellationToken">Cancels acquisition.</param>
	/// <returns>The caller-owned connection, or null if unavailable.</returns>
	ValueTask<BrokeredServiceConnection?> GetConnectionAsync(ServiceMoniker moniker, ServiceActivationOptions options = default, CancellationToken cancellationToken = default);

	/// <summary>Gets the capabilities of the selected registration without activating it.</summary>
	/// <param name="moniker">The requested service.</param>
	/// <param name="cancellationToken">Cancels lookup.</param>
	/// <returns>Stable capability identifiers, or null when no registration exists.</returns>
	ValueTask<IReadOnlyList<string>?> GetCapabilitiesAsync(ServiceMoniker moniker, CancellationToken cancellationToken = default);
}
