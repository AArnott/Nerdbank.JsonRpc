// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;

public class OutOfBandStreamTests
{
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task StreamArgumentTransfersOutOfBand(JsonRpcEncoding encoding)
	{
		(IDuplexPipe firstTransport, IDuplexPipe secondTransport) = FullDuplexStream.CreatePipePair();
		Task<MultiplexingStream> firstMultiplexerTask = MultiplexingStream.CreateAsync(firstTransport.AsStream());
		Task<MultiplexingStream> secondMultiplexerTask = MultiplexingStream.CreateAsync(secondTransport.AsStream());
		await Task.WhenAll(firstMultiplexerTask, secondMultiplexerTask);
		using MultiplexingStream firstMultiplexer = await firstMultiplexerTask;
		using MultiplexingStream secondMultiplexer = await secondMultiplexerTask;
		Task<MultiplexingStream.Channel> secondRpcChannelTask = secondMultiplexer.AcceptChannelAsync(string.Empty, CancellationToken.None);
		MultiplexingStream.Channel firstRpcChannel = firstMultiplexer.CreateChannel();
		MultiplexingStream.Channel secondRpcChannel = await secondRpcChannelTask;
		using JsonRpc firstRpc = new(CreateChannel(firstRpcChannel, encoding)) { MultiplexingStream = firstMultiplexer };
		using JsonRpc secondRpc = new(CreateChannel(secondRpcChannel, encoding)) { MultiplexingStream = secondMultiplexer };
		secondRpc.AddRpcTarget<IOutOfBandStreamService>(new OutOfBandStreamService());
		firstRpc.Start();
		secondRpc.Start();
		IOutOfBandStreamService client = firstRpc.Attach<IOutOfBandStreamService>();
		Pipe content = new();
		await content.Writer.WriteAsync("hello"u8.ToArray());
		await content.Writer.CompleteAsync();

		Assert.Equal(5, await client.CountAsync(new DuplexPipe(content.Reader, content.Writer).AsStream(), CancellationToken.None));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task DuplexPipeAndPipeReaderArgumentsTransferOutOfBand(JsonRpcEncoding encoding)
	{
		await using RpcPair pair = await RpcPair.CreateAsync(encoding);
		IOutOfBandStreamService client = pair.Client.Attach<IOutOfBandStreamService>();
		Pipe duplexContent = new();
		await duplexContent.Writer.WriteAsync("hello"u8.ToArray());
		await duplexContent.Writer.CompleteAsync();
		Assert.Equal(5, await client.CountAsync(new DuplexPipe(duplexContent.Reader, duplexContent.Writer), CancellationToken.None));

		Pipe readerContent = new();
		await readerContent.Writer.WriteAsync("world"u8.ToArray());
		await readerContent.Writer.CompleteAsync();
		Assert.Equal(5, await client.CountAsync(readerContent.Reader, CancellationToken.None));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PipeWriterArgumentTransfersOutOfBand(JsonRpcEncoding encoding)
	{
		await using RpcPair pair = await RpcPair.CreateAsync(encoding);
		IOutOfBandStreamService client = pair.Client.Attach<IOutOfBandStreamService>();
		Pipe pipe = new();

		await client.WriteAsync(pipe.Writer, CancellationToken.None);
		ReadResult result = await pipe.Reader.ReadAsync();
		Assert.Equal("hello"u8.ToArray(), result.Buffer.ToArray());
		pipe.Reader.AdvanceTo(result.Buffer.End);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PipeReaderRemainsUsableAfterRpcMethodReturns(JsonRpcEncoding encoding)
	{
		await using RpcPair pair = await RpcPair.CreateAsync(encoding);
		IOutOfBandStreamService client = pair.Client.Attach<IOutOfBandStreamService>();
		Pipe pipe = new();

		await client.RetainReaderAsync(pipe.Reader, CancellationToken.None);
		await pipe.Writer.WriteAsync("after"u8.ToArray());
		await pipe.Writer.CompleteAsync();

		Assert.Equal(5, await client.ReadRetainedAsync(CancellationToken.None));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PipeWriterRemainsUsableAfterRpcMethodReturns(JsonRpcEncoding encoding)
	{
		await using RpcPair pair = await RpcPair.CreateAsync(encoding);
		IOutOfBandStreamService client = pair.Client.Attach<IOutOfBandStreamService>();
		Pipe pipe = new();

		await client.RetainWriterAsync(pipe.Writer, CancellationToken.None);
		await client.WriteRetainedAsync(CancellationToken.None);

		List<byte> received = [];
		ReadResult result;
		do
		{
			result = await pipe.Reader.ReadAsync();
			received.AddRange(result.Buffer.ToArray());
			pipe.Reader.AdvanceTo(result.Buffer.End);
		}
		while (!result.IsCompleted);

		Assert.Equal("after"u8.ToArray(), received.ToArray());
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task StreamReturnValueTransfersOutOfBand(JsonRpcEncoding encoding)
	{
		await using RpcPair pair = await RpcPair.CreateAsync(encoding);
		IOutOfBandStreamService client = pair.Client.Attach<IOutOfBandStreamService>();
		using Stream stream = await client.ReturnStreamAsync(CancellationToken.None);
		byte[] buffer = new byte[5];
		Assert.Equal(5, await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None));
		Assert.Equal("hello"u8.ToArray(), buffer);
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
		=> encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, NullLogger.Instance)
			: new JsonRpcMessagePackChannel(pipe, NullLogger.Instance);

	private sealed class RpcPair(MultiplexingStream clientMultiplexer, MultiplexingStream serverMultiplexer, JsonRpc client, JsonRpc server) : IAsyncDisposable
	{
		internal JsonRpc Client { get; } = client;

		public async ValueTask DisposeAsync()
		{
			this.Client.Dispose();
			server.Dispose();
			await clientMultiplexer.DisposeAsync();
			await serverMultiplexer.DisposeAsync();
		}

		internal static async Task<RpcPair> CreateAsync(JsonRpcEncoding encoding)
		{
			(IDuplexPipe clientTransport, IDuplexPipe serverTransport) = FullDuplexStream.CreatePipePair();
			Task<MultiplexingStream> clientMultiplexerTask = MultiplexingStream.CreateAsync(clientTransport.AsStream());
			Task<MultiplexingStream> serverMultiplexerTask = MultiplexingStream.CreateAsync(serverTransport.AsStream());
			await Task.WhenAll(clientMultiplexerTask, serverMultiplexerTask);
			MultiplexingStream clientMultiplexer = await clientMultiplexerTask;
			MultiplexingStream serverMultiplexer = await serverMultiplexerTask;
			try
			{
				Task<MultiplexingStream.Channel> serverRpcChannelTask = serverMultiplexer.AcceptChannelAsync(string.Empty, CancellationToken.None);
				MultiplexingStream.Channel clientRpcChannel = clientMultiplexer.CreateChannel();
				MultiplexingStream.Channel serverRpcChannel = await serverRpcChannelTask;
				JsonRpc client = new(CreateChannel(clientRpcChannel, encoding)) { MultiplexingStream = clientMultiplexer };
				JsonRpc server = new(CreateChannel(serverRpcChannel, encoding)) { MultiplexingStream = serverMultiplexer };
				server.AddRpcTarget<IOutOfBandStreamService>(new OutOfBandStreamService());
				client.Start();
				server.Start();
				return new(clientMultiplexer, serverMultiplexer, client, server);
			}
			catch
			{
				await clientMultiplexer.DisposeAsync();
				await serverMultiplexer.DisposeAsync();
				throw;
			}
		}
	}
}
