// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IAsyncEnumerableService
{
	IAsyncEnumerable<int> GetNumbersAsync(int count, CancellationToken cancellationToken);

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
