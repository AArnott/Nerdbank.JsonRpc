// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
		while (true)
		{
			ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			ReadOnlySequence<byte> buffer = read.Buffer;
			if (this.TryReadMessage(ref buffer, cancellationToken, out JsonRpcMessage? message))
			{
				// The envelope converter copies every retained payload, so the pipe's buffer may be released immediately.
				reader.AdvanceTo(buffer.Start);
				yield return message;
				continue;
			}

			if (read.IsCompleted)
			{
				reader.AdvanceTo(buffer.End);
				if (!buffer.IsEmpty)
				{
					throw new EndOfStreamException("The stream ended in the middle of a MessagePack structure.");
				}

				yield break;
			}

			reader.AdvanceTo(buffer.Start, buffer.End);
		}
	}

	/// <inheritdoc/>
	protected override ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken)
	{
		this.Serializer.ValidateMessage(message);
		this.messagePackSerializer.Serialize(writer, new JsonRpcMessagePackEnvelope(message), cancellationToken);
		ReleaseSingleUsePayload(message);
		return default;
	}

	private static System.Threading.Channels.Channel<JsonRpcMessage> CreateValidatedInboundChannel(int? capacity, MessagePackSerializer serializer)
	{
		if (serializer is null)
		{
			throw new ArgumentNullException(nameof(serializer));
		}

		return CreateInboundChannel(capacity);
	}

	/// <summary>Synchronously deserializes the first message if the buffer contains all of it.</summary>
	/// <param name="buffer">The buffered bytes; advanced past the message when one is read.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <param name="message">Receives the message.</param>
	/// <returns><see langword="true"/> if a complete message was read.</returns>
	private bool TryReadMessage(ref ReadOnlySequence<byte> buffer, CancellationToken cancellationToken, [NotNullWhen(true)] out JsonRpcMessage? message)
	{
		// A null refresh delegate makes an incomplete buffer report InsufficientBuffer rather than end-of-stream.
		MessagePackStreamingReader scanner = new(buffer, null, null);
		SerializationContext context = this.messagePackSerializer.StartingContext;
		switch (scanner.TrySkip(ref context))
		{
			case MessagePackPrimitives.DecodeResult.Success:
				break;
			case MessagePackPrimitives.DecodeResult.EmptyBuffer or MessagePackPrimitives.DecodeResult.InsufficientBuffer:
				message = null;
				return false;
			default:
				throw new ProtocolViolationException("Invalid MessagePack-encoded JSON-RPC message.");
		}

		ReadOnlySequence<byte> messageBytes = buffer.Slice(0, scanner.Position);
		try
		{
			message = this.messagePackSerializer.Deserialize<JsonRpcMessagePackEnvelope>(messageBytes, cancellationToken).Message;
		}
		catch (MessagePackSerializationException ex) when (ex.InnerException is ProtocolViolationException inner)
		{
			ExceptionDispatchInfo.Capture(inner).Throw();
			throw;
		}

		buffer = buffer.Slice(scanner.Position);
		return true;
	}
}
