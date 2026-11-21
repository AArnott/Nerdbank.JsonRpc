// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>
/// A non-generic view of an <see cref="IAsyncEnumerable{T}"/> that has been decorated with JSON-RPC transmission settings.
/// </summary>
internal interface IRpcEnumerable
{
	/// <summary>Gets the settings that tune how this sequence is transmitted.</summary>
	JsonRpcEnumerableSettings Settings { get; }

	/// <summary>
	/// Produces the number of values requested by <see cref="JsonRpcEnumerableSettings.Prefetch"/> so they can
	/// accompany the enumerable in the message that carries it.
	/// </summary>
	/// <param name="cancellationToken">A token to cancel value production.</param>
	/// <returns>A task that completes when prefetching is done.</returns>
	ValueTask PrefetchAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Uses <paramref name="cancellation"/> as the token the prefetched iterator observes.
	/// The target method was invoked with that same token, so a compiler-generated iterator does not
	/// create a second linked token that would only become canceled from inside a callback.
	/// </summary>
	/// <param name="cancellation">The request cancellation lease. The enumerable retains it while an iterator is outstanding.</param>
	void AttachRequestCancellation(InboundRequestCancellation cancellation);
}
