// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft;
using Microsoft.Extensions.Logging;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Provides a JSON-RPC message channel that streams messages over a duplex pipe
/// without any headers or framing added.
/// </summary>
public class StreamingJsonRpcMessageChannel : JsonRpcPipeChannel
{
	public StreamingJsonRpcMessageChannel(IDuplexPipe pipe, ILogger logger, int? inboundCapacity = 100, int? outboundCapacity = null)
		: base(pipe, CreateInboundChannel(inboundCapacity), CreateOutboundChannel(outboundCapacity), logger)
	{
	}

	protected override async IAsyncEnumerable<JsonRpcMessage> ReceiveMessagesAsync(PipeReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		Requires.NotNull(reader);
		await foreach (JsonRpcMessage? msg in Serializer.DeserializeEnumerableAsync<JsonRpcMessage>(reader, cancellationToken))
		{
			if (msg is null)
			{
				throw new ProtocolViolationException("Unexpected null value where a JSON-RPC message was expected.");
			}

			yield return msg;
		}
	}

	protected override ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken)
	{
		return SerializeAsync(writer, message, cancellationToken);
	}
}
