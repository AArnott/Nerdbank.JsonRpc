// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Provides an abstract channel for transporting JSON-RPC messages over a duplex pipe, enabling asynchronous message
/// exchange between endpoints.
/// </summary>
/// <remarks>
/// Derived classes select the encoding and implement serialization, deserialization, and any framing.
/// This base class manages the pipe and message queues without choosing a wire format.
/// </remarks>
public abstract class JsonRpcPipeChannel : Channel<JsonRpcMessage>, System.IAsyncDisposable
{
	/// <summary>The default maximum encoded message size, in bytes.</summary>
	internal const int DefaultMaximumMessageSize = 8 * 1024 * 1024;

	private static readonly EventId MessageSent = new(1, "Message sent");
	private static readonly EventId MessageReceived = new(2, "Message received");

	private readonly CancellationTokenSource disposalSource = new();
	private readonly CancellationTokenSource inboundDisposalSource = new();
	private readonly PipeReader pipeReader;
	private readonly ChannelWriter<JsonRpcMessage> outboundMessageWriter;
	private readonly TaskCompletionSource<bool> finalNotificationFlushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource<bool> inputCompletionAllowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource<bool> transportReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Task inboundTaskProcessor;
	private readonly Task outboundTaskProcessor;
	private readonly ChannelWriter<JsonRpcMessage> inboundMessageWriter;
	private readonly ChannelReader<JsonRpcMessage> outboundMessageReader;
	private JsonRpcMessage? finalNotification;
	private Exception? outboundFailure;
	private volatile bool finalizing;
	private bool connectionOwned;
	private volatile int maximumMessageSize = DefaultMaximumMessageSize;
	private ILogger logger = NullLogger.Instance;
	private int inboundAborted;

	/// <summary>Initializes a new instance of the <see cref="JsonRpcPipeChannel"/> class with deferred transport startup.</summary>
	/// <param name="pipe">The connected duplex pipe.</param>
	/// <param name="inboundChannel">The queue for received messages.</param>
	/// <param name="outboundChannel">The queue for messages to send.</param>
	protected JsonRpcPipeChannel(IDuplexPipe pipe, Channel<JsonRpcMessage> inboundChannel, Channel<JsonRpcMessage> outboundChannel)
	{
		Requires.NotNull(pipe);
		Requires.NotNull(inboundChannel);
		Requires.NotNull(outboundChannel);

		(this.Reader, this.inboundMessageWriter) = (inboundChannel.Reader, inboundChannel.Writer);
		(this.Writer, this.outboundMessageReader) = (outboundChannel.Writer, outboundChannel.Reader);
		this.outboundMessageWriter = outboundChannel.Writer;
		this.pipeReader = pipe.Input;

		this.inboundTaskProcessor = this.HandleInboundMessagesAsync(pipe.Input, this.inboundDisposalSource.Token);
		this.outboundTaskProcessor = this.HandleOutboundMessagesAsync(pipe.Output, this.disposalSource.Token);
	}

	/// <summary>Gets the encoding used by this transport.</summary>
	public abstract JsonRpcEncoding Encoding { get; }

	/// <summary>Gets the serializer selected by this channel for application values.</summary>
	public abstract JsonRpcSerializer Serializer { get; }

	/// <summary>Gets the configured maximum size, in bytes, of a message.</summary>
	protected int MaximumMessageSize => this.maximumMessageSize;

	protected ILogger Logger => Volatile.Read(ref this.logger);

#pragma warning disable SA1202 // Public API intentionally follows protected implementation properties.
	/// <summary>Starts transport processing after the channel and its owning <see cref="JsonRpc"/> instance have been configured.</summary>
	/// <remarks><see cref="JsonRpc.Start"/> calls this method automatically. Call it directly only when using the channel without a <see cref="JsonRpc"/> instance.</remarks>
	public void Start() => this.transportReady.TrySetResult(true);
#pragma warning restore SA1202

	/// <summary>Stops transport processing and completes both sides of the pipe.</summary>
	/// <returns>The bounded transport cleanup operation.</returns>
	/// <exception cref="OperationCanceledException">A transport processor did not stop within the one-second cleanup budget.</exception>
	public async ValueTask DisposeAsync()
	{
		try
		{
#if NET
			await this.disposalSource.CancelAsync().ConfigureAwait(false);
#else
			this.disposalSource.Cancel();
#endif
		}
		finally
		{
			this.inputCompletionAllowed.TrySetResult(true);
			try
			{
				this.AbortInbound(this.disposalSource.Token);
			}
			finally
			{
				this.Writer.TryComplete();
				this.outboundMessageWriter.TryComplete();
				this.Start();
			}
		}

		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
#pragma warning disable VSTHRD003 // These are this channel's own processors.
		await Task.WhenAll(this.inboundTaskProcessor, this.outboundTaskProcessor).WithCancellation(timeout.Token).ConfigureAwait(false);
#pragma warning restore VSTHRD003
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
			case JsonRpcMessageBatch batch:
				foreach (JsonRpcMessage entry in batch.Messages)
				{
					ReleaseSingleUsePayload(entry);
				}

				break;
		}
	}

	/// <summary>Releases owned payloads of a received message that will not be dispatched.</summary>
	/// <param name="message">The abandoned received message.</param>
	internal static void ReleaseReceivedPayload(JsonRpcMessage message)
	{
		switch (message)
		{
			case JsonRpcRequest request:
				request.Arguments.Release();
				break;
			case JsonRpcResult result:
				result.Result.Release();
				break;
			case JsonRpcMessageBatch batch:
				foreach (JsonRpcMessage entry in batch.Messages)
				{
					ReleaseReceivedPayload(entry);
				}

				break;
		}
	}

	/// <summary>Associates this channel with a connection that controls terminal cleanup.</summary>
	internal void SetConnectionOwner() => this.connectionOwned = true;

	/// <summary>Attempts to flush a final diagnostic before output is disconnected.</summary>
	/// <param name="notification">The diagnostic notification containing the full rejection message.</param>
	/// <returns>The operation, which fails if the send or flush does not finish within one second.</returns>
	internal async Task SendFinalNotificationAsync(JsonRpcRequest notification)
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
		this.finalNotification = notification;
		this.finalizing = true;
		await this.outboundMessageWriter.WriteAsync(notification, timeout.Token).ConfigureAwait(false);
#pragma warning disable VSTHRD003 // Completed by this channel's outbound processor after flushing the final notification.
		if (!await this.finalNotificationFlushed.Task.WithCancellation(timeout.Token).ConfigureAwait(false))
		{
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(this.outboundFailure!).Throw();
		}
#pragma warning restore VSTHRD003
	}

	/// <summary>Gets the configured maximum encoded message size.</summary>
	/// <returns>The maximum encoded message size in bytes.</returns>
	internal int GetMaximumMessageSize() => this.maximumMessageSize;

	/// <summary>Gets the logger used by this channel.</summary>
	/// <returns>The logger used for transport diagnostics.</returns>
	internal ILogger GetLogger() => this.Logger;

	/// <summary>Sets the logger used by this channel.</summary>
	/// <param name="value">The logger to use for transport diagnostics.</param>
	internal void SetLogger(ILogger value) => Volatile.Write(ref this.logger, Requires.NotNull(value));

	/// <summary>Completes the queue of received messages so that a consumer waiting on it without a cancellation token wakes up.</summary>
	/// <param name="cancellationToken">The canceled token that ended the consumer's interest in received messages.</param>
	/// <remarks>
	/// Channel readers reuse a cached wait operation only for waits that cannot be canceled, so the connection waits
	/// without a token and calls this method instead when it stops reading.
	/// </remarks>
	internal void AbortInbound(CancellationToken cancellationToken)
	{
		if (Interlocked.Exchange(ref this.inboundAborted, 1) != 0)
		{
			return;
		}

		this.inboundMessageWriter.TryComplete(new OperationCanceledException(cancellationToken));
		try
		{
			this.inboundDisposalSource.Cancel();
		}
		finally
		{
			this.pipeReader.CancelPendingRead();
		}
	}

	/// <summary>Updates the configured maximum encoded message size.</summary>
	/// <param name="value">The new positive maximum size, in bytes.</param>
	internal void SetMaximumMessageSize(int value)
	{
		if (value <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(value), value, "A positive size is required.");
		}

		this.maximumMessageSize = value;
	}

	protected static Channel<JsonRpcMessage> CreateInboundChannel(int? capacity) => capacity is null
		? Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleWriter = true })
		: Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(capacity.Value) { SingleWriter = true });

	protected static Channel<JsonRpcMessage> CreateOutboundChannel(int? capacity) => capacity is null
		? Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true })
		: Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(capacity.Value) { SingleReader = true });

	protected abstract IAsyncEnumerable<JsonRpcMessage> ReceiveMessagesAsync(PipeReader reader, CancellationToken cancellationToken);

	protected abstract ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken);

	private static string FormatLoggedMessage(JsonRpcMessage message, Exception? exception)
		=> $"JSON-RPC {message.GetType().Name}";

	private async Task HandleInboundMessagesAsync(PipeReader reader, CancellationToken cancellationToken)
	{
		Exception? failure = null;
		try
		{
#pragma warning disable VSTHRD003 // Waiting for this channel's own transport initialization gate.
			await this.transportReady.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			await foreach (JsonRpcMessage message in this.ReceiveMessagesAsync(reader, cancellationToken).ConfigureAwait(false))
			{
				if (Volatile.Read(ref this.inboundAborted) != 0)
				{
					ReleaseReceivedPayload(message);
					break;
				}

				this.Logger.Log(LogLevel.Information, MessageReceived, message, null, FormatLoggedMessage);
				try
				{
					await this.inboundMessageWriter.WriteAsync(message, cancellationToken).ConfigureAwait(false);
				}
				catch
				{
					ReleaseReceivedPayload(message);
					throw;
				}
			}
		}
		catch (Exception ex) when ((ex is OperationCanceledException || ex is ChannelClosedException) && Volatile.Read(ref this.inboundAborted) != 0)
		{
			// The owning connection deliberately stopped input while retaining output for its final diagnostic.
		}
		catch (Exception ex)
		{
			failure = ex;
			this.Logger.LogError(ex, "JSON-RPC inbound transport failed; encoding: {Encoding}.", this.Encoding);
			if (ex.Data["ParserException"] is Exception parserException)
			{
				this.Logger.LogError(parserException, "Underlying JSON-RPC parser failure.");
			}
		}
		finally
		{
			this.inboundMessageWriter.TryComplete(failure);
			if (failure is not null && !(this.connectionOwned && failure is System.Net.ProtocolViolationException))
			{
				this.Writer.TryComplete(failure);
				if (!this.connectionOwned)
				{
					try
					{
#if NET
						await this.disposalSource.CancelAsync().ConfigureAwait(false);
#else
						this.disposalSource.Cancel();
#endif
					}
					catch (Exception ex)
					{
						this.Logger.LogError(ex, "JSON-RPC output cancellation failed; retaining the original input cause.");
					}
				}
			}

			try
			{
				if (this.connectionOwned)
				{
#pragma warning disable VSTHRD003 // The owning connection allows input completion after its diagnostic flush attempt.
					await this.inputCompletionAllowed.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
				}

				await reader.CompleteAsync(this.connectionOwned ? null : failure).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				this.Logger.LogError(ex, "JSON-RPC input cleanup failed; retaining the original transport cause.");
			}
		}
	}

	private async Task HandleOutboundMessagesAsync(PipeWriter writer, CancellationToken cancellationToken)
	{
		Requires.NotNull(writer);
		Exception? failure = null;
		try
		{
#pragma warning disable VSTHRD003 // Waiting for this channel's own transport initialization gate.
			await this.transportReady.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			while (await this.outboundMessageReader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
			{
				while (this.outboundMessageReader.TryRead(out JsonRpcMessage? message))
				{
					if (cancellationToken.IsCancellationRequested || (this.finalizing && !ReferenceEquals(message, this.finalNotification)))
					{
						ReleaseSingleUsePayload(message);
						continue;
					}

					try
					{
						await this.SendMessageAsync(writer, message, cancellationToken).ConfigureAwait(false);
						FlushResult flush = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
						if (flush.IsCanceled)
						{
							throw new OperationCanceledException("JSON-RPC output flush was canceled.", cancellationToken);
						}

						if (flush.IsCompleted)
						{
							throw new EndOfStreamException("The JSON-RPC peer stopped reading output.");
						}

						this.Logger.Log(LogLevel.Information, MessageSent, message, null, FormatLoggedMessage);
						if (ReferenceEquals(message, this.finalNotification))
						{
							this.finalNotificationFlushed.TrySetResult(true);
						}
					}
					catch
					{
						ReleaseSingleUsePayload(message);
						throw;
					}
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			failure = ex;
			this.Logger.LogError(ex, "JSON-RPC outbound transport failed; encoding: {Encoding}.", this.Encoding);
			this.inboundMessageWriter.TryComplete(ex);
			this.Writer.TryComplete(ex);
			this.outboundMessageWriter.TryComplete(ex);
			this.outboundFailure = ex;
			this.finalNotificationFlushed.TrySetResult(false);
			try
			{
				this.AbortInbound(new CancellationToken(canceled: true));
			}
			catch (Exception cleanupException)
			{
				this.Logger.LogError(cleanupException, "JSON-RPC input cancellation failed; retaining the original output cause.");
			}
		}
		finally
		{
			while (this.outboundMessageReader.TryRead(out JsonRpcMessage? abandoned))
			{
				ReleaseSingleUsePayload(abandoned);
			}

			try
			{
				await writer.CompleteAsync(failure).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				this.Logger.LogError(ex, "JSON-RPC output cleanup failed; retaining the original transport cause.");
			}
		}
	}
}
