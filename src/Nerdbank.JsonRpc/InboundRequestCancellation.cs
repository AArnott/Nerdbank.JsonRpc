// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>
/// The cancellation source for one inbound request.
/// A prefetched async iterator keeps a lease so the source outlives the request that created it
/// and can still be canceled when the connection drops.
/// </summary>
internal sealed class InboundRequestCancellation
{
	private readonly CancellationTokenSource source = new();
	private int remaining = 1;

	/// <summary>Gets the source canceled when the request or a surviving iterator is canceled.</summary>
	internal CancellationTokenSource Source => this.source;

	/// <summary>Gets the token handed to the target method.</summary>
	internal CancellationToken Token => this.source.Token;

	/// <summary>Holds the source open until a matching <see cref="Release"/>.</summary>
	internal void Retain() => Interlocked.Increment(ref this.remaining);

	/// <summary>Drops one lease and disposes the source when none remain.</summary>
	internal void Release()
	{
		if (Interlocked.Decrement(ref this.remaining) == 0)
		{
			this.source.Dispose();
		}
	}
}
