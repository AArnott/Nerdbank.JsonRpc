// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Provides resilient service acquisition for service brokers.</summary>
public static class ServiceBrokerExtensions
{
	/// <summary>Creates and initializes a stable proxy, including when the service is currently unavailable.</summary>
	/// <typeparam name="T">The primary service interface.</typeparam>
	/// <param name="broker">The broker to observe.</param>
	/// <param name="descriptor">The typed contract and its complete allowed additional-interface universe.</param>
	/// <param name="options">Options for each activation.</param>
	/// <param name="logger">The lifecycle logger.</param>
	/// <param name="cancellationToken">Cancels initial acquisition.</param>
	/// <returns>The disposable owner of the stable generated proxy.</returns>
	public static ValueTask<ResilientServiceProxy<T>> GetResilientProxyAsync<T>(this IServiceBroker broker, IJsonRpcDescriptor<T> descriptor, ServiceActivationOptions options = default, ILogger<ServiceProxyContext>? logger = null, CancellationToken cancellationToken = default)
		where T : class => ResilientServiceProxy<T>.CreateAsync(broker, descriptor, options, logger, cancellationToken);
}
