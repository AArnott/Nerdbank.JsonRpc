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
/// Encodes JSON-RPC messages as a stream of self-delimiting MessagePack values over a duplex pipe,
/// without any additional headers or framing.
/// </summary>
public class JsonRpcMessagePackChannel : JsonRpcPipeChannel
{
	private static readonly MessagePackSerializer Serializer = new() { InternStrings = true };

	/// <summary>Initializes a new instance of the <see cref="JsonRpcMessagePackChannel"/> class.</summary>
	/// <param name="pipe">The connected duplex pipe.</param>
	/// <param name="logger">The transport logger.</param>
	/// <param name="inboundCapacity">The inbound queue limit, or null for an unbounded queue.</param>
	/// <param name="outboundCapacity">The outbound queue limit, or null for an unbounded queue.</param>
	public JsonRpcMessagePackChannel(IDuplexPipe pipe, ILogger logger, int? inboundCapacity = 100, int? outboundCapacity = null)
		: base(pipe, CreateInboundChannel(inboundCapacity), CreateOutboundChannel(outboundCapacity), logger)
	{
	}

	/// <inheritdoc/>
	public override JsonRpcEncoding Encoding => JsonRpcEncoding.MessagePack;

	/// <inheritdoc/>
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

	/// <inheritdoc/>
	protected override ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken)
	{
		return message switch
		{
			JsonRpcRequest request => Serializer.SerializeAsync(writer, request, cancellationToken),
			JsonRpcResult result => Serializer.SerializeAsync(writer, result, cancellationToken),
			JsonRpcError error => Serializer.SerializeAsync(writer, error, cancellationToken),
			JsonRpcMessageBatch batch => Serializer.SerializeAsync<JsonRpcMessage>(writer, batch, cancellationToken),
			_ => throw new ArgumentException($"Unrecognized JSON-RPC message type: {message.GetType().FullName}", nameof(message)),
		};
	}
}
