// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;

internal sealed class AsyncEnumerableService : IAsyncEnumerableService
{
	private int generatedValues;
	private bool generatorDisposed;

	public Task<IAsyncEnumerable<int>> GetNumbersAsync(int count, CancellationToken cancellationToken)
		=> Task.FromResult(this.ProduceAsync(count, cancellationToken));

	public IAsyncEnumerable<int> GetNumbersDirect(int count, CancellationToken cancellationToken) => this.ProduceAsync(count, cancellationToken);

	public Task<IAsyncEnumerable<int>> GetNumbersWithSettingsAsync(int count, int minBatchSize, int maxReadAhead, int prefetch, CancellationToken cancellationToken)
		=> Task.FromResult(this.ProduceAsync(count, cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings
		{
			MinBatchSize = minBatchSize,
			MaxReadAhead = maxReadAhead,
			Prefetch = prefetch,
		}));

	public Task<IAsyncEnumerable<string>> GetWordsAsync(CancellationToken cancellationToken)
		=> Task.FromResult(new[] { "alpha", "beta", "gamma" }.AsAsyncEnumerable());

	public Task<IAsyncEnumerable<int>> GetFailingSequenceAsync(int valuesBeforeFailure, CancellationToken cancellationToken)
		=> Task.FromResult(FailAsync(valuesBeforeFailure));

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

	private static async IAsyncEnumerable<int> FailAsync(int valuesBeforeFailure, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		for (int i = 0; i < valuesBeforeFailure; i++)
		{
			await Task.Yield();
			yield return i;
		}

		throw new InvalidOperationException("The sequence failed as requested.");
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
}
