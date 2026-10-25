// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Publishes service implementations through a broker.</summary>
public interface IBrokeredServiceContainer
{
	/// <summary>Registers a service identity and optional on-demand loader without loading its implementation.</summary>
	/// <param name="registration">The service metadata.</param>
	/// <remarks>May be called concurrently with acquisitions and other catalog operations. Duplicate registrations are rejected.</remarks>
	void Register(ServiceRegistration registration);

	/// <summary>Removes a registration and its current proffer without destroying existing activations.</summary>
	/// <param name="moniker">The identity to remove.</param>
	/// <returns>Whether the service was registered.</returns>
	/// <remarks>May be called on a live container. Pending lookups cannot use the removed registration to discover a replacement, and existing activations remain client-owned.</remarks>
	bool Unregister(ServiceMoniker moniker);

	/// <summary>Publishes an implementation until its proffer lease is disposed.</summary>
	/// <typeparam name="T">The primary service contract.</typeparam>
	/// <param name="descriptor">The descriptor for an already registered service.</param>
	/// <param name="factory">The per-client activation factory.</param>
	/// <returns>A lease that removes availability without destroying existing clients.</returns>
	/// <exception cref="InvalidOperationException">The moniker is not registered or is already proffered.</exception>
	IDisposable Proffer<T>(IJsonRpcDescriptor<T> descriptor, BrokeredServiceFactory<T> factory)
		where T : class;

	/// <summary>Creates a broker view with independent availability tracking.</summary>
	/// <param name="audience">Flags every exposed service must have. None imposes no requirements, but never exposes services with no location audience.</param>
	/// <returns>The broker view.</returns>
	/// <remarks>Use Local for an RPC relay, optionally combined with PublicSdk. Process-only services cannot be acquired as pipes.</remarks>
	IServiceBroker GetServiceBroker(ServiceAudience audience = ServiceAudience.Local);
}
