// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Activates a new service instance for one client.</summary>
/// <typeparam name="T">The primary service contract.</typeparam>
/// <param name="moniker">The service identity requested by the client.</param>
/// <param name="options">The client's activation data.</param>
/// <param name="serviceBroker">The broker to use for dependencies.</param>
/// <param name="cancellationToken">Cancels activation.</param>
/// <returns>The client-owned instance, or null if unavailable.</returns>
public delegate ValueTask<T?> BrokeredServiceFactory<T>(ServiceMoniker moniker, ServiceActivationOptions options, IServiceBroker serviceBroker, CancellationToken cancellationToken)
	where T : class;
