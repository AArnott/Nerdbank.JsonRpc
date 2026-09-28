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
}
