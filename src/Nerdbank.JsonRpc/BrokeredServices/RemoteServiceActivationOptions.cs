// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Globalization;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Contains activation data that can cross a broker control connection.</summary>
public readonly record struct RemoteServiceActivationOptions
{
	/// <summary>Gets application-defined activation arguments.</summary>
	public IReadOnlyDictionary<string, string>? ActivationArguments { get; init; }

	/// <summary>Gets the requested culture name, or null to use the host's default.</summary>
	public string? ClientCulture { get; init; }

	/// <summary>Gets the requested UI culture name, or null to use the host's default.</summary>
	public string? ClientUICulture { get; init; }

	/// <summary>Copies the transferable portion of local activation options.</summary>
	/// <param name="options">The local options.</param>
	/// <returns>Options with no local targets or delegates.</returns>
	internal static RemoteServiceActivationOptions FromLocal(ServiceActivationOptions options)
		=> new()
		{
			ActivationArguments = options.ActivationArguments,
			ClientCulture = options.ClientCulture?.Name,
			ClientUICulture = options.ClientUICulture?.Name,
		};

	/// <summary>Validates culture names and reconstructs local activation options.</summary>
	/// <returns>The options for the service factory.</returns>
	internal ServiceActivationOptions ToLocal()
		=> new()
		{
			ActivationArguments = this.ActivationArguments,
			ClientCulture = this.ClientCulture is { } culture ? CultureInfo.GetCultureInfo(culture) : null,
			ClientUICulture = this.ClientUICulture is { } uiCulture ? CultureInfo.GetCultureInfo(uiCulture) : null,
		};
}
