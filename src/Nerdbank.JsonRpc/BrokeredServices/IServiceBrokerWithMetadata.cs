// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Provides non-activating registration metadata to relay brokers.</summary>
public interface IServiceBrokerWithMetadata : IServiceBroker
{
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
