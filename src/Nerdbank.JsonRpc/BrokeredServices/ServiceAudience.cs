// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Identifies the clients permitted to access a service.</summary>
/// <remarks>A broker view requires every flag in its filter. PublicSdk classifies a service; combine it with a location flag.</remarks>
[Flags]
public enum ServiceAudience
{
	/// <summary>The service is unavailable. As a view filter, imposes no audience requirements.</summary>
	None = 0,

	/// <summary>The service is accessible within its process.</summary>
	Process = 0x1,

	/// <summary>The service is accessible within its process and from other processes on the same machine and user account.</summary>
	Local = Process | 0x2,

	/// <summary>The service is part of the public SDK. Combine with a location to expose it.</summary>
	PublicSdk = 0x10000000,
}
