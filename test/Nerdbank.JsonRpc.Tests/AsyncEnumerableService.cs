// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;

internal sealed class AsyncEnumerableService : IAsyncEnumerableService
{
	private int generatedValues;
	private bool generatorDisposed;
	private int cancelableEnumerationCanceled;
	private int blockingEnumerationCanceled;
	private int blockingBatchCanceled;
	private int prefetchedBlockCanceled;
	private int suspendedYieldCanceled;
	private int disposalWaitCanceled;

	internal TaskCompletionSource<bool> UncooperativeMoveNextStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal TaskCompletionSource<bool> UncooperativeDisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal TaskCompletionSource<bool> ReleaseUncooperativeOperation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal TaskCompletionSource<bool> FailingGeneratorDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the signal that <see cref="GetNumbersUntilCanceledAsync"/> is blocked in its cancelable wait.</summary>
	internal TaskCompletionSource<bool> CancelableEnumerationWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the signal that <see cref="GetNumbersUntilCanceledAsync"/> has left its <see langword="finally"/> block.</summary>
	internal TaskCompletionSource<bool> CancelableEnumerationDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets a value indicating whether <see cref="GetNumbersUntilCanceledAsync"/> observed a canceled enumerator token.</summary>
	internal bool CancelableEnumerationCanceled => Volatile.Read(ref this.cancelableEnumerationCanceled) != 0;

	/// <summary>Gets the signal that <see cref="GetNumbersIgnoringCancellationAsync"/> is blocked in an await that ignores cancellation.</summary>
	internal TaskCompletionSource<bool> BlockingEnumerationWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the signal that <see cref="GetNumbersIgnoringCancellationAsync"/> has left its <see langword="finally"/> block.</summary>
	internal TaskCompletionSource<bool> BlockingEnumerationDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the gate released by a test so <see cref="GetNumbersIgnoringCancellationAsync"/> can yield again.</summary>
	internal TaskCompletionSource<bool> ReleaseBlockingEnumeration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets a value indicating whether <see cref="GetNumbersIgnoringCancellationAsync"/> observed a canceled enumerator token.</summary>
	internal bool BlockingEnumerationCanceled => Volatile.Read(ref this.blockingEnumerationCanceled) != 0;

	/// <summary>Gets the signal that <see cref="GetBlockingBatchAsync"/> is blocked before its second value.</summary>
	internal TaskCompletionSource<bool> BlockingBatchWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the gate that lets <see cref="GetBlockingBatchAsync"/> produce its second value.</summary>
	internal TaskCompletionSource<bool> ReleaseBlockingBatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the signal that <see cref="GetBlockingBatchAsync"/> has started the move that produces its third value.</summary>
	internal TaskCompletionSource<bool> BlockingBatchSecondMoveWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the gate that lets <see cref="GetBlockingBatchAsync"/> leave its third move.</summary>
	internal TaskCompletionSource<bool> ReleaseBlockingBatchSecondMove { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the signal set from the <see langword="finally"/> block of <see cref="GetBlockingBatchAsync"/>.</summary>
	internal TaskCompletionSource<bool> BlockingBatchDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets a value indicating whether <see cref="GetBlockingBatchAsync"/> observed a canceled enumerator token.</summary>
	internal bool BlockingBatchCanceled => Volatile.Read(ref this.blockingBatchCanceled) != 0;

	/// <summary>Gets the signal that <see cref="GetPrefetchedBlockingSequenceAsync"/> is blocked after its prefetched value.</summary>
	internal TaskCompletionSource<bool> PrefetchedBlockWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the signal set from the <see langword="finally"/> block of <see cref="GetPrefetchedBlockingSequenceAsync"/>.</summary>
	internal TaskCompletionSource<bool> PrefetchedBlockDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets a value indicating whether <see cref="GetPrefetchedBlockingSequenceAsync"/> observed a canceled enumerator token.</summary>
	internal bool PrefetchedBlockCanceled => Volatile.Read(ref this.prefetchedBlockCanceled) != 0;

	/// <summary>Gets the signal that <see cref="GetPrefetchedSequenceWhoseCancellationBlocksAsync"/> is blocked after its prefetched value.</summary>
	internal TaskCompletionSource<bool> PrefetchedCancellationBlockWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the signal set when the cancellation callback of <see cref="GetPrefetchedSequenceWhoseCancellationBlocksAsync"/> returns.</summary>
	internal TaskCompletionSource<bool> BlockingCancellationFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the signal set from the <see langword="finally"/> block of <see cref="GetPrefetchedSequenceWhoseCancellationBlocksAsync"/>.</summary>
	internal TaskCompletionSource<bool> PrefetchedCancellationBlockDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets or sets the task a cancellation callback blocks on. Tests assign the server connection's <see cref="JsonRpc.Completion"/>.</summary>
	internal Task? CancellationCallbackBlocksOn { get; set; }

	/// <summary>Gets the signal set from the <see langword="finally"/> block of <see cref="GetPrefetchedSuspendedYieldAsync"/>.</summary>
	internal TaskCompletionSource<bool> SuspendedYieldDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets a value indicating whether <see cref="GetPrefetchedSuspendedYieldAsync"/> observed a canceled enumerator token.</summary>
	internal bool SuspendedYieldCanceled => Volatile.Read(ref this.suspendedYieldCanceled) != 0;

	/// <summary>Gets the signal set from the <see langword="finally"/> block of <see cref="GetPrefetchedSequenceWhoseCancellationWaitsForDisposalAsync"/>.</summary>
	internal TaskCompletionSource<bool> PrefetchedDisposalWaitDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the signal set when the cancellation callback of <see cref="GetPrefetchedSequenceWhoseCancellationWaitsForDisposalAsync"/> returns.</summary>
	internal TaskCompletionSource<bool> DisposalWaitCallbackFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets a value indicating whether <see cref="GetPrefetchedSequenceWhoseCancellationWaitsForDisposalAsync"/> observed a canceled enumerator token.</summary>
	internal bool DisposalWaitCanceled => Volatile.Read(ref this.disposalWaitCanceled) != 0;

	/// <summary>Gets the signal that <see cref="GetSequenceThatBlocksDuringPrefetchAsync"/> is blocked inside prefetch.</summary>
	internal TaskCompletionSource<bool> PrefetchDuringBlockWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the signal set from the <see langword="finally"/> block of <see cref="GetSequenceThatBlocksDuringPrefetchAsync"/>.</summary>
	internal TaskCompletionSource<bool> PrefetchDuringBlockDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets the callback retained by the last sequence-producing call.</summary>
	internal ICallScopedCounter? LastCounter { get; private set; }

	public IAsyncEnumerable<int> GetNumbersAsync(int count, CancellationToken cancellationToken) => this.ProduceAsync(count, cancellationToken);

	/// <inheritdoc/>
	public async IAsyncEnumerable<int> GetNumbersUntilCanceledAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		try
		{
			yield return 0;
			this.CancelableEnumerationWaiting.TrySetResult(true);
			await Task.Delay(Timeout.Infinite, cancellationToken);
		}
		finally
		{
			Volatile.Write(ref this.cancelableEnumerationCanceled, cancellationToken.IsCancellationRequested ? 1 : 0);
			this.CancelableEnumerationDisposed.TrySetResult(true);
		}
	}

	/// <inheritdoc/>
	public async IAsyncEnumerable<int> GetNumbersIgnoringCancellationAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		try
		{
			yield return 0;
			this.BlockingEnumerationWaiting.TrySetResult(true);
			await this.ReleaseBlockingEnumeration.Task;
			yield return 1;
		}
		finally
		{
			Volatile.Write(ref this.blockingEnumerationCanceled, cancellationToken.IsCancellationRequested ? 1 : 0);
			this.BlockingEnumerationDisposed.TrySetResult(true);
		}
	}

	/// <inheritdoc/>
	public IAsyncEnumerable<int> GetBlockingBatchAsync(int minBatchSize, CancellationToken cancellationToken)
		=> this.BlockAcrossBatchAsync(cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = minBatchSize });

	/// <inheritdoc/>
	public IAsyncEnumerable<int> GetPrefetchedBlockingSequenceAsync(ICallScopedCounter counter, CancellationToken cancellationToken)
	{
		this.LastCounter = counter;
		return this.BlockAfterPrefetchAsync(cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings { Prefetch = 1 });
	}

	/// <inheritdoc/>
	public IAsyncEnumerable<int> GetPrefetchedSequenceWhoseCancellationBlocksAsync(CancellationToken cancellationToken)
		=> this.BlockOnCompletionWhenCanceledAsync(cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings { Prefetch = 1 });

	/// <inheritdoc/>
	public IAsyncEnumerable<int> GetPrefetchedSuspendedYieldAsync(CancellationToken cancellationToken)
		=> this.SuspendAfterPrefetchedYieldAsync(cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings { Prefetch = 1 });

	/// <inheritdoc/>
	public IAsyncEnumerable<int> GetPrefetchedSequenceWhoseCancellationWaitsForDisposalAsync(CancellationToken cancellationToken)
		=> this.WaitForDisposalWhenCanceledAsync(cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings { Prefetch = 1 });

	/// <inheritdoc/>
	public IAsyncEnumerable<int> GetSequenceThatBlocksDuringPrefetchAsync(CancellationToken cancellationToken)
		=> this.BlockDuringPrefetchAsync(cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings { Prefetch = 1 });

	public IAsyncEnumerable<int> GetUncooperativeSequenceAsync(bool blockReadAhead, bool blockDispose, CancellationToken cancellationToken)
		=> new UncooperativeSequence(this, blockReadAhead, blockDispose).WithJsonRpcSettings(new() { MaxReadAhead = blockReadAhead ? 1 : 0 });

	public Task<IAsyncEnumerable<int>> GetNumbersWrappedAsync(int count, CancellationToken cancellationToken)
		=> Task.FromResult(this.ProduceAsync(count, cancellationToken));

	public IAsyncEnumerable<int> GetNumbersWithSettingsAsync(int count, int minBatchSize, int maxReadAhead, int prefetch, CancellationToken cancellationToken)
		=> this.ProduceAsync(count, cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings
		{
			MinBatchSize = minBatchSize,
			MaxReadAhead = maxReadAhead,
			Prefetch = prefetch,
		});

	public IAsyncEnumerable<string> GetWordsAsync(CancellationToken cancellationToken)
		=> new[] { "alpha", "beta", "gamma" }.AsAsyncEnumerable();

	public IAsyncEnumerable<int> GetFailingSequenceAsync(int valuesBeforeFailure, CancellationToken cancellationToken)
		=> this.FailAsync(valuesBeforeFailure, cancellationToken);

	public IAsyncEnumerable<IAsyncEnumerable<int>> GetNestedSequencesAsync(CancellationToken cancellationToken)
		=> ProduceNestedAsync(cancellationToken);

	/// <inheritdoc/>
	public IAsyncEnumerable<int> UseCounterAsync(ICallScopedCounter counter, int count, int prefetch, int readAhead, bool fail, CancellationToken cancellationToken)
	{
		this.LastCounter = counter;
		if (count < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(count));
		}

		return ProduceUsingCounterAsync(counter, count, fail, cancellationToken).WithJsonRpcSettings(new()
		{
			Prefetch = prefetch,
			MaxReadAhead = readAhead,
		});
	}

	/// <inheritdoc/>
	public Task<IAsyncEnumerable<int>> UseCounterWrappedAsync(ICallScopedCounter counter, CancellationToken cancellationToken)
		=> Task.FromResult(this.UseCounterAsync(counter, 3, 0, 0, false, cancellationToken));

	/// <inheritdoc/>
	public Task<IAsyncEnumerable<int>[]> UseCounterInArrayAsync(ICallScopedCounter counter, CancellationToken cancellationToken)
	{
		this.LastCounter = counter;
		return Task.FromResult<IAsyncEnumerable<int>[]>([ProduceUsingCounterAsync(counter, 1, false, cancellationToken), ProduceUsingCounterAsync(counter, 1, false, cancellationToken)]);
	}

	/// <inheritdoc/>
	public IAsyncEnumerable<IAsyncEnumerable<int>> UseCounterInNestedSequencesAsync(ICallScopedCounter counter, CancellationToken cancellationToken)
	{
		this.LastCounter = counter;
		return new[] { ProduceUsingCounterAsync(counter, 1, false, cancellationToken), ProduceUsingCounterAsync(counter, 1, false, cancellationToken) }.AsAsyncEnumerable();
	}

	/// <inheritdoc/>
	public IAsyncEnumerable<int> UseCounterDuringDisposalAsync(ICallScopedCounter counter, int prefetch, CancellationToken cancellationToken)
	{
		this.LastCounter = counter;
		return new DisposalCallbackSequence(counter).WithJsonRpcSettings(new() { Prefetch = prefetch });
	}

	public async Task<int> SumAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
	{
		int sum = 0;
		await foreach (int value in values.WithCancellation(cancellationToken))
		{
			sum += value;
		}

		return sum;
	}

	public async Task<int> SumTwoAsync(IAsyncEnumerable<int> first, IAsyncEnumerable<int> second, CancellationToken cancellationToken)
		=> await this.SumAsync(first, cancellationToken) + await this.SumAsync(second, cancellationToken);

	public Task<int> CountGeneratedValuesAsync(CancellationToken cancellationToken) => Task.FromResult(Volatile.Read(ref this.generatedValues));

	public Task<bool> IsGeneratorDisposedAsync(CancellationToken cancellationToken) => Task.FromResult(Volatile.Read(ref this.generatorDisposed));

	public void NotifyWithSequence(IAsyncEnumerable<int> values)
	{
	}

	private static async IAsyncEnumerable<int> ProduceUsingCounterAsync(ICallScopedCounter counter, int count, bool fail, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		try
		{
			for (int i = 0; i < count; i++)
			{
				if (fail && i == 1)
				{
					throw new InvalidOperationException("Enumeration failed.");
				}

				yield return await counter.IncrementAsync(cancellationToken);
			}
		}
		finally
		{
			await counter.IncrementAsync(CancellationToken.None);
		}
	}

	private static async IAsyncEnumerable<IAsyncEnumerable<int>> ProduceNestedAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		for (int i = 0; i < 2; i++)
		{
			await Task.Yield();
			yield return Enumerable.Range(i * 10, 3).AsAsyncEnumerable();
		}
	}

	private async IAsyncEnumerable<int> FailAsync(int valuesBeforeFailure, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		try
		{
			for (int i = 0; i < valuesBeforeFailure; i++)
			{
				await Task.Yield();
				yield return i;
			}

			throw new InvalidOperationException("The sequence failed as requested.");
		}
		finally
		{
			this.FailingGeneratorDisposed.TrySetResult(true);
		}
	}

	private async IAsyncEnumerable<int> BlockAcrossBatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		try
		{
			yield return 0;
			this.BlockingBatchWaiting.TrySetResult(true);
			await this.ReleaseBlockingBatch.Task;
			yield return 1;
			this.BlockingBatchSecondMoveWaiting.TrySetResult(true);
			await this.ReleaseBlockingBatchSecondMove.Task;
			yield return 2;
		}
		finally
		{
			Volatile.Write(ref this.blockingBatchCanceled, cancellationToken.IsCancellationRequested ? 1 : 0);
			this.BlockingBatchDisposed.TrySetResult(true);
		}
	}

	private async IAsyncEnumerable<int> BlockAfterPrefetchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		try
		{
			yield return 0;
			this.PrefetchedBlockWaiting.TrySetResult(true);
			await Task.Delay(Timeout.Infinite, cancellationToken);
		}
		finally
		{
			Volatile.Write(ref this.prefetchedBlockCanceled, cancellationToken.IsCancellationRequested ? 1 : 0);
			this.PrefetchedBlockDisposed.TrySetResult(true);
		}
	}

	private async IAsyncEnumerable<int> BlockOnCompletionWhenCanceledAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		try
		{
			yield return 0;

			// Register before the cancelable wait so shutdown observes this callback while a pull is outstanding.
			// The callback is intentionally not disposed here: disposing it from finally would wait for the callback,
			// and the callback waits for connection completion.
			cancellationToken.Register(
				static state =>
				{
					AsyncEnumerableService service = (AsyncEnumerableService)state!;
					try
					{
#pragma warning disable VSTHRD002 // The callback must block synchronously; that is the shutdown hazard under test.
						service.CancellationCallbackBlocksOn!.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
					}
					catch (Exception)
					{
						// A dropped transport can fault Completion. The callback still has to return.
					}

					service.BlockingCancellationFinished.TrySetResult(true);
				},
				this);
			this.PrefetchedCancellationBlockWaiting.TrySetResult(true);
			await Task.Delay(Timeout.Infinite, cancellationToken);
		}
		finally
		{
			this.PrefetchedCancellationBlockDisposed.TrySetResult(true);
		}
	}

	private async IAsyncEnumerable<int> WaitForDisposalWhenCanceledAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		try
		{
			// Registered before the prefetch yield, so cancellation later races this callback with disposal.
			// The registration is left in place: disposing it from finally would wait on the callback.
			cancellationToken.Register(
				static state =>
				{
					AsyncEnumerableService service = (AsyncEnumerableService)state!;
					try
					{
#pragma warning disable VSTHRD002 // The callback must block synchronously; that is the disposal hazard under test.
						service.PrefetchedDisposalWaitDisposed.Task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
					}
					catch (Exception)
					{
						// Disposal can fault the wait. The callback still has to return.
					}

					service.DisposalWaitCallbackFinished.TrySetResult(true);
				},
				this);
			yield return 0;
			yield return 1;
		}
		finally
		{
			Volatile.Write(ref this.disposalWaitCanceled, cancellationToken.IsCancellationRequested ? 1 : 0);
			this.PrefetchedDisposalWaitDisposed.TrySetResult(true);
		}
	}

	private async IAsyncEnumerable<int> BlockDuringPrefetchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		try
		{
			// Registered before the blocking wait, while prefetch of the first value is still outstanding.
			// The registration is left in place: disposing it from finally would wait on the callback.
			cancellationToken.Register(
				static state =>
				{
					AsyncEnumerableService service = (AsyncEnumerableService)state!;
					try
					{
#pragma warning disable VSTHRD002 // The callback must block synchronously; that is the shutdown hazard under test.
						service.CancellationCallbackBlocksOn!.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
					}
					catch (Exception)
					{
						// A dropped transport can fault Completion. The callback still has to return.
					}

					service.BlockingCancellationFinished.TrySetResult(true);
				},
				this);
			this.PrefetchDuringBlockWaiting.TrySetResult(true);
			await Task.Delay(Timeout.Infinite, cancellationToken);
			yield return 0;
		}
		finally
		{
			this.PrefetchDuringBlockDisposed.TrySetResult(true);
		}
	}

	private async IAsyncEnumerable<int> SuspendAfterPrefetchedYieldAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		try
		{
			yield return 0;
			yield return 1;
		}
		finally
		{
			Volatile.Write(ref this.suspendedYieldCanceled, cancellationToken.IsCancellationRequested ? 1 : 0);
			this.SuspendedYieldDisposed.TrySetResult(true);
		}
	}

	private async IAsyncEnumerable<int> ProduceAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		try
		{
			for (int i = 0; i < count; i++)
			{
				await Task.Yield();
				Interlocked.Increment(ref this.generatedValues);
				yield return i;
			}
		}
		finally
		{
			Volatile.Write(ref this.generatorDisposed, true);
		}
	}

	private sealed class DisposalCallbackSequence(ICallScopedCounter counter) : IAsyncEnumerable<int>
	{
		/// <inheritdoc/>
		public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(counter);

		private sealed class Enumerator(ICallScopedCounter counter) : IAsyncEnumerator<int>
		{
			private bool yielded;
			private bool disposed;

			/// <inheritdoc/>
			public int Current => 42;

			/// <inheritdoc/>
			public ValueTask<bool> MoveNextAsync()
			{
				bool result = !this.yielded;
				this.yielded = true;
				return new(result);
			}

			/// <inheritdoc/>
			public async ValueTask DisposeAsync()
			{
				if (!this.disposed)
				{
					this.disposed = true;
					await counter.IncrementAsync(CancellationToken.None);
				}
			}
		}
	}

	private sealed class UncooperativeSequence(AsyncEnumerableService owner, bool blockMoveNext, bool blockDispose) : IAsyncEnumerable<int>
	{
		public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(owner, blockMoveNext, blockDispose);

		private sealed class Enumerator(AsyncEnumerableService owner, bool blockMoveNext, bool blockDispose) : IAsyncEnumerator<int>
		{
			private bool moved;

			public int Current => 42;

			public ValueTask<bool> MoveNextAsync()
			{
				if (blockMoveNext)
				{
					owner.UncooperativeMoveNextStarted.TrySetResult(true);
					return new(owner.ReleaseUncooperativeOperation.Task);
				}

				if (!this.moved)
				{
					this.moved = true;
					return new(true);
				}

				return new(false);
			}

			public ValueTask DisposeAsync()
			{
				owner.UncooperativeDisposeStarted.TrySetResult(true);
				return blockDispose ? new(owner.ReleaseUncooperativeOperation.Task) : default;
			}
		}
	}
}
