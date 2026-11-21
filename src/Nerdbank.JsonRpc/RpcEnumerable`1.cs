// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Reflection;

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
		CancellationTokenRegistration registration = default;
		if (cancellationToken.CanBeCanceled && !cancellationToken.Equals(cancellation.Token))
		{
			registration = cancellationToken.Register(static state => ((OwnedCancellation)state!).CancelFromLinkedToken(), cancellation);
		}

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

		// An inbound request token is the one the target method already captured, and this sequence holds
		// a lease on it. Any other caller token is only an input: canceling the source behind it would
		// also cancel unrelated work that shares that token, including the call that is sending this sequence.
		// An inbound request that has already begun canceling is left alone, because its callbacks still
		// run on the shutdown stack and registering the iterator there can block Completion.
		if (this.enumerationCancellation is null)
		{
			if (this.requestCancellation is InboundRequestCancellation request && request.TryArmEnumeration())
			{
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
			if (this.startedEnumerator is null)
			{
				this.startedEnumerator = this.inner.GetAsyncEnumerator(this.enumerationCancellation.Token);

				// When the iterator captured a different token, the compiler observes a linked source.
				// Remember it before any await so cancellation can reach that source ahead of prefetch-token callbacks.
				this.enumerationCancellation.ObserveStartedEnumerator(this.startedEnumerator);
			}

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
						// Wait until the token the iterator observes is canceled, then drop the wait
						// before DisposeAsync releases a compiler-linked source that owns the handle.
						await this.enumerationCancellation!.WaitUntilSignaledAsync().ConfigureAwait(false);
						this.enumerationCancellation.DetachWait();
						await enumerator.DisposeAsync().ConfigureAwait(false);
					}
				}
				finally
				{
#pragma warning disable VSTHRD103 // Unregistering a cancellation callback is synchronous.
					requestRegistration.Dispose();
#pragma warning restore VSTHRD103
					this.enumerationCancellation?.DetachWait();
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
				// Wait until the token the iterator observes is canceled, not until its callbacks return,
				// so a callback can block on disposal. A compiler-linked token is canceled before callbacks
				// on the prefetch token; waiting on the prefetch wait handle instead lets disposal drop the link.
				await cancellation.WaitUntilSignaledAsync().ConfigureAwait(false);
				cancellation.DetachWait();
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
		private readonly bool ownsSource;
		private readonly object gate = new();
		private CancellationTokenSource? compilerLinked;
		private RegisteredWaitHandle? waitRegistration;
		private TaskCompletionSource<bool>? signaled;
		private ManualResetEventSlim? linkedFlag;
		private bool published;
		private bool cancelScheduled;
		private bool cancelFinished;
		private int pendingCancels;
		private bool disposeRequested;
		private bool released;
		private int waitArmed;

		private OwnedCancellation(CancellationTokenSource source, InboundRequestCancellation? lease, bool ownsSource)
		{
			this.source = source;
			this.lease = lease;
			this.ownsSource = ownsSource;
		}

		/// <summary>Gets the token observed by the started enumerator.</summary>
		internal CancellationToken Token => this.source.Token;

		/// <summary>Creates a source owned entirely by the sequence.</summary>
		/// <returns>The cancellation owner.</returns>
		internal static OwnedCancellation Create() => new(new CancellationTokenSource(), lease: null, ownsSource: true);

		/// <summary>Cancels and releases an existing request source instead of a private one.</summary>
		/// <param name="lease">The request lease. The caller has already armed it.</param>
		/// <returns>The cancellation owner.</returns>
		internal static OwnedCancellation Adopt(InboundRequestCancellation lease) => new(lease.Source, lease, ownsSource: false);

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
				this.pendingCancels++;
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
		/// Records the linked source a compiler-generated async iterator created while starting <paramref name="enumerator"/>.
		/// </summary>
		/// <param name="enumerator">The enumerator that was just started with <see cref="Token"/>.</param>
		/// <remarks>
		/// The C# compiler stores that source in a private field named <c>&lt;&gt;x__combinedTokens</c> only when the
		/// iterator captured one cancelable token and was started with a different one. The iterator's
		/// <see langword="finally"/> block reads the linked token. NativeAOT can trim the field from reflection;
		/// disposal then waits on <see cref="Token"/>, whose wait handle is signaled before the compiler link runs.
		/// </remarks>
		internal void ObserveStartedEnumerator(object enumerator)
		{
			CancellationTokenSource? linked = TryGetCompilerLinkedSource(enumerator);
			if (linked is null || ReferenceEquals(linked, this.source))
			{
				return;
			}

			bool queueCatchUp;
			lock (this.gate)
			{
				Volatile.Write(ref this.compilerLinked, linked);

				// Cancellation may already be queued or finished from inside GetAsyncEnumerator, before this
				// source was visible. Catch up off this stack: a callback waiting for finally would deadlock
				// prefetch if it ran here, because disposal has not started.
				queueCatchUp = this.cancelScheduled || this.IsCanceled();
				if (queueCatchUp)
				{
					this.pendingCancels++;
				}
			}

			if (!queueCatchUp)
			{
				return;
			}

			try
			{
				ThreadPool.QueueUserWorkItem(static state => ((OwnedCancellation)state!).CancelLinkedCatchUp(), this);
			}
			catch (NotSupportedException)
			{
				this.CancelLinkedCatchUp();
			}
		}

		/// <summary>
		/// Completes when the token the iterator observes is canceled, or immediately when cancellation was not requested.
		/// </summary>
		/// <returns>A task that completes once that token is canceled, without waiting for its callbacks.</returns>
		/// <remarks>
		/// The runtime signals <see cref="CancellationToken.WaitHandle"/> before it runs callbacks on that token,
		/// so a callback can block on disposal. When the compiler linked the prefetch token with the token the
		/// iterator captured, the iterator observes the linked token. <see cref="CancelCore"/> sets that linked
		/// flag before prefetch-token callbacks run, without waiting for the linked callbacks themselves.
		/// Waiting only for the prefetch wait handle lets an earlier callback hold that list while disposal drops the link.
		/// A disposed linked source ends this wait. Falling back to the prefetch token can hang, because a linked
		/// callback may still be waiting for disposal and the prefetch source is canceled only after that callback returns.
		/// A callback that blocks on <see cref="JsonRpc.Completion"/> still runs off the shutdown path because
		/// cancellation itself is queued.
		/// </remarks>
		internal ValueTask WaitUntilSignaledAsync()
		{
			if (!this.TryGetObservedToken(out CancellationToken observed))
			{
				// A second dispose runs after the source was released. There is nothing left to wait for.
				return default;
			}

			TaskCompletionSource<bool>? waiter = null;
			lock (this.gate)
			{
				if (this.published || this.released)
				{
					return default;
				}

				if (IsCancellationRequested(observed))
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

			this.ArmWait(observed);
			return new ValueTask(waiter.Task);
		}

		/// <summary>
		/// Drops the wait registration before the enumerator is disposed.
		/// A compiler iterator disposes its linked source from <see cref="IAsyncDisposable.DisposeAsync"/>,
		/// and that source owns the handle being waited on.
		/// </summary>
		internal void DetachWait() => this.TakeWaitRegistration()?.Unregister(null);

		/// <summary>
		/// Releases the source. An in-flight cancel, including compiler-linked callbacks that were queued
		/// off this thread, disposes the source only after those callbacks return. Iterator disposal does not
		/// wait for them, so a callback can block on <see langword="finally"/> and still read this source.
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

				if (this.pendingCancels > 0 || (this.cancelScheduled && !this.cancelFinished))
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

		private static bool TryReadToken(CancellationTokenSource source, out CancellationToken token)
		{
			try
			{
				token = source.Token;
				return true;
			}
			catch (ObjectDisposedException)
			{
				// Disposal can overlap a second DisposeAsync after the source has already been released.
				token = default;
				return false;
			}
		}

		private static bool IsCancellationRequested(CancellationToken token)
		{
			try
			{
				return token.IsCancellationRequested;
			}
			catch (ObjectDisposedException)
			{
				return true;
			}
		}

		/// <summary>Reads <c>&lt;&gt;x__combinedTokens</c> from a compiler-generated async iterator, when reflection can see it.</summary>
		/// <param name="enumerator">The started enumerator.</param>
		/// <returns>The linked source, or <see langword="null"/> when the iterator did not create one or the field is not visible.</returns>
#if NET
		[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Best-effort lookup of the C# compiler's <>x__combinedTokens field. When reflection metadata is unavailable, disposal waits on the prefetch token.")]
		[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Best-effort lookup of the C# compiler's <>x__combinedTokens field. When reflection metadata is unavailable, disposal waits on the prefetch token.")]
#endif
		private static CancellationTokenSource? TryGetCompilerLinkedSource(object enumerator)
		{
			try
			{
				FieldInfo? field = enumerator.GetType().GetField(
					"<>x__combinedTokens",
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
				return field?.GetValue(enumerator) as CancellationTokenSource;
			}
			catch (Exception ex) when (ex is NotSupportedException or FieldAccessException or TargetException or ArgumentException)
			{
				return null;
			}
		}

		private void CancelCore()
		{
			// The compiler link is one of this source's callbacks. Set the linked flag first so a callback
			// registered ahead of that link cannot keep the iterator's token uncanceled until disposal drops it.
			// Do not run the linked callbacks on this thread. One of them can wait for finally while finally
			// waits for this source, and joining those callbacks would leave both waits stuck.
			this.SignalCompilerLinkedToken();
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
				this.CompleteCancel();
			}
		}

		/// <summary>
		/// Cancels a compiler-linked source discovered after cancellation was already queued.
		/// Runs off the prefetch stack so a callback can block on disposal.
		/// </summary>
		private void CancelLinkedCatchUp()
		{
			try
			{
				this.SignalCompilerLinkedToken();
			}
			finally
			{
				this.CompleteCancel();
			}
		}

		/// <summary>Drops one in-flight cancel and releases the source when none remain.</summary>
		private void CompleteCancel()
		{
			bool releaseNow = false;
			lock (this.gate)
			{
				this.pendingCancels--;
				if (this.pendingCancels > 0)
				{
					return;
				}

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

		/// <summary>
		/// Sets the compiler-linked cancellation flag without running that source's callbacks on this thread.
		/// </summary>
		/// <remarks>
		/// <see cref="CancellationTokenSource.Cancel()"/> runs callbacks before it returns. A linked callback can
		/// wait for the iterator's <see langword="finally"/> block while that block waits for <see cref="source"/>.
		/// Joining the linked callbacks first leaves both waits stuck. The flag still has to flip before
		/// <see cref="source"/> callbacks run, or an earlier prefetch-token callback can dispose the link
		/// before the iterator observes it.
		/// The linked callbacks are still counted in <see cref="pendingCancels"/> until they return, so
		/// <see cref="Release"/> does not dispose <see cref="source"/> while one of them is reading its token.
		/// That count does not delay <see cref="source"/> cancellation or iterator disposal.
		/// </remarks>
		private void SignalCompilerLinkedToken()
		{
			CancellationTokenSource? linked = Volatile.Read(ref this.compilerLinked);
			if (linked is null || !TryReadToken(linked, out CancellationToken token) || IsCancellationRequested(token))
			{
				return;
			}

			bool start;
			ManualResetEventSlim flag;
			lock (this.gate)
			{
				if (this.released)
				{
					return;
				}

				if (this.linkedFlag is null)
				{
					this.linkedFlag = new ManualResetEventSlim(false);
					this.pendingCancels++;
					start = true;
				}
				else
				{
					start = false;
				}

				flag = this.linkedFlag;
			}

			if (!start)
			{
				// Another caller is setting the flag. Wait for that, not for its callbacks.
				try
				{
					flag.Wait();
				}
				catch (ObjectDisposedException)
				{
					// Release already dropped the event after the callbacks finished.
				}

				return;
			}

			try
			{
				// A worker calls Cancel so its callbacks are snapshotted before it invokes them.
				// CancelAsync can lose that race: iterator disposal clears the registration list
				// before the queued handlers run, and the callback never starts.
				// This thread waits only until the flag is visible, then cancels the prefetch source.
				// The worker keeps the pending count until the callbacks return.
				this.StartLinkedCancelWorker(linked, token);
			}
			finally
			{
				try
				{
					flag.Set();
				}
				catch (ObjectDisposedException)
				{
					// Release already dropped the event after the callbacks finished.
				}
			}
		}

		/// <summary>Queues <see cref="CancellationTokenSource.Cancel()"/> and waits until its flag is visible.</summary>
		/// <param name="linked">The compiler-linked source.</param>
		/// <param name="token">The token of <paramref name="linked"/>.</param>
		private void StartLinkedCancelWorker(CancellationTokenSource linked, CancellationToken token)
		{
			LinkedCancelWork work = new(this, linked);
			try
			{
				try
				{
					ThreadPool.QueueUserWorkItem(static state => ((LinkedCancelWork)state!).Run(), work);
				}
				catch (NotSupportedException)
				{
					Thread thread = new(static state => ((LinkedCancelWork)state!).Run())
					{
						IsBackground = true,
					};
					thread.Start(work);
				}
			}
			catch (Exception ex) when (ex is NotSupportedException or OutOfMemoryException)
			{
				// The worker never started, so nothing else will drop the pending count.
				this.CompleteCancel();
				throw;
			}

			work.WaitUntilObservable(token);
		}

		private bool TryGetObservedToken(out CancellationToken observed)
		{
			CancellationTokenSource? linked = Volatile.Read(ref this.compilerLinked);
			if (linked is not null)
			{
				if (TryReadToken(linked, out observed))
				{
					return true;
				}

				// The linked source was disposed. Its wait handle does not wake waiters. Waiting on the
				// prefetch token instead outlives the source the iterator was actually observing.
				observed = default;
				return false;
			}

			return TryReadToken(this.source, out observed);
		}

		private void ArmWait(CancellationToken token)
		{
			if (Interlocked.Exchange(ref this.waitArmed, 1) != 0)
			{
				return;
			}

			if (IsCancellationRequested(token))
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
				if (IsCancellationRequested(token))
				{
					this.PublishSignaled();
				}
			}
			catch (NotSupportedException)
			{
				if (IsCancellationRequested(token))
				{
					this.PublishSignaled();
				}
			}
			catch (ObjectDisposedException)
			{
				// The compiler source was disposed before its handle could be waited on.
				this.PublishSignaled();
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

		private RegisteredWaitHandle? TakeWaitRegistration()
		{
			lock (this.gate)
			{
				RegisteredWaitHandle? registration = this.waitRegistration;
				this.waitRegistration = null;
				return registration;
			}
		}

		private void DisposeSource()
		{
			this.TakeWaitRegistration()?.Unregister(null);
			ManualResetEventSlim? flag;
			lock (this.gate)
			{
				flag = this.linkedFlag;
				this.linkedFlag = null;
			}

			flag?.Dispose();
			if (this.lease is not null)
			{
				this.lease.Release();
			}
			else if (this.ownsSource)
			{
				this.source.Dispose();
			}
		}

		/// <summary>Runs <see cref="CancellationTokenSource.Cancel()"/> off the caller and wakes that caller when the flag is set or the attempt ends.</summary>
		private sealed class LinkedCancelWork
		{
			private readonly OwnedCancellation owner;
			private readonly CancellationTokenSource linked;
			private readonly ManualResetEvent finished = new(false);

			internal LinkedCancelWork(OwnedCancellation owner, CancellationTokenSource linked)
			{
				this.owner = owner;
				this.linked = linked;
			}

			internal void Run()
			{
				try
				{
#pragma warning disable VSTHRD103 // Cancel is synchronous so callbacks are snapshotted before iterator disposal.
					this.linked.Cancel();
#pragma warning restore VSTHRD103
				}
				catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
				{
					// Already disposed, or a callback threw after the flag was set.
				}
				finally
				{
					try
					{
						this.finished.Set();
					}
					catch (ObjectDisposedException)
					{
						// The waiter already observed the flag and released this event.
					}

					// Callbacks have returned. The owned source can be disposed now, and not before.
					this.owner.CompleteCancel();
					this.finished.Dispose();
				}
			}

			/// <summary>Blocks until <paramref name="token"/> is signaled or <see cref="Run"/> has finished.</summary>
			/// <param name="token">The linked token. Its wait handle is signaled before callbacks run.</param>
			internal void WaitUntilObservable(CancellationToken token)
			{
				try
				{
					if (!IsCancellationRequested(token))
					{
						WaitHandle.WaitAny(new WaitHandle[] { token.WaitHandle, this.finished });
					}
				}
				catch (ObjectDisposedException)
				{
					// The linked source was disposed before its handle could be waited on.
				}
			}
		}
	}
}
