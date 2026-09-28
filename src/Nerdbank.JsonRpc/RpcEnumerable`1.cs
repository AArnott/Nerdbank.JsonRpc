// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>
/// An <see cref="IAsyncEnumerable{T}"/> that carries JSON-RPC transmission settings and, optionally,
/// values that were produced ahead of time so they can ride along in the message that carries the enumerable.
/// </summary>
/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
internal sealed class RpcEnumerable<T> : IAsyncEnumerable<T>, IRpcEnumerable
{
	private readonly IAsyncEnumerable<T> inner;
	private IAsyncEnumerator<T>? startedEnumerator;
	private List<T>? prefetchedValues;
	private bool exhausted;

	/// <summary>Initializes a new instance of the <see cref="RpcEnumerable{T}"/> class.</summary>
	/// <param name="inner">The sequence being decorated.</param>
	/// <param name="settings">The settings to apply when this sequence is transmitted over JSON-RPC.</param>
	internal RpcEnumerable(IAsyncEnumerable<T> inner, JsonRpcEnumerableSettings settings)
	{
		this.inner = inner;
		this.Settings = settings;
	}

	/// <inheritdoc/>
	public JsonRpcEnumerableSettings Settings { get; }

	/// <inheritdoc/>
	public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
	{
		if (this.startedEnumerator is null)
		{
			return this.inner.GetAsyncEnumerator(cancellationToken);
		}

		// Prefetching already advanced an enumerator, so continue from where it left off,
		// replaying any values that were fetched but never torn off for transmission.
		List<T>? pending = this.prefetchedValues;
		this.prefetchedValues = null;
		IAsyncEnumerator<T> started = this.startedEnumerator;
		return pending is { Count: > 0 } ? new PrependingEnumerator(pending, started) : started;
	}

	/// <inheritdoc/>
	public ValueTask PrefetchAsync(CancellationToken cancellationToken)
		=> this.Settings.Prefetch > 0 && this.prefetchedValues is null
			? this.PrefetchAsync(this.Settings.Prefetch, cancellationToken)
			: default;

	/// <summary>Produces up to <paramref name="count"/> values immediately so they can accompany the enumerable on the wire.</summary>
	/// <param name="count">The maximum number of values to produce.</param>
	/// <param name="cancellationToken">A token to cancel value production.</param>
	/// <returns>A task that completes when the values have been produced or the sequence has ended.</returns>
	internal async ValueTask PrefetchAsync(int count, CancellationToken cancellationToken)
	{
		this.startedEnumerator ??= this.inner.GetAsyncEnumerator(cancellationToken);
		List<T> values = this.prefetchedValues ?? new List<T>(count);
		while (values.Count < count)
		{
			if (!await this.startedEnumerator.MoveNextAsync().ConfigureAwait(false))
			{
				this.exhausted = true;
				break;
			}

			values.Add(this.startedEnumerator.Current);
		}

		this.prefetchedValues = values;
	}

	/// <summary>
	/// Removes and returns the values that were produced ahead of time, reporting whether the sequence
	/// ended while producing them.
	/// </summary>
	/// <returns>
	/// The prefetched values, and a value indicating whether the sequence is known to be complete,
	/// meaning no token needs to be sent for the consumer to ask for more.
	/// </returns>
	internal (IReadOnlyList<T> Values, bool Finished) TearOffPrefetchedElements()
	{
		IReadOnlyList<T> values = this.prefetchedValues ?? (IReadOnlyList<T>)Array.Empty<T>();
		this.prefetchedValues = null;
		return (values, this.exhausted);
	}

	/// <summary>Yields a buffered prefix before continuing with an enumerator that has already been advanced.</summary>
	private sealed class PrependingEnumerator(List<T> prefix, IAsyncEnumerator<T> inner) : IAsyncEnumerator<T>
	{
		private int index = -1;

		/// <inheritdoc/>
		public T Current => this.index >= 0 && this.index < prefix.Count ? prefix[this.index] : inner.Current;

		/// <inheritdoc/>
		public ValueTask DisposeAsync() => inner.DisposeAsync();

		/// <inheritdoc/>
		public ValueTask<bool> MoveNextAsync()
		{
			if (this.index + 1 < prefix.Count)
			{
				this.index++;
				return new ValueTask<bool>(true);
			}

			this.index = prefix.Count;
			return inner.MoveNextAsync();
		}
	}
}
