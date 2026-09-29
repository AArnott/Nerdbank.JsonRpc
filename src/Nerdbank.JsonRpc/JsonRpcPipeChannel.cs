// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft;
using Microsoft.Extensions.Logging;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Provides an abstract channel for transporting JSON-RPC messages over a duplex pipe, enabling asynchronous message
/// exchange between endpoints.
/// </summary>
/// <remarks>
/// Derived classes select the encoding and implement serialization, deserialization, and any framing.
/// This base class manages the pipe and message queues without choosing a wire format.
/// </remarks>
public abstract class JsonRpcPipeChannel : Channel<JsonRpcMessage>, IAsyncDisposable
{
	private static readonly EventId MessageSent = new(1, "Message sent");
	private static readonly EventId MessageReceived = new(2, "Message received");

	private readonly CancellationTokenSource disposalSource = new();
	private readonly TaskCompletionSource<bool> transportReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Task inboundTaskProcessor;
	private readonly Task outboundTaskProcessor;
	private readonly ChannelWriter<JsonRpcMessage> inboundMessageWriter;
	private readonly ChannelReader<JsonRpcMessage> outboundMessageReader;
	private volatile bool inboundAborted;

	/// <summary>Initializes a new instance of the <see cref="JsonRpcPipeChannel"/> class with deferred transport startup.</summary>
	/// <param name="pipe">The connected duplex pipe.</param>
	/// <param name="inboundChannel">The queue for received messages.</param>
	/// <param name="outboundChannel">The queue for messages to send.</param>
	/// <param name="logger">The transport logger.</param>
	protected JsonRpcPipeChannel(IDuplexPipe pipe, Channel<JsonRpcMessage> inboundChannel, Channel<JsonRpcMessage> outboundChannel, ILogger logger)
	{
		Requires.NotNull(pipe);
		Requires.NotNull(inboundChannel);
		Requires.NotNull(outboundChannel);

		this.Logger = logger;

		(this.Reader, this.inboundMessageWriter) = (inboundChannel.Reader, inboundChannel.Writer);
		(this.Writer, this.outboundMessageReader) = (outboundChannel.Writer, outboundChannel.Reader);

		this.inboundTaskProcessor = this.HandleInboundMessagesAsync(pipe.Input, this.disposalSource.Token);
		this.outboundTaskProcessor = this.HandleOutboundMessagesAsync(pipe.Output, this.disposalSource.Token);
	}

	/// <summary>Gets the encoding used by this transport.</summary>
	public abstract JsonRpcEncoding Encoding { get; }

	/// <summary>Gets the serializer selected by this channel for application values.</summary>
	public abstract JsonRpcSerializer Serializer { get; }

	protected ILogger Logger { get; }

	public async ValueTask DisposeAsync()
	{
#if NET
		await this.disposalSource.CancelAsync().ConfigureAwait(false);
#else
		this.disposalSource.Cancel();
#endif

		// The outbound queue is read without a cancellation token (see HandleOutboundMessagesAsync), so completing it is what wakes that reader.
		this.Writer.TryComplete(new OperationCanceledException(this.disposalSource.Token));
		this.StartTransport();

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks - No main thread dependency.
		await Task.WhenAll(this.inboundTaskProcessor, this.outboundTaskProcessor).ConfigureAwait(false);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
	}

	/// <summary>Returns single-use pooled buffers to the pool once the message that carries them has been serialized.</summary>
	/// <param name="message">The serialized message, whose single-use payloads must not be used again.</param>
	internal static void ReleaseSingleUsePayload(JsonRpcMessage message)
	{
		switch (message)
		{
			case JsonRpcRequest request:
				request.Arguments.ReleaseIfSingleUse();
				break;
			case JsonRpcResult result:
				result.Result.ReleaseIfSingleUse();
				break;
		}
	}

	/// <summary>Completes the queue of received messages so that a consumer waiting on it without a cancellation token wakes up.</summary>
	/// <param name="cancellationToken">The canceled token that ended the consumer's interest in received messages.</param>
	/// <remarks>
	/// Channel readers reuse a cached wait operation only for waits that cannot be canceled, so the connection waits
	/// without a token and calls this method instead when it stops reading.
	/// </remarks>
	internal void AbortInbound(CancellationToken cancellationToken)
	{
		this.inboundAborted = true;
		this.inboundMessageWriter.TryComplete(new OperationCanceledException(cancellationToken));
	}

	protected static Channel<JsonRpcMessage> CreateInboundChannel(int? capacity) => capacity is null
		? Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleWriter = true })
		: Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(capacity.Value) { SingleWriter = true });

	protected static Channel<JsonRpcMessage> CreateOutboundChannel(int? capacity) => capacity is null
		? Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true })
		: Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(capacity.Value) { SingleReader = true });

	/// <summary>Starts transport processing after a derived channel has initialized its framing.</summary>
	protected void StartTransport() => this.transportReady.TrySetResult(true);

	protected abstract IAsyncEnumerable<JsonRpcMessage> ReceiveMessagesAsync(PipeReader reader, CancellationToken cancellationToken);

	protected abstract ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken);

	private static string FormatLoggedMessage(JsonRpcMessage message, Exception? exception)
		=> $"JSON-RPC {message.GetType().Name}";

	private async Task HandleInboundMessagesAsync(PipeReader reader, CancellationToken cancellationToken)
	{
		try
		{
#pragma warning disable VSTHRD003 // Waiting for this channel's own transport initialization gate.
			await this.transportReady.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			await foreach (JsonRpcMessage message in this.ReceiveMessagesAsync(reader, cancellationToken))
			{
				this.Logger.Log(LogLevel.Information, MessageReceived, message, null, FormatLoggedMessage);
				await this.inboundMessageWriter.WriteAsync(message, cancellationToken).ConfigureAwait(false);
			}

			this.inboundMessageWriter.TryComplete();
			await reader.CompleteAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			if (!(ex is ChannelClosedException && this.inboundAborted))
			{
				this.Logger.LogError(ex, "JSON-RPC inbound transport failed.");
			}

			this.inboundMessageWriter.TryComplete(ex);
			this.Writer.TryComplete(ex);
#if NET
			await this.disposalSource.CancelAsync().ConfigureAwait(false);
#else
			this.disposalSource.Cancel();
#endif
			await reader.CompleteAsync(ex).ConfigureAwait(false);
		}
	}

	private async Task HandleOutboundMessagesAsync(PipeWriter writer, CancellationToken cancellationToken)
	{
		Requires.NotNull(writer);
		try
		{
#pragma warning disable VSTHRD003 // Waiting for this channel's own transport initialization gate.
			await this.transportReady.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			while (!this.outboundMessageReader.Completion.IsCompleted)
			{
				// Reading without a token lets the channel reuse its cached read operation; disposal completes the queue instead.
				JsonRpcMessage message = await this.outboundMessageReader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
				cancellationToken.ThrowIfCancellationRequested();
				await this.SendMessageAsync(writer, message, cancellationToken).ConfigureAwait(false);
				await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
				this.Logger.Log(LogLevel.Information, MessageSent, message, null, FormatLoggedMessage);
			}

			await writer.CompleteAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			this.Logger.LogError(ex, "JSON-RPC outbound transport failed.");
			this.inboundMessageWriter.TryComplete(ex);
			this.Writer.TryComplete(ex);
#if NET
			await this.disposalSource.CancelAsync().ConfigureAwait(false);
#else
			this.disposalSource.Cancel();
#endif
			await writer.CompleteAsync(ex).ConfigureAwait(false);
		}
	}
}
