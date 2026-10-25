// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Indicates that a stable proxy currently has no available backing service.</summary>
public sealed class BrokeredServiceUnavailableException : InvalidOperationException
{
	/// <summary>Initializes a new instance of the <see cref="BrokeredServiceUnavailableException"/> class.</summary>
	/// <param name="moniker">The unavailable service.</param>
	public BrokeredServiceUnavailableException(ServiceMoniker moniker)
		: base($"Brokered service '{moniker}' is temporarily unavailable.")
	{
	}
}
