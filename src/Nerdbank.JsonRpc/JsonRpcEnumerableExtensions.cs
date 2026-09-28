// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Extension methods that control how an <see cref="IAsyncEnumerable{T}"/> is transmitted over a JSON-RPC connection.
/// </summary>
public static class JsonRpcEnumerableExtensions
{
	/// <summary>
	/// Applies JSON-RPC transmission settings to a sequence that is about to be sent to a remote party.
	/// </summary>
	/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
	/// <param name="enumerable">The sequence to decorate.</param>
	/// <param name="settings">The settings to apply.</param>
	/// <returns>A decorated sequence to use as the RPC argument or return value.</returns>
	/// <remarks>
	/// Settings are only honored by the party that <em>sends</em> the sequence, and are ignored if applied by the receiver.
	/// The returned sequence behaves identically to <paramref name="enumerable"/> when enumerated locally.
	/// </remarks>
	/// <example>
	/// <code><![CDATA[
	/// public IAsyncEnumerable<int> GetNumbersAsync(CancellationToken cancellationToken)
	///     => this.GetNumbersCoreAsync(cancellationToken)
	///            .WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = 10 });
	/// ]]></code>
	/// </example>
#pragma warning disable VSTHRD200 // This method returns a sequence, not an awaitable.
	public static IAsyncEnumerable<T> WithJsonRpcSettings<T>(this IAsyncEnumerable<T> enumerable, JsonRpcEnumerableSettings settings)
	{
		Requires.NotNull(enumerable);
		Requires.NotNull(settings);
		return new RpcEnumerable<T>(enumerable, settings);
	}

	/// <summary>
	/// Produces the first several values of a sequence immediately so they can be included in the very message
	/// that carries the sequence to the remote party, sparing the receiver a round-trip.
	/// </summary>
	/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
	/// <param name="enumerable">The sequence to prefetch values from.</param>
	/// <param name="count">The maximum number of values to produce up front.</param>
	/// <param name="cancellationToken">A token to cancel value production.</param>
	/// <returns>A decorated sequence carrying the prefetched values.</returns>
	/// <remarks>
	/// Use this for a sequence passed as an RPC <em>argument</em> or nested within an object graph.
	/// For a sequence returned directly from an RPC method, prefer
	/// <see cref="JsonRpcEnumerableSettings.Prefetch"/> so the RPC method itself need not be asynchronous.
	/// </remarks>
	public static async ValueTask<IAsyncEnumerable<T>> WithPrefetchAsync<T>(this IAsyncEnumerable<T> enumerable, int count, CancellationToken cancellationToken = default)
	{
		Requires.NotNull(enumerable);
		Requires.Range(count >= 0, nameof(count));

		RpcEnumerable<T> decorated = enumerable as RpcEnumerable<T> ?? new RpcEnumerable<T>(enumerable, JsonRpcEnumerableSettings.Default);
		if (count > 0)
		{
			await decorated.PrefetchAsync(count, cancellationToken).ConfigureAwait(false);
		}

		return decorated;
	}

	/// <summary>
	/// Exposes an existing collection as an <see cref="IAsyncEnumerable{T}"/> so that transmitting it over
	/// JSON-RPC streams the values on demand instead of sending the whole collection in one message.
	/// </summary>
	/// <typeparam name="T">The type of value in the collection.</typeparam>
	/// <param name="enumerable">The collection to expose.</param>
	/// <returns>An async sequence over <paramref name="enumerable"/>.</returns>
	public static IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IEnumerable<T> enumerable)
	{
		Requires.NotNull(enumerable);
		return new SyncAsAsyncEnumerable<T>(enumerable);
	}
#pragma warning restore VSTHRD200

	/// <summary>Adapts a synchronous sequence to <see cref="IAsyncEnumerable{T}"/>.</summary>
	/// <typeparam name="T">The type of value in the sequence.</typeparam>
	/// <param name="inner">The synchronous sequence.</param>
	private sealed class SyncAsAsyncEnumerable<T>(IEnumerable<T> inner) : IAsyncEnumerable<T>
	{
		/// <inheritdoc/>
		public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
			=> new Enumerator(inner.GetEnumerator(), cancellationToken);

		/// <summary>Walks a synchronous enumerator through the asynchronous enumerator contract.</summary>
		private sealed class Enumerator(IEnumerator<T> inner, CancellationToken cancellationToken) : IAsyncEnumerator<T>
		{
			/// <inheritdoc/>
			public T Current => inner.Current;

			/// <inheritdoc/>
			public ValueTask DisposeAsync()
			{
				inner.Dispose();
				return default;
			}

			/// <inheritdoc/>
			public ValueTask<bool> MoveNextAsync()
			{
				cancellationToken.ThrowIfCancellationRequested();
				return new ValueTask<bool>(inner.MoveNext());
			}
		}
	}
}
