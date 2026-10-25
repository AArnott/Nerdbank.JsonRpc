// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Describes services whose availability may have changed.</summary>
[GenerateShape]
public sealed partial class BrokeredServicesChangedEventArgs : EventArgs
{
	/// <summary>Initializes a new instance of the <see cref="BrokeredServicesChangedEventArgs"/> class.</summary>
	/// <param name="impactedServices">The affected service identities.</param>
	/// <param name="otherServicesImpacted">Whether services not listed may also have changed.</param>
	public BrokeredServicesChangedEventArgs(ImmutableHashSet<ServiceMoniker> impactedServices, bool otherServicesImpacted = false)
	{
		this.ImpactedServices = impactedServices ?? throw new ArgumentNullException(nameof(impactedServices));
		this.OtherServicesImpacted = otherServicesImpacted;
	}

	/// <summary>Gets the affected services.</summary>
	public ImmutableHashSet<ServiceMoniker> ImpactedServices { get; }

	/// <summary>Gets a value indicating whether unlisted services may also be affected.</summary>
	public bool OtherServicesImpacted { get; }
}
