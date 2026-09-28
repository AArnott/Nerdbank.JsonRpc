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

	/// <summary>
	/// Issues a JSON-RPC request whose result is an <see cref="IAsyncEnumerable{T}"/> and exposes the sequence
	/// without waiting for the response.
	/// </summary>
	/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
	/// <param name="client">The client to send the request over.</param>
	/// <param name="method">The remote method name.</param>
	/// <param name="arguments">The encoded arguments.</param>
	/// <param name="resultShape">The shape of the <see cref="IAsyncEnumerable{T}"/> result type.</param>
	/// <param name="cancellationToken">A token to cancel the request and the enumeration.</param>
	/// <returns>A sequence that may be enumerated exactly once.</returns>
	/// <remarks>
	/// This method is intended for use by generated proxies. The request is sent immediately so that it
	/// participates correctly in a <see cref="JsonRpcBatch"/>; only the response is awaited lazily.
	/// </remarks>
	public static IAsyncEnumerable<T> RequestEnumerable<T>(IJsonRpcClient client, string method, JsonRpcValue arguments, ITypeShape<IAsyncEnumerable<T>> resultShape, CancellationToken cancellationToken)
	{
		Requires.NotNull(client);
		return new DeferredEnumerable<T>(client.RequestAsync(method, arguments, resultShape, cancellationToken));
	}
#pragma warning restore VSTHRD200

	/// <summary>A sequence whose values come from an RPC response that has not necessarily arrived yet.</summary>
	/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
	/// <param name="response">The pending response carrying the sequence.</param>
	private sealed class DeferredEnumerable<T>(ValueTask<IAsyncEnumerable<T>> response) : IAsyncEnumerable<T>
	{
		private readonly Task<IAsyncEnumerable<T>> response = response.AsTask();
		private bool enumeratorAcquired;

		/// <inheritdoc/>
		public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
		{
			if (this.enumeratorAcquired)
			{
				throw new InvalidOperationException("A remoted IAsyncEnumerable<T> may only be enumerated once.");
			}

			this.enumeratorAcquired = true;
			return new Enumerator(this.response, cancellationToken);
		}

		/// <summary>Awaits the response on the first move, then forwards to the real enumerator.</summary>
		/// <param name="response">The pending response carrying the sequence.</param>
		/// <param name="cancellationToken">A token to cancel the enumeration.</param>
		private sealed class Enumerator(Task<IAsyncEnumerable<T>> response, CancellationToken cancellationToken) : IAsyncEnumerator<T>
		{
			private IAsyncEnumerator<T>? inner;

			/// <inheritdoc/>
			public T Current => this.inner is null ? default! : this.inner.Current;

			/// <inheritdoc/>
			public ValueTask DisposeAsync() => this.inner?.DisposeAsync() ?? default;

			/// <inheritdoc/>
			public async ValueTask<bool> MoveNextAsync()
			{
				if (this.inner is null)
				{
#pragma warning disable VSTHRD003 // The task represents the remote response to a request this object owns.
					IAsyncEnumerable<T> sequence = await response.ConfigureAwait(false);
#pragma warning restore VSTHRD003
					this.inner = sequence.GetAsyncEnumerator(cancellationToken);
				}

				return await this.inner.MoveNextAsync().ConfigureAwait(false);
			}
		}
	}

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
