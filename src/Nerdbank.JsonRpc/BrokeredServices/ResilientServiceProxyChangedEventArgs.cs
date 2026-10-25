// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Reports the result of a backing-service refresh.</summary>
/// <param name="kind">The observed service transition.</param>
public sealed class ResilientServiceProxyChangedEventArgs(ResilientServiceProxyChangeKind kind) : EventArgs
{
	/// <summary>Gets the transition kind.</summary>
	public ResilientServiceProxyChangeKind Kind { get; } = kind;
}
