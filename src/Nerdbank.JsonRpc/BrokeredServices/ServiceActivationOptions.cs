// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Globalization;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Describes one service activation. This data does not authenticate or authorize a caller.</summary>
public readonly record struct ServiceActivationOptions
{
	/// <summary>Gets application-defined activation arguments.</summary>
	public IReadOnlyDictionary<string, string>? ActivationArguments { get; init; }

	/// <summary>Gets the culture requested for activation.</summary>
	public CultureInfo? ClientCulture { get; init; }

	/// <summary>Gets the UI culture requested for activation.</summary>
	public CultureInfo? ClientUICulture { get; init; }

	/// <summary>Gets the local callback target. This value is not serialized.</summary>
	public object? ClientRpcTarget { get; init; }

	/// <summary>Gets a local setup callback invoked before incoming service dispatch starts.</summary>
	/// <remarks>Subscribe to service events here to avoid losing early notifications. This callback is not serialized.</remarks>
	public Action<object>? ConfigureProxy { get; init; }
}
