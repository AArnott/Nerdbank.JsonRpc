// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;

namespace Nerdbank.JsonRpc;

/// <summary>Specifies IPC listening behavior.</summary>
public readonly record struct IpcServerOptions
{
	/// <summary>Gets a caller-chosen name, or null for a cryptographically unpredictable name.</summary>
	public string? Name { get; init; }

	/// <summary>Gets a value indicating whether to accept more than one client.</summary>
	public bool AllowMultipleClients { get; init; }

	/// <summary>Gets the typed server logger.</summary>
	public ILogger<IpcServer>? Logger { get; init; }
}
