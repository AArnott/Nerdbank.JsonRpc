// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IAsyncEnumerableService
{
	IAsyncEnumerable<int> GetNumbersAsync(int count, CancellationToken cancellationToken);

	/// <summary>Returns a sequence that yields once, then waits until its enumerator token is canceled.</summary>
	/// <param name="cancellationToken">A token to cancel production.</param>
	/// <returns>The sequence.</returns>
	IAsyncEnumerable<int> GetNumbersUntilCanceledAsync(CancellationToken cancellationToken);

	/// <summary>Returns a sequence that yields once, then waits without observing cancellation, then yields again.</summary>
	/// <param name="cancellationToken">A token to cancel production.</param>
	/// <returns>The sequence.</returns>
	IAsyncEnumerable<int> GetNumbersIgnoringCancellationAsync(CancellationToken cancellationToken);

	/// <summary>Returns a sequence that blocks before its second and third values, sent in batches of <paramref name="minBatchSize"/>.</summary>
	/// <param name="minBatchSize">The minimum number of values in each batch.</param>
	/// <param name="cancellationToken">A token to cancel production.</param>
	/// <returns>The sequence.</returns>
	IAsyncEnumerable<int> GetBlockingBatchAsync(int minBatchSize, CancellationToken cancellationToken);

	/// <summary>Returns a sequence that prefetches one value, then waits until its enumerator token is canceled.</summary>
	/// <param name="counter">A call-scoped callback retained for the sequence.</param>
	/// <param name="cancellationToken">A token to cancel production.</param>
	/// <returns>The sequence.</returns>
	IAsyncEnumerable<int> GetPrefetchedBlockingSequenceAsync(ICallScopedCounter counter, CancellationToken cancellationToken);

	/// <summary>
	/// Returns a sequence that prefetches one value, then waits.
	/// Canceling the enumerator runs a callback that blocks until <see cref="AsyncEnumerableService.CancellationCallbackBlocksOn"/> completes.
	/// </summary>
	/// <param name="cancellationToken">A token to cancel production.</param>
	/// <returns>The sequence.</returns>
	IAsyncEnumerable<int> GetPrefetchedSequenceWhoseCancellationBlocksAsync(CancellationToken cancellationToken);

	/// <summary>Returns a sequence that prefetches one value and then suspends at the next yield, with no further await.</summary>
	/// <param name="cancellationToken">A token to cancel production.</param>
	/// <returns>The sequence.</returns>
	IAsyncEnumerable<int> GetPrefetchedSuspendedYieldAsync(CancellationToken cancellationToken);

	IAsyncEnumerable<int> GetUncooperativeSequenceAsync(bool blockReadAhead, bool blockDispose, CancellationToken cancellationToken);

	Task<IAsyncEnumerable<int>> GetNumbersWrappedAsync(int count, CancellationToken cancellationToken);

	IAsyncEnumerable<int> GetNumbersWithSettingsAsync(int count, int minBatchSize, int maxReadAhead, int prefetch, CancellationToken cancellationToken);

	IAsyncEnumerable<string> GetWordsAsync(CancellationToken cancellationToken);

	IAsyncEnumerable<int> GetFailingSequenceAsync(int valuesBeforeFailure, CancellationToken cancellationToken);

	IAsyncEnumerable<IAsyncEnumerable<int>> GetNestedSequencesAsync(CancellationToken cancellationToken);

	/// <summary>Returns a sequence that invokes a call-scoped callback, including during disposal.</summary>
	/// <param name="counter">The callback.</param>
	/// <param name="count">The sequence length.</param>
	/// <param name="prefetch">The prefetch count.</param>
	/// <param name="readAhead">The read-ahead count.</param>
	/// <param name="fail">Whether to fail during enumeration.</param>
	/// <param name="cancellationToken">A token to cancel production.</param>
	/// <returns>The sequence.</returns>
	IAsyncEnumerable<int> UseCounterAsync(ICallScopedCounter counter, int count, int prefetch, int readAhead, bool fail, CancellationToken cancellationToken);

	/// <summary>Returns the same sequence through a task-wrapped contract.</summary>
	/// <param name="counter">The callback.</param>
	/// <param name="cancellationToken">A token to cancel production.</param>
	/// <returns>The sequences.</returns>
	Task<IAsyncEnumerable<int>> UseCounterWrappedAsync(ICallScopedCounter counter, CancellationToken cancellationToken);

	/// <summary>Returns independent sequences sharing one call-scoped callback.</summary>
	/// <param name="counter">The callback.</param>
	/// <param name="cancellationToken">A token to cancel production.</param>
	/// <returns>The sequences.</returns>
	Task<IAsyncEnumerable<int>[]> UseCounterInArrayAsync(ICallScopedCounter counter, CancellationToken cancellationToken);

	/// <summary>Returns nested sequences sharing one call-scoped callback.</summary>
	/// <param name="counter">The callback.</param>
	/// <param name="cancellationToken">A token to cancel production.</param>
	/// <returns>The sequences.</returns>
	IAsyncEnumerable<IAsyncEnumerable<int>> UseCounterInNestedSequencesAsync(ICallScopedCounter counter, CancellationToken cancellationToken);

	/// <summary>Returns a custom enumerator that calls back only from DisposeAsync.</summary>
	/// <param name="counter">The callback.</param>
	/// <param name="prefetch">The prefetch count.</param>
	/// <param name="cancellationToken">A token to cancel production.</param>
	/// <returns>The sequence.</returns>
	IAsyncEnumerable<int> UseCounterDuringDisposalAsync(ICallScopedCounter counter, int prefetch, CancellationToken cancellationToken);

	Task<int> SumAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);

	Task<int> SumTwoAsync(IAsyncEnumerable<int> first, IAsyncEnumerable<int> second, CancellationToken cancellationToken);

	Task<int> CountGeneratedValuesAsync(CancellationToken cancellationToken);

	Task<bool> IsGeneratorDisposedAsync(CancellationToken cancellationToken);

	void NotifyWithSequence(IAsyncEnumerable<int> values);
}
