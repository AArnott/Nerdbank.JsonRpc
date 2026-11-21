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
	private OwnedCancellation? enumerationCancellation;
	private IAsyncEnumerator<T>? startedEnumerator;
	private List<T>? prefetchedValues;
	private bool exhausted;
	private bool enumeratorAcquired;

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

		if (this.startedEnumerator is null)
		{
			return this.inner.GetAsyncEnumerator(cancellationToken);
		}

		// Prefetch already started the only enumerator this wrapper can own. A second acquire would keep
		// the first caller's token (often CancellationToken.None) and drop any values buffered later.
		if (this.enumeratorAcquired)
		{
			throw new InvalidOperationException("A partially prefetched sequence may only be enumerated once.");
		}

		// Prefetch started the iterator with a token owned by this sequence. The request token that
		// was current during prefetch is disposed when that request completes, so later pulls and
		// shutdown must cancel this token instead.
		this.enumeratorAcquired = true;
		OwnedCancellation cancellation = this.enumerationCancellation!;
		CancellationTokenRegistration registration = cancellationToken.Register(static state => ((OwnedCancellation)state!).CancelFromLinkedToken(), cancellation);
		List<T>? pending = this.prefetchedValues;
		this.prefetchedValues = null;
		IAsyncEnumerator<T> started = this.startedEnumerator;
		IAsyncEnumerator<T> enumerator = pending is { Count: > 0 } ? new PrependingEnumerator(pending, started) : started;
		return new LinkedEnumerator(enumerator, cancellation, registration);
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

		if (this.enumeratorAcquired)
		{
			throw new InvalidOperationException("A partially prefetched sequence cannot buffer more values after enumeration has started.");
		}

		// Link the caller only for this prefetch. The completed request's token is disposed afterward and must not cancel later pulls.
		this.enumerationCancellation ??= new OwnedCancellation();
		CancellationTokenRegistration requestRegistration = cancellationToken.Register(static state => ((OwnedCancellation)state!).CancelFromLinkedToken(), this.enumerationCancellation);
		IAsyncEnumerator<T>? enumerator = null;
		bool keepEnumerator = false;
		try
		{
			// The caller registration only queues cancellation, so a token that is already canceled would otherwise
			// let a synchronous iterator produce every requested value before that work runs.
			cancellationToken.ThrowIfCancellationRequested();
			this.startedEnumerator ??= this.inner.GetAsyncEnumerator(this.enumerationCancellation.Token);
			enumerator = this.startedEnumerator;
			List<T> values = this.prefetchedValues ?? new List<T>(count);
			while (values.Count < count)
			{
				cancellationToken.ThrowIfCancellationRequested();
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
			if (!keepEnumerator)
			{
				this.startedEnumerator = null;
				try
				{
					if (enumerator is not null)
					{
						// Keep the request linked until disposal finishes. The enumerator was started with the
						// owned token, so unlinking first would leave a dispose that waits on that token stuck.
						this.enumerationCancellation?.WaitUntilSignaled();
						await enumerator.DisposeAsync().ConfigureAwait(false);
					}
				}
				finally
				{
#pragma warning disable VSTHRD103 // Unregistering a cancellation callback is synchronous.
					requestRegistration.Dispose();
#pragma warning restore VSTHRD103
					this.enumerationCancellation?.Release();
					this.enumerationCancellation = null;
				}
			}
			else
			{
#pragma warning disable VSTHRD103 // Unregistering a cancellation callback is synchronous.
				requestRegistration.Dispose();
#pragma warning restore VSTHRD103
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
	/// <param name="cancellation">The token source passed to <paramref name="inner"/>, released with it.</param>
	/// <param name="registration">The registration that cancels <paramref name="cancellation"/> when a later caller cancels.</param>
	private sealed class LinkedEnumerator(IAsyncEnumerator<T> inner, OwnedCancellation cancellation, CancellationTokenRegistration registration) : IAsyncEnumerator<T>
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
				// Disposal can otherwise run before the queued cancel, so finally would observe a live token.
				// Waiting only until the token is signaled lets blocking callbacks finish on the worker.
				cancellation.WaitUntilSignaled();
				await inner.DisposeAsync().ConfigureAwait(false);
			}
			finally
			{
#pragma warning disable VSTHRD103 // Unregistering a cancellation callback is synchronous.
				registration.Dispose();
#pragma warning restore VSTHRD103
				cancellation.Release();
			}
		}
	}

	/// <summary>
	/// A token source whose cancellation is queued off the caller.
	/// Connection shutdown cancels through this source, and a callback that waits for
	/// <see cref="JsonRpc.Completion"/> must not run on the path that signals that completion.
	/// </summary>
	private sealed class OwnedCancellation
	{
		private readonly CancellationTokenSource source = new();
		private readonly object gate = new();
		private bool cancelScheduled;
		private bool cancelFinished;
		private bool disposeRequested;
		private bool released;

		/// <summary>Gets the token observed by the started enumerator.</summary>
		internal CancellationToken Token => this.source.Token;

		private bool IsSignaled
		{
			get
			{
				try
				{
					return this.source.IsCancellationRequested;
				}
				catch (ObjectDisposedException)
				{
					return true;
				}
			}
		}

		/// <summary>Queues cancellation so the caller's stack, including connection shutdown, is not blocked by iterator callbacks.</summary>
		internal void CancelFromLinkedToken()
		{
			lock (this.gate)
			{
				if (this.released || this.cancelScheduled)
				{
					return;
				}

				this.cancelScheduled = true;
			}

			// A dedicated thread, not the pool: disposal waits until the token is signaled and must not starve this work.
			Thread thread = new(static state => ((OwnedCancellation)state!).CancelCore())
			{
				IsBackground = true,
				Name = "JSON-RPC enumerable cancellation",
			};
			try
			{
				thread.Start(this);
			}
			catch (Exception ex) when (ex is OutOfMemoryException or ThreadStartException)
			{
				// Starting the thread failed after cancellation was marked scheduled. Run it here so a disposal
				// waiting on the flag cannot spin forever. Callbacks may then run on this caller.
				this.CancelCore();
			}
		}

		/// <summary>
		/// Blocks until a queued cancel has marked the token canceled.
		/// Returns immediately when cancellation was not requested. Does not wait for callbacks, which may depend on disposal finishing.
		/// </summary>
		internal void WaitUntilSignaled()
		{
			if (this.IsSignaled)
			{
				return;
			}

			bool scheduled;
			lock (this.gate)
			{
				scheduled = this.cancelScheduled && !this.released;
			}

			if (!scheduled)
			{
				return;
			}

			SpinWait spin = default;
			while (!this.IsSignaled)
			{
				spin.SpinOnce();
			}
		}

		/// <summary>
		/// Releases the source. An in-flight cancel disposes the source after its callbacks return,
		/// so a callback cannot observe a disposed source and a disposal exception cannot skip the release.
		/// </summary>
		internal void Release()
		{
			bool releaseNow = false;
			lock (this.gate)
			{
				if (this.released)
				{
					return;
				}

				if (this.cancelScheduled && !this.cancelFinished)
				{
					this.disposeRequested = true;
					return;
				}

				this.released = true;
				releaseNow = true;
			}

			if (releaseNow)
			{
				this.source.Dispose();
			}
		}

		private void CancelCore()
		{
			try
			{
#pragma warning disable VSTHRD103 // CancelAsync is unavailable on all target frameworks.
				this.source.Cancel();
#pragma warning restore VSTHRD103
			}
			catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
			{
				// The source may already have been released, or a callback may have thrown after cancellation was signaled.
			}
			finally
			{
				bool releaseNow = false;
				lock (this.gate)
				{
					this.cancelFinished = true;
					if (this.disposeRequested && !this.released)
					{
						this.released = true;
						releaseNow = true;
					}
				}

				if (releaseNow)
				{
					this.source.Dispose();
				}
			}
		}
	}
}
