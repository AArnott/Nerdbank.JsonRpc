// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Provides lifecycle and availability information for a stable service proxy.</summary>
public interface IResilientServiceProxy : IDisposable, IAsyncDisposable
{
	/// <summary>Occurs after a refresh gains, loses or replaces the backing service.</summary>
	event EventHandler<ResilientServiceProxyChangedEventArgs>? BackingServiceChanged;

	/// <summary>Gets a value indicating whether a backing service is currently available.</summary>
	bool IsAvailable { get; }
}
