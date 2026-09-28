// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IAsyncEnumerableService
{
	IAsyncEnumerable<int> GetNumbersAsync(int count, CancellationToken cancellationToken);

	Task<IAsyncEnumerable<int>> GetNumbersWrappedAsync(int count, CancellationToken cancellationToken);

	IAsyncEnumerable<int> GetNumbersWithSettingsAsync(int count, int minBatchSize, int maxReadAhead, int prefetch, CancellationToken cancellationToken);

	IAsyncEnumerable<string> GetWordsAsync(CancellationToken cancellationToken);

	IAsyncEnumerable<int> GetFailingSequenceAsync(int valuesBeforeFailure, CancellationToken cancellationToken);

	Task<int> SumAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);

	Task<int> SumTwoAsync(IAsyncEnumerable<int> first, IAsyncEnumerable<int> second, CancellationToken cancellationToken);

	Task<int> CountGeneratedValuesAsync(CancellationToken cancellationToken);

	Task<bool> IsGeneratorDisposedAsync(CancellationToken cancellationToken);

	void NotifyWithSequence(IAsyncEnumerable<int> values);
}
