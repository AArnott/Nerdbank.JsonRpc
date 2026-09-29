// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft;
using Microsoft.Extensions.Logging;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Encodes JSON-RPC messages as MessagePack over a duplex pipe, framed as described by <see cref="JsonRpcMessagePackFraming"/>.
/// Inbound frames are limited by <see cref="JsonRpc.MaximumMessageSize"/>.
/// </summary>
public class JsonRpcMessagePackChannel : JsonRpcPipeChannel
{
	/// <summary>The framing used when none is specified.</summary>
	public const JsonRpcMessagePackFraming DefaultFraming = JsonRpcMessagePackFraming.BigEndianInt32LengthHeader;

	private const int LengthHeaderSize = 4;

	private readonly MessagePackSerializer messagePackSerializer;

	private readonly JsonRpcMessagePackFraming framing;

	/// <summary>The reusable writer that reserves and backfills each message's length header.</summary>
	private PrefixingBufferWriter<byte>? prefixingWriter;

	/// <summary>The pipe writer that <see cref="prefixingWriter"/> wraps.</summary>
	private PipeWriter? prefixingWriterTarget;

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
		: this(pipe, logger, serializer, DefaultFraming, inboundCapacity, outboundCapacity)
	{
	}

	/// <summary>Initializes a new instance of the <see cref="JsonRpcMessagePackChannel"/> class with a configured serializer and framing.</summary>
	/// <param name="pipe">The connected duplex pipe.</param>
	/// <param name="logger">The transport logger.</param>
	/// <param name="serializer">The serializer for MessagePack application values and envelopes.</param>
	/// <param name="framing">The wire framing convention, which both parties must agree on.</param>
	/// <param name="inboundCapacity">The inbound queue limit, or null for an unbounded queue.</param>
	/// <param name="outboundCapacity">The outbound queue limit, or null for an unbounded queue.</param>
	public JsonRpcMessagePackChannel(IDuplexPipe pipe, ILogger logger, MessagePackSerializer serializer, JsonRpcMessagePackFraming framing, int? inboundCapacity = 100, int? outboundCapacity = null)
		: base(pipe, CreateValidatedInboundChannel(inboundCapacity, serializer, framing), CreateOutboundChannel(outboundCapacity), logger)
	{
		this.messagePackSerializer = serializer;
		this.framing = framing;
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

			if (this.framing == JsonRpcMessagePackFraming.SelfDelimiting && buffer.Length > this.MaximumMessageSize)
			{
				throw new ProtocolViolationException("MessagePack frame exceeds the size limit.");
			}

			if (read.IsCompleted)
			{
				reader.AdvanceTo(buffer.End);
				if (!buffer.IsEmpty)
				{
					throw new EndOfStreamException("The stream ended in the middle of a MessagePack message.");
				}

				yield break;
			}

			reader.AdvanceTo(buffer.Start, buffer.End);
		}
	}

	/// <inheritdoc/>
	protected override ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken)
	{
		Requires.NotNull(writer);
		this.Serializer.ValidateMessage(message);
		JsonRpcMessagePackEnvelope envelope = new(message);
		if (this.framing == JsonRpcMessagePackFraming.BigEndianInt32LengthHeader)
		{
			if (!ReferenceEquals(this.prefixingWriterTarget, writer))
			{
				this.prefixingWriter = new PrefixingBufferWriter<byte>(writer, LengthHeaderSize);
				this.prefixingWriterTarget = writer;
			}

			PrefixingBufferWriter<byte> prefixingWriter = this.prefixingWriter!;
			this.messagePackSerializer.Serialize(prefixingWriter, envelope, cancellationToken);
			BinaryPrimitives.WriteUInt32BigEndian(prefixingWriter.Prefix.Span, checked((uint)prefixingWriter.Length));
			prefixingWriter.Commit();
		}
		else
		{
			this.messagePackSerializer.Serialize(writer, envelope, cancellationToken);
		}

		ReleaseSingleUsePayload(message);
		return default;
	}

	private static System.Threading.Channels.Channel<JsonRpcMessage> CreateValidatedInboundChannel(int? capacity, MessagePackSerializer serializer, JsonRpcMessagePackFraming framing)
	{
		if (serializer is null)
		{
			throw new ArgumentNullException(nameof(serializer));
		}

		if (framing is not (JsonRpcMessagePackFraming.SelfDelimiting or JsonRpcMessagePackFraming.BigEndianInt32LengthHeader))
		{
			throw new ArgumentOutOfRangeException(nameof(framing));
		}

		return CreateInboundChannel(capacity);
	}

	/// <summary>Finds the bytes of the first message if the buffer contains all of it.</summary>
	/// <param name="buffer">The buffered bytes.</param>
	/// <param name="message">Receives the message's MessagePack structure, excluding any header.</param>
	/// <returns><see langword="true"/> if a complete message is buffered.</returns>
	private bool TryFindMessage(ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> message)
	{
		if (this.framing == JsonRpcMessagePackFraming.BigEndianInt32LengthHeader)
		{
			if (buffer.Length < LengthHeaderSize)
			{
				message = default;
				return false;
			}

			uint length;
			if (buffer.First.Span.Length >= LengthHeaderSize)
			{
				length = BinaryPrimitives.ReadUInt32BigEndian(buffer.First.Span);
			}
			else
			{
				Span<byte> header = stackalloc byte[LengthHeaderSize];
				buffer.Slice(0, LengthHeaderSize).CopyTo(header);
				length = BinaryPrimitives.ReadUInt32BigEndian(header);
			}

			if (length == 0 || length > this.MaximumMessageSize)
			{
				throw new ProtocolViolationException($"Invalid MessagePack message length: {length}. The maximum frame size is {this.MaximumMessageSize} bytes.");
			}

			if (buffer.Length - LengthHeaderSize < length)
			{
				message = default;
				return false;
			}

			message = buffer.Slice(LengthHeaderSize, length);
			return true;
		}

		// A null refresh delegate makes an incomplete buffer report InsufficientBuffer rather than end-of-stream.
		MessagePackStreamingReader scanner = new(buffer, null, null);
		SerializationContext context = this.messagePackSerializer.StartingContext;
		switch (scanner.TrySkip(ref context))
		{
			case MessagePackPrimitives.DecodeResult.Success:
				message = buffer.Slice(0, scanner.Position);
				return true;
			case MessagePackPrimitives.DecodeResult.EmptyBuffer or MessagePackPrimitives.DecodeResult.InsufficientBuffer:
				message = default;
				return false;
			default:
				throw new ProtocolViolationException("Invalid MessagePack-encoded JSON-RPC message.");
		}
	}

	/// <summary>Synchronously deserializes the first message if the buffer contains all of it.</summary>
	/// <param name="buffer">The buffered bytes; advanced past the message when one is read.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <param name="message">Receives the message.</param>
	/// <returns><see langword="true"/> if a complete message was read.</returns>
	private bool TryReadMessage(ref ReadOnlySequence<byte> buffer, CancellationToken cancellationToken, [NotNullWhen(true)] out JsonRpcMessage? message)
	{
		if (!this.TryFindMessage(buffer, out ReadOnlySequence<byte> messageBytes))
		{
			message = null;
			return false;
		}

		if (messageBytes.Length > this.MaximumMessageSize)
		{
			throw new ProtocolViolationException("MessagePack frame exceeds the size limit.");
		}

		try
		{
			message = this.messagePackSerializer.Deserialize<JsonRpcMessagePackEnvelope>(messageBytes, cancellationToken).Message;
		}
		catch (MessagePackSerializationException ex) when (ex.InnerException is ProtocolViolationException inner)
		{
			ExceptionDispatchInfo.Capture(inner).Throw();
			throw;
		}

		buffer = buffer.Slice(messageBytes.End);
		return true;
	}
}
