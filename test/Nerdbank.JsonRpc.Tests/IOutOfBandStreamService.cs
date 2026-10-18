// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using System.IO.Pipelines;
using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IOutOfBandStreamService
{
	[MethodShape(Name = "countStream")]
	Task<int> CountAsync(Stream stream, CancellationToken cancellationToken);

	[MethodShape(Name = "countDuplexPipe")]
	Task<int> CountAsync(IDuplexPipe pipe, CancellationToken cancellationToken);

	[MethodShape(Name = "countPipeReader")]
	Task<int> CountAsync(PipeReader reader, CancellationToken cancellationToken);

	[MethodShape(Name = "retainPipeReader")]
	Task RetainReaderAsync(PipeReader reader, CancellationToken cancellationToken);

	[MethodShape(Name = "readRetainedPipeReader")]
	Task<int> ReadRetainedAsync(CancellationToken cancellationToken);

	[MethodShape(Name = "retainPipeWriter")]
	Task RetainWriterAsync(PipeWriter writer, CancellationToken cancellationToken);

	[MethodShape(Name = "writeRetainedPipeWriter")]
	Task WriteRetainedAsync(CancellationToken cancellationToken);

	[MethodShape(Name = "returnStream")]
	Task<Stream> ReturnStreamAsync(CancellationToken cancellationToken);

	[MethodShape(Name = "writePipeWriter")]
	Task WriteAsync(PipeWriter writer, CancellationToken cancellationToken);
}
