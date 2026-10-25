// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using PolyType;

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ILocalBoundaryService
{
	Task<PipeReader> GetReaderAsync(CancellationToken cancellationToken = default);

	Task<PipeWriter> GetWriterAsync(CancellationToken cancellationToken = default);

	Task<IDuplexPipe> GetPipeAsync(CancellationToken cancellationToken = default);

	IAsyncEnumerable<int> EnumerateAsync(int failure, CancellationToken cancellationToken = default);

	IAsyncEnumerable<int> EnumerateWithObserverAsync(IObserver<int> observer, int failure, CancellationToken cancellationToken = default);
}
