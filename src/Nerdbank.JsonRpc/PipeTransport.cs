// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Net;
using Microsoft;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

public class PipeTransport : JsonRpcTransport, IAsyncDisposable
{
	private readonly CancellationTokenSource disposalSource = new();
	private readonly Task inboundTaskProcessor;
	private readonly Task outboundTaskProcessor;

	public PipeTransport(IDuplexPipe pipe, int? inboundCapacity = 100, int? outboundCapacity = null)
		: base(CreateInboundChannel(inboundCapacity), CreateOutboundChannel(outboundCapacity))
	{
		Requires.NotNull(pipe);

		this.inboundTaskProcessor = this.HandleInboundMessagesAsync(pipe.Input);
		this.outboundTaskProcessor = this.HandleOutboundMessagesAsync(pipe.Output);
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

	private async Task HandleInboundMessagesAsync(PipeReader reader)
	{
		CancellationToken disposalToken = this.disposalSource.Token;
		try
		{
			await foreach (JsonRpcMessage? msg in Serializer.DeserializeEnumerableAsync<JsonRpcMessage>(reader, disposalToken))
			{
				if (msg is null)
				{
					throw new ProtocolViolationException("Unexpected null value where a JSON-RPC message was expected.");
				}

				await this.InboundMessageWriter.WriteAsync(msg, disposalToken).ConfigureAwait(false);
			}

			await reader.CompleteAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			await reader.CompleteAsync(ex).ConfigureAwait(false);
		}
	}

	private async Task HandleOutboundMessagesAsync(PipeWriter writer)
	{
		CancellationToken disposalToken = this.disposalSource.Token;
		try
		{
			while (!this.OutboundMessageReader.Completion.IsCompleted)
			{
				JsonRpcMessage msg = await this.OutboundMessageReader.ReadAsync(disposalToken).ConfigureAwait(false);
				await SerializeAsync(writer, msg, disposalToken).ConfigureAwait(false);
				await writer.FlushAsync(disposalToken).ConfigureAwait(false);
			}

			await writer.CompleteAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			await writer.CompleteAsync(ex).ConfigureAwait(false);
		}
	}
}
