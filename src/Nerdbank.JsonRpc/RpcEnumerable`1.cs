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
	private IAsyncEnumerator<T>? continuation;
	private CancellationTokenSource? enumerationCancellation;
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
		if (this.exhausted)
		{
			IReadOnlyList<T> values = this.prefetchedValues ?? (IReadOnlyList<T>)Array.Empty<T>();
			this.prefetchedValues = null;
			return values.AsAsyncEnumerable().GetAsyncEnumerator(cancellationToken);
		}

		if (this.continuation is not null)
		{
			return this.continuation;
		}

		if (this.startedEnumerator is null)
		{
			return this.inner.GetAsyncEnumerator(cancellationToken);
		}

		// Prefetch started the iterator with a token owned by this sequence. The request token that
		// was current during prefetch is disposed when that request completes, so later pulls and
		// shutdown must cancel this token instead.
		CancellationTokenRegistration registration = cancellationToken.Register(static state => CancelSource(state), this.enumerationCancellation);
		List<T>? pending = this.prefetchedValues;
		this.prefetchedValues = null;
		IAsyncEnumerator<T> started = this.startedEnumerator;
		IAsyncEnumerator<T> enumerator = pending is { Count: > 0 } ? new PrependingEnumerator(pending, started) : started;
		this.continuation = new LinkedEnumerator(enumerator, this.enumerationCancellation, registration);
		return this.continuation;
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
		if (this.exhausted)
		{
			return;
		}

		// Link the caller only for this prefetch. The completed request's token is disposed afterward and must not cancel later pulls.
		this.enumerationCancellation ??= new CancellationTokenSource();
		CancellationTokenRegistration requestRegistration = cancellationToken.Register(static state => CancelSource(state), this.enumerationCancellation);
		IAsyncEnumerator<T>? enumerator = null;
		bool keepEnumerator = false;
		try
		{
			this.startedEnumerator ??= this.inner.GetAsyncEnumerator(this.enumerationCancellation.Token);
			enumerator = this.startedEnumerator;
			List<T> values = this.prefetchedValues ?? new List<T>(count);
			while (values.Count < count)
			{
				if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
				{
					this.exhausted = true;
					break;
				}

				values.Add(enumerator.Current);
			}

			this.prefetchedValues = values;
			keepEnumerator = !this.exhausted;
		}
		finally
		{
#pragma warning disable VSTHRD103 // Unregistering a cancellation callback is synchronous.
			requestRegistration.Dispose();
#pragma warning restore VSTHRD103
			if (!keepEnumerator && enumerator is not null)
			{
				this.startedEnumerator = null;
				await enumerator.DisposeAsync().ConfigureAwait(false);
				this.enumerationCancellation?.Dispose();
				this.enumerationCancellation = null;
			}
		}
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

	/// <summary>Cancels a source that may already have been disposed with its enumerator.</summary>
	/// <param name="state">The <see cref="CancellationTokenSource"/> to cancel.</param>
	private static void CancelSource(object? state)
	{
		try
		{
#pragma warning disable VSTHRD103 // CancelAsync is unavailable on all target frameworks.
			((CancellationTokenSource)state!).Cancel();
#pragma warning restore VSTHRD103
		}
		catch (ObjectDisposedException)
		{
			// The enumerator finished and disposed the source before this callback ran.
		}
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

	/// <summary>Forwards an already started enumerator and releases the token that keeps it cancellable.</summary>
	/// <param name="inner">The enumerator to forward to.</param>
	/// <param name="cancellationSource">The token source passed to <paramref name="inner"/>, disposed with it.</param>
	/// <param name="registration">The registration that cancels <paramref name="cancellationSource"/> when a later caller cancels.</param>
	private sealed class LinkedEnumerator(IAsyncEnumerator<T> inner, CancellationTokenSource? cancellationSource, CancellationTokenRegistration registration) : IAsyncEnumerator<T>
	{
		/// <inheritdoc/>
		public T Current => inner.Current;

		/// <inheritdoc/>
		public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();

		/// <inheritdoc/>
		public async ValueTask DisposeAsync()
		{
			try
			{
				await inner.DisposeAsync().ConfigureAwait(false);
			}
			finally
			{
				registration.Dispose();
				cancellationSource?.Dispose();
			}
		}
	}
}
