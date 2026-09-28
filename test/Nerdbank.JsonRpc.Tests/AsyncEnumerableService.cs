// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;

internal sealed class AsyncEnumerableService : IAsyncEnumerableService
{
	private int generatedValues;
	private bool generatorDisposed;

	/// <summary>Gets the callback retained by the last sequence-producing call.</summary>
	internal ICallScopedCounter? LastCounter { get; private set; }

	public IAsyncEnumerable<int> GetNumbersAsync(int count, CancellationToken cancellationToken) => this.ProduceAsync(count, cancellationToken);

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
		=> FailAsync(valuesBeforeFailure, cancellationToken);

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

	private static async IAsyncEnumerable<int> FailAsync(int valuesBeforeFailure, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		for (int i = 0; i < valuesBeforeFailure; i++)
		{
			await Task.Yield();
			yield return i;
		}

		throw new InvalidOperationException("The sequence failed as requested.");
	}

	private static async IAsyncEnumerable<IAsyncEnumerable<int>> ProduceNestedAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		for (int i = 0; i < 2; i++)
		{
			await Task.Yield();
			yield return Enumerable.Range(i * 10, 3).AsAsyncEnumerable();
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
}
