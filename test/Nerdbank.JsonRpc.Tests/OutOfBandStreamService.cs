// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using System.IO.Pipelines;
using Nerdbank.Streams;

internal sealed class OutOfBandStreamService : IOutOfBandStreamService
{
	private PipeReader? retainedReader;
	private PipeWriter? retainedWriter;

	public Task<int> CountAsync(Stream stream, CancellationToken cancellationToken) => this.CountStreamAsync(stream, cancellationToken);

	public Task<int> CountAsync(IDuplexPipe pipe, CancellationToken cancellationToken) => CountReaderAsync(pipe.Input, cancellationToken);

	public Task<int> CountAsync(PipeReader reader, CancellationToken cancellationToken) => CountReaderAsync(reader, cancellationToken);

	public Task RetainReaderAsync(PipeReader reader, CancellationToken cancellationToken)
	{
		this.retainedReader = reader;
		return Task.CompletedTask;
	}

	public Task<int> ReadRetainedAsync(CancellationToken cancellationToken)
		=> CountReaderAsync(this.retainedReader ?? throw new InvalidOperationException("No reader has been retained."), cancellationToken);

	public Task RetainWriterAsync(PipeWriter writer, CancellationToken cancellationToken)
	{
		this.retainedWriter = writer;
		return Task.CompletedTask;
	}

	public async Task WriteRetainedAsync(CancellationToken cancellationToken)
	{
		PipeWriter writer = this.retainedWriter ?? throw new InvalidOperationException("No writer has been retained.");
		await writer.WriteAsync("after"u8.ToArray(), cancellationToken).ConfigureAwait(false);
		await writer.CompleteAsync().ConfigureAwait(false);
	}

	public Task<Stream> ReturnStreamAsync(CancellationToken cancellationToken)
	{
		Pipe pipe = new();
		Span<byte> span = pipe.Writer.GetSpan(5);
		span[0] = (byte)'h';
		span[1] = (byte)'e';
		span[2] = (byte)'l';
		span[3] = (byte)'l';
		span[4] = (byte)'o';
		pipe.Writer.Advance(5);
		pipe.Writer.Complete();
		return Task.FromResult(new DuplexPipe(pipe.Reader, pipe.Writer).AsStream());
	}

	public async Task WriteAsync(PipeWriter writer, CancellationToken cancellationToken)
	{
		await writer.WriteAsync("hello"u8.ToArray(), cancellationToken).ConfigureAwait(false);
		await writer.CompleteAsync().ConfigureAwait(false);
	}

	private static async Task<int> CountReaderAsync(PipeReader reader, CancellationToken cancellationToken)
	{
		int count = 0;
		while (true)
		{
			ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			count += checked((int)result.Buffer.Length);
			reader.AdvanceTo(result.Buffer.End);
			if (result.IsCompleted)
			{
				return count;
			}
		}
	}

	private async Task<int> CountStreamAsync(Stream stream, CancellationToken cancellationToken)
	{
		int count = 0;
		byte[] buffer = new byte[256];
		int read;
		while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
		{
			count += read;
		}

		return count;
	}
}
