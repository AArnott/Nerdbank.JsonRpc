// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Loads service implementation code and proffers its factory on demand.</summary>
/// <param name="moniker">The requested service identity.</param>
/// <param name="container">The container to which the implementation should be proffered.</param>
/// <returns>A task that completes when loading and proffering have finished.</returns>
/// <remarks>
/// Loading is shared by concurrent requests and is not canceled when an individual request is canceled.
/// Proffer through the supplied container, which rejects proffers for this moniker after its registration is removed or replaced.
/// </remarks>
public delegate ValueTask BrokeredServiceLoader(ServiceMoniker moniker, IBrokeredServiceContainer container);

/// <summary>Declares a service identity, exposure policy, supported optional contracts, and optional loader.</summary>
public sealed class ServiceRegistration
{
	/// <summary>Initializes a new instance of the <see cref="ServiceRegistration"/> class.</summary>
	/// <param name="moniker">The service identity.</param>
	/// <param name="loadAsync">Loads the implementation and proffers its factory, or null for externally proffered services.</param>
	/// <param name="audience">The permitted clients, defaulting to same-machine access.</param>
	/// <param name="additionalInterfaceIds">The stable wire identifiers of optional contracts supported by every activation of this registration.</param>
	/// <remarks>Identifiers are case-sensitive and resolved against the proffered server descriptor. No contract types or shapes are required at registration time.</remarks>
	public ServiceRegistration(ServiceMoniker moniker, BrokeredServiceLoader? loadAsync = null, ServiceAudience audience = ServiceAudience.Local, IReadOnlyList<string>? additionalInterfaceIds = null)
	{
		this.Moniker = moniker ?? throw new ArgumentNullException(nameof(moniker));
		this.LoadAsync = loadAsync;
		ServiceAudienceFilter.Validate(audience);
		this.Audience = audience;
		string[] ids = additionalInterfaceIds?.ToArray() ?? [];
		if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
		{
			throw new ArgumentException("Additional contract identifiers must be non-empty and unique.", nameof(additionalInterfaceIds));
		}

		this.AdditionalInterfaceIds = Array.AsReadOnly(ids);
	}

	/// <summary>Gets the service identity, independent of any implementation or contract assembly.</summary>
	public ServiceMoniker Moniker { get; }

	/// <summary>Gets the permitted clients, enforced before implementation loading.</summary>
	public ServiceAudience Audience { get; }

	/// <summary>Gets the supported optional contracts' stable wire identifiers, independently of contract assemblies.</summary>
	/// <remarks>These ordinal, case-sensitive strings match <see cref="ServiceInterface.Id"/> and the capability identifiers sent to clients.</remarks>
	public IReadOnlyList<string> AdditionalInterfaceIds { get; }

	/// <summary>Gets the optional callback that loads implementation code and proffers a factory.</summary>
	/// <remarks>The callback runs at most once per registration, including failed attempts. Removing and re-registering creates a new loading lifetime.</remarks>
	public BrokeredServiceLoader? LoadAsync { get; }
}
