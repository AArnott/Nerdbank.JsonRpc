// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Net;
using System.Threading.Channels;
using Microsoft;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Provides an abstract channel for transporting JSON-RPC messages over a duplex pipe, enabling asynchronous message
/// exchange between endpoints.
/// </summary>
/// <remarks>
/// Abstract methods allow a derived class to define how messages are serialized and deserialized over the pipe,
/// and to control any framing around those messages.
/// </remarks>
public abstract class JsonRpcPipeChannel : Channel<JsonRpcMessage>, IAsyncDisposable
{
	protected static readonly MessagePackSerializer Serializer = new()
	{
		InternStrings = true,
	};

	private readonly CancellationTokenSource disposalSource = new();
	private readonly Task inboundTaskProcessor;
	private readonly Task outboundTaskProcessor;
	private readonly ChannelWriter<JsonRpcMessage> inboundMessageWriter;
	private readonly ChannelReader<JsonRpcMessage> outboundMessageReader;

	protected JsonRpcPipeChannel(IDuplexPipe pipe, Channel<JsonRpcMessage> inboundChannel, Channel<JsonRpcMessage> outboundChannel)
	{
		Requires.NotNull(pipe);
		Requires.NotNull(inboundChannel);
		Requires.NotNull(outboundChannel);

		(this.Reader, this.inboundMessageWriter) = (inboundChannel.Reader, inboundChannel.Writer);
		(this.Writer, this.outboundMessageReader) = (outboundChannel.Writer, outboundChannel.Reader);

		this.inboundTaskProcessor = this.HandleInboundMessagesAsync(pipe.Input, this.disposalSource.Token);
		this.outboundTaskProcessor = this.HandleOutboundMessagesAsync(pipe.Output, this.disposalSource.Token);
	}

	public async ValueTask DisposeAsync()
	{
#if NET
		await this.disposalSource.CancelAsync().ConfigureAwait(false);
#else
		this.disposalSource.Cancel();
#endif

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks - No main thread dependency.
		await Task.WhenAll(this.inboundTaskProcessor, this.outboundTaskProcessor).ConfigureAwait(false);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
	}

	protected static ValueTask SerializeAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken)
	{
		return message switch
		{
			JsonRpcRequest request => Serializer.SerializeAsync(writer, request, cancellationToken),
			JsonRpcResult result => Serializer.SerializeAsync(writer, result, cancellationToken),
			JsonRpcError error => Serializer.SerializeAsync(writer, error, cancellationToken),
			_ => throw new ArgumentException($"Unrecognized JSON-RPC message type: {message.GetType().FullName}", nameof(message)),
		};
	}

	protected static async ValueTask<JsonRpcMessage?> DeserializeAsync(PipeReader reader, CancellationToken cancellationToken)
	{
		Requires.NotNull(reader);

		// Read just to verify that we're not at the end of the stream.
		// Then undo the read and ask the deserializer to take over.
		ReadResult readResult = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		if (readResult.Buffer.IsEmpty && readResult.IsCompleted)
		{
			return null;
		}

		reader.AdvanceTo(readResult.Buffer.Start);

		return await Serializer.DeserializeAsync<JsonRpcMessage>(reader, cancellationToken).ConfigureAwait(false) ?? throw new ProtocolViolationException("Unexpected null value was received instead of JSON-RPC message.");
	}

	protected static Channel<JsonRpcMessage> CreateInboundChannel(int? capacity) => capacity is null
		? Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleWriter = true })
		: Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(capacity.Value) { SingleWriter = true });

	protected static Channel<JsonRpcMessage> CreateOutboundChannel(int? capacity) => capacity is null
		? Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true })
		: Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(capacity.Value) { SingleReader = true });

	protected abstract IAsyncEnumerable<JsonRpcMessage> ReceiveMessagesAsync(PipeReader reader, CancellationToken cancellationToken);

	protected abstract ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken);

	private async Task HandleInboundMessagesAsync(PipeReader reader, CancellationToken cancellationToken)
	{
		try
		{
			await foreach (JsonRpcMessage message in this.ReceiveMessagesAsync(reader, cancellationToken))
			{
				await this.inboundMessageWriter.WriteAsync(message, cancellationToken).ConfigureAwait(false);
			}

			await reader.CompleteAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			await reader.CompleteAsync(ex).ConfigureAwait(false);
		}
	}

	private async Task HandleOutboundMessagesAsync(PipeWriter writer, CancellationToken cancellationToken)
	{
		Requires.NotNull(writer);
		try
		{
			while (!this.outboundMessageReader.Completion.IsCompleted)
			{
				JsonRpcMessage msg = await this.outboundMessageReader.ReadAsync(cancellationToken).ConfigureAwait(false);
				await this.SendMessageAsync(writer, msg, cancellationToken).ConfigureAwait(false);
				await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
			}

			await writer.CompleteAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			await writer.CompleteAsync(ex).ConfigureAwait(false);
		}
	}
}
