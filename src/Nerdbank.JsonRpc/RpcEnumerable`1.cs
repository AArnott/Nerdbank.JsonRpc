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
	private InboundRequestCancellation? requestCancellation;
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
	public void AttachRequestCancellation(InboundRequestCancellation cancellation) => this.requestCancellation ??= cancellation;

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

		// Prefer the request token the iterator method already captured. A second token would make a compiler
		// iterator link the two, and that link flips only inside a cancellation callback.
		if (this.enumerationCancellation is null)
		{
			if (this.requestCancellation is InboundRequestCancellation request)
			{
				request.Retain();
				this.enumerationCancellation = OwnedCancellation.Adopt(request);
			}
			else
			{
				this.enumerationCancellation = OwnedCancellation.Create();
			}
		}

		// Link the caller only for this prefetch. Skip it when the caller token is the iterator token:
		// registering a callback on that token would run during its own cancellation.
		CancellationTokenRegistration requestRegistration = default;
		if (cancellationToken.CanBeCanceled && !cancellationToken.Equals(this.enumerationCancellation.Token))
		{
			requestRegistration = cancellationToken.Register(static state => ((OwnedCancellation)state!).CancelFromLinkedToken(), this.enumerationCancellation);
		}

		// A previous prefetch may already own an enumerator. Capturing it before the token check means a
		// canceled repeat prefetch still disposes that enumerator instead of dropping it.
		IAsyncEnumerator<T>? enumerator = this.startedEnumerator;
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
						// Disposal waits until the iterator token is canceled. Unlinking first can drop that signal.
						await this.enumerationCancellation!.WaitUntilSignaledAsync().ConfigureAwait(false);
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
				// The token flag flips before any callback. Waiting for that, not for callbacks to finish,
				// lets a callback block on disposal without keeping the iterator alive.
				await cancellation.WaitUntilSignaledAsync().ConfigureAwait(false);
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
		private readonly CancellationTokenSource source;
		private readonly InboundRequestCancellation? lease;
		private readonly object gate = new();
		private RegisteredWaitHandle? waitRegistration;
		private TaskCompletionSource<bool>? signaled;
		private bool published;
		private bool cancelScheduled;
		private bool cancelFinished;
		private bool disposeRequested;
		private bool released;
		private int waitArmed;

		private OwnedCancellation(CancellationTokenSource source, InboundRequestCancellation? lease)
		{
			this.source = source;
			this.lease = lease;
		}

		/// <summary>Gets the token observed by the started enumerator.</summary>
		internal CancellationToken Token => this.source.Token;

		/// <summary>Creates a source owned entirely by the sequence.</summary>
		/// <returns>The cancellation owner.</returns>
		internal static OwnedCancellation Create() => new(new CancellationTokenSource(), lease: null);

		/// <summary>Cancels and releases an existing request source instead of a private one.</summary>
		/// <param name="lease">The request lease. The caller has already retained it.</param>
		/// <returns>The cancellation owner.</returns>
		internal static OwnedCancellation Adopt(InboundRequestCancellation lease) => new(lease.Source, lease);

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

			// The pool, not a dedicated thread: disposal awaits the signal and does not occupy a worker while it waits.
			// If the pool rejects the item, run it here so a waiter cannot hang. Callbacks may then run on this caller.
			try
			{
				ThreadPool.QueueUserWorkItem(static state => ((OwnedCancellation)state!).CancelCore(), this);
			}
			catch (NotSupportedException)
			{
				this.CancelCore();
			}
		}

		/// <summary>
		/// Completes when the token's cancellation flag is set, or immediately when cancellation was not requested.
		/// </summary>
		/// <returns>A task that completes once the token is canceled, without waiting for cancellation callbacks.</returns>
		/// <remarks>
		/// The runtime sets the flag and signals <see cref="CancellationToken.WaitHandle"/> before it runs callbacks.
		/// A callback registered by an in-flight move can therefore block on disposal without delaying this wait,
		/// and a callback that blocks on <see cref="JsonRpc.Completion"/> still runs off the shutdown path because
		/// cancellation itself is queued.
		/// </remarks>
		internal ValueTask WaitUntilSignaledAsync()
		{
			TaskCompletionSource<bool>? waiter = null;
			lock (this.gate)
			{
				if (this.published || this.released)
				{
					return default;
				}

				if (this.IsCanceled())
				{
					this.published = true;
					return default;
				}

				if (!this.cancelScheduled)
				{
					return default;
				}

				this.signaled ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				waiter = this.signaled;
			}

			this.ArmWait();
			return new ValueTask(waiter.Task);
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
				this.DisposeSource();
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
				// The wait handle is what unblocks disposal. Publishing here only covers a waiter that raced
				// ahead of the flag; it does not make disposal depend on callback order.
				this.PublishSignaled();
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
					this.DisposeSource();
				}
			}
		}

		private void ArmWait()
		{
			if (Interlocked.Exchange(ref this.waitArmed, 1) != 0)
			{
				return;
			}

			CancellationToken token = this.source.Token;
			if (this.IsCanceled())
			{
				this.PublishSignaled();
				return;
			}

			try
			{
				RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
					token.WaitHandle,
					static (state, timedOut) => ((OwnedCancellation)state!).PublishSignaled(),
					this,
					Timeout.Infinite,
					executeOnlyOnce: true);

				bool unregister = false;
				lock (this.gate)
				{
					if (this.published || this.released)
					{
						unregister = true;
					}
					else
					{
						this.waitRegistration = registration;
					}
				}

				if (unregister)
				{
					registration.Unregister(null);
				}

				// Cancellation can win the race between the check above and the registration.
				if (this.IsCanceled())
				{
					this.PublishSignaled();
				}
			}
			catch (NotSupportedException)
			{
				if (this.IsCanceled())
				{
					this.PublishSignaled();
				}
			}
		}

		/// <summary>Releases waiters once the token flag is set, without waiting for later callbacks.</summary>
		private void PublishSignaled()
		{
			TaskCompletionSource<bool>? waiter;
			lock (this.gate)
			{
				if (this.published)
				{
					return;
				}

				this.published = true;
				waiter = this.signaled;
			}

			waiter?.TrySetResult(true);
		}

		private bool IsCanceled()
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

		private void DisposeSource()
		{
			RegisteredWaitHandle? registration;
			lock (this.gate)
			{
				registration = this.waitRegistration;
				this.waitRegistration = null;
			}

			registration?.Unregister(null);
			if (this.lease is not null)
			{
				this.lease.Release();
			}
			else
			{
				this.source.Dispose();
			}
		}
	}
}
