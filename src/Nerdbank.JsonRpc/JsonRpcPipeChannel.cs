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

	protected JsonRpcPipeChannel(IDuplexPipe pipe, Channel<JsonRpcMessage> inboundChannel, Channel<JsonRpcMessage> outboundChannel, ILogger logger, bool startImmediately = true)
	{
		Requires.NotNull(pipe);
		Requires.NotNull(inboundChannel);
		Requires.NotNull(outboundChannel);

		this.Logger = logger;

		(this.Reader, this.inboundMessageWriter) = (inboundChannel.Reader, inboundChannel.Writer);
		(this.Writer, this.outboundMessageReader) = (outboundChannel.Writer, outboundChannel.Reader);

		this.inboundTaskProcessor = this.HandleInboundMessagesAsync(pipe.Input, this.disposalSource.Token);
		this.outboundTaskProcessor = this.HandleOutboundMessagesAsync(pipe.Output, this.disposalSource.Token);
		if (startImmediately)
		{
			this.StartTransport();
		}
	}

	/// <summary>Gets the encoding used by this transport.</summary>
	public abstract JsonRpcEncoding Encoding { get; }

	/// <summary>Gets the plugin bound to the transport, if it requires a particular instance.</summary>
	public virtual JsonRpcSerializer? SerializerPlugin => null;

	protected ILogger Logger { get; }

	public async ValueTask DisposeAsync()
	{
#if NET
		await this.disposalSource.CancelAsync().ConfigureAwait(false);
#else
		this.disposalSource.Cancel();
#endif
		this.StartTransport();

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks - No main thread dependency.
		await Task.WhenAll(this.inboundTaskProcessor, this.outboundTaskProcessor).ConfigureAwait(false);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
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
			this.Logger.LogError(ex, "JSON-RPC inbound transport failed.");
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
				JsonRpcMessage message = await this.outboundMessageReader.ReadAsync(cancellationToken).ConfigureAwait(false);
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
