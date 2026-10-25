// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Validates and matches audience requirements.</summary>
internal static class ServiceAudienceFilter
{
	/// <summary>Rejects audience bits not supported by this implementation.</summary>
	/// <param name="audience">The audience or filter.</param>
	internal static void Validate(ServiceAudience audience)
	{
		if ((audience & ~(ServiceAudience.Local | ServiceAudience.PublicSdk)) != 0)
		{
			throw new ArgumentOutOfRangeException(nameof(audience));
		}
	}

	/// <summary>Checks location eligibility and all required flags.</summary>
	/// <param name="audience">The service's audience.</param>
	/// <param name="required">The view's required flags.</param>
	/// <returns>Whether the service is visible.</returns>
	internal static bool Matches(ServiceAudience audience, ServiceAudience required)
		=> (audience & ServiceAudience.Local) != 0 && (audience & required) == required;
}
