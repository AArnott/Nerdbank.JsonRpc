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
	private readonly MessagePackSerializer messagePackSerializer;

	/// <summary>Initializes a new instance of the <see cref="JsonRpcMessagePackChannel"/> class with the default serializer.</summary>
	/// <param name="pipe">The connected duplex pipe.</param>
	/// <param name="logger">The transport logger.</param>
	/// <param name="inboundCapacity">The inbound queue limit, or null for an unbounded queue.</param>
	/// <param name="outboundCapacity">The outbound queue limit, or null for an unbounded queue.</param>
	public JsonRpcMessagePackChannel(IDuplexPipe pipe, ILogger logger, int? inboundCapacity = 100, int? outboundCapacity = null)
		: this(pipe, logger, DefaultSerializer, inboundCapacity, outboundCapacity)
	{
	}

	/// <summary>Initializes a new instance of the <see cref="JsonRpcMessagePackChannel"/> class with a configured serializer.</summary>
	/// <param name="pipe">The connected duplex pipe.</param>
	/// <param name="logger">The transport logger.</param>
	/// <param name="serializer">The serializer for MessagePack application values and envelopes.</param>
	/// <param name="inboundCapacity">The inbound queue limit, or null for an unbounded queue.</param>
	/// <param name="outboundCapacity">The outbound queue limit, or null for an unbounded queue.</param>
	public JsonRpcMessagePackChannel(IDuplexPipe pipe, ILogger logger, MessagePackSerializer serializer, int? inboundCapacity = 100, int? outboundCapacity = null)
		: base(pipe, CreateValidatedInboundChannel(inboundCapacity, serializer), CreateOutboundChannel(outboundCapacity), logger)
	{
		this.messagePackSerializer = serializer;
		this.Serializer = new MessagePackSerializerPlugin(this.messagePackSerializer);
		this.StartTransport();
	}

	/// <summary>Gets the default serializer for MessagePack channels.</summary>
	public static MessagePackSerializer DefaultSerializer { get; } = new() { InternStrings = true };

	/// <inheritdoc/>
	public override JsonRpcEncoding Encoding => JsonRpcEncoding.MessagePack;

	/// <inheritdoc/>
	public override JsonRpcSerializer Serializer { get; }

	/// <inheritdoc/>
	protected override async IAsyncEnumerable<JsonRpcMessage> ReceiveMessagesAsync(PipeReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		Requires.NotNull(reader);
		await foreach (JsonRpcMessagePackEnvelope envelope in this.messagePackSerializer.DeserializeEnumerableAsync<JsonRpcMessagePackEnvelope>(reader, cancellationToken))
		{
			yield return envelope.Message;
		}
	}

	/// <inheritdoc/>
	protected override ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken)
	{
		this.Serializer.ValidateMessage(message);
		return this.messagePackSerializer.SerializeAsync(writer, new JsonRpcMessagePackEnvelope(message), cancellationToken);
	}

	private static System.Threading.Channels.Channel<JsonRpcMessage> CreateValidatedInboundChannel(int? capacity, MessagePackSerializer serializer)
	{
		if (serializer is null)
		{
			throw new ArgumentNullException(nameof(serializer));
		}

		return CreateInboundChannel(capacity);
	}
}
