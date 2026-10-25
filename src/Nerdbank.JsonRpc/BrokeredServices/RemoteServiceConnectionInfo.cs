// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Describes an allocated, single-client service channel.</summary>
[GenerateShape]
public sealed partial record RemoteServiceConnectionInfo
{
	/// <summary>Gets the request identity used for abandonment cleanup.</summary>
	public required Guid RequestId { get; init; }

	/// <summary>Gets the same-account IPC endpoint.</summary>
	public string? PipeName { get; init; }

	/// <summary>Gets the offered channel identifier when using a multiplexed connection.</summary>
	public ulong? MultiplexingChannelId { get; init; }

	/// <summary>Gets stable identifiers of the actual implementation's additional contracts.</summary>
	public required IReadOnlyList<string> Capabilities { get; init; }
}
