// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Describes one mutually exclusive backing-service transition.</summary>
public enum ResilientServiceProxyChangeKind
{
	/// <summary>A service became available.</summary>
	Gained,

	/// <summary>A service became unavailable.</summary>
	Lost,

	/// <summary>A previously available service was replaced.</summary>
	Replaced,
}
