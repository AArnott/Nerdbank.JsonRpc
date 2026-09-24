// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Nerdbank.JsonRpc;

/// <summary>Defines the framing of UTF-8 JSON-RPC messages.</summary>
public enum JsonRpcJsonFraming
{
	/// <summary>One compact JSON payload per line.</summary>
	NewlineDelimited,

	/// <summary>ASCII Content-Length header followed by its UTF-8 body.</summary>
	ContentLength,
}

/// <summary>Transports JSON-RPC messages as UTF-8 JSON with explicit framing.</summary>
public sealed class JsonRpcJsonChannel : JsonRpcPipeChannel
{
	private const int MaximumFrameSize = 8 * 1024 * 1024;
	private const int MaximumHeaderSize = 8192;
	private readonly JsonRpcJsonFraming framing;

	/// <summary>Initializes a new instance of the <see cref="JsonRpcJsonChannel"/> class.</summary>
	/// <param name="pipe">The connected pipe.</param>
	/// <param name="serializer">The same plugin assigned to the JsonRpc instance.</param>
	/// <param name="framing">The wire framing convention.</param>
	/// <param name="logger">The transport logger.</param>
	/// <param name="inboundCapacity">The inbound queue limit.</param>
	/// <param name="outboundCapacity">The outbound queue limit.</param>
	public JsonRpcJsonChannel(IDuplexPipe pipe, JsonSerializerPlugin serializer, JsonRpcJsonFraming framing, ILogger logger, int? inboundCapacity = 100, int? outboundCapacity = null)
		: base(pipe, CreateInboundChannel(inboundCapacity), CreateOutboundChannel(outboundCapacity), logger, startImmediately: false)
	{
		this.SerializerPlugin = serializer ?? throw new ArgumentNullException(nameof(serializer));
		this.framing = framing;
		if (!Enum.IsDefined(typeof(JsonRpcJsonFraming), framing))
		{
			throw new ArgumentOutOfRangeException(nameof(framing));
		}

		this.StartTransport();
	}

	/// <summary>Initializes a new instance of the <see cref="JsonRpcJsonChannel"/> class.</summary>
	/// <param name="pipe">The connected pipe.</param>
	/// <param name="serializer">The configured serializer also assigned to JsonRpc.Serializer.</param>
	/// <param name="framing">The wire framing convention.</param>
	/// <param name="logger">The transport logger.</param>
	public JsonRpcJsonChannel(IDuplexPipe pipe, Nerdbank.Json.JsonSerializer serializer, JsonRpcJsonFraming framing, ILogger logger)
		: this(pipe, new JsonSerializerPlugin(serializer), framing, logger)
	{
	}

	/// <inheritdoc/>
	public override JsonRpcEncoding Encoding => JsonRpcEncoding.Json;

	/// <inheritdoc/>
	public override JsonRpcSerializer SerializerPlugin { get; }

	/// <inheritdoc/>
	protected override async IAsyncEnumerable<JsonRpcMessage> ReceiveMessagesAsync(PipeReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		while (true)
		{
			ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			ReadOnlySequence<byte> buffer = read.Buffer;
			if (this.TryReadFrame(buffer, out byte[]? payload, out SequencePosition consumed))
			{
				reader.AdvanceTo(consumed);
				yield return JsonRpcJsonCodec.Read(payload!);
			}
			else
			{
				if (read.IsCompleted)
				{
					reader.AdvanceTo(buffer.End);
					if (!buffer.IsEmpty)
					{
						throw new ProtocolViolationException("Incomplete JSON-RPC frame at end of stream.");
					}

					yield break;
				}

				if (buffer.Length > MaximumFrameSize + MaximumHeaderSize)
				{
					throw new ProtocolViolationException("JSON-RPC frame exceeds the size limit.");
				}

				reader.AdvanceTo(buffer.Start, buffer.End);
			}
		}
	}

	/// <inheritdoc/>
	protected override ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		this.SerializerPlugin.ValidateMessage(message);
		byte[] payload = JsonRpcJsonCodec.Write(message);
		if (payload.Length > MaximumFrameSize)
		{
			throw new ProtocolViolationException("JSON-RPC frame exceeds the size limit.");
		}

		if (this.framing == JsonRpcJsonFraming.ContentLength)
		{
			writer.Write(System.Text.Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n"));
		}

		writer.Write(payload);
		if (this.framing == JsonRpcJsonFraming.NewlineDelimited)
		{
			writer.Write(new byte[] { (byte)'\n' });
		}

		return default;
	}

	private bool TryReadFrame(ReadOnlySequence<byte> buffer, out byte[]? payload, out SequencePosition consumed)
	{
		payload = null;
		consumed = buffer.Start;
		if (this.framing == JsonRpcJsonFraming.NewlineDelimited)
		{
			SequencePosition? newline = buffer.PositionOf((byte)'\n');
			if (newline is null)
			{
				return false;
			}

			ReadOnlySequence<byte> record = buffer.Slice(0, newline.Value);
			if (!record.IsEmpty && record.Slice(record.Length - 1, 1).ToArray()[0] == (byte)'\r')
			{
				record = record.Slice(0, record.Length - 1);
			}

			if (record.IsEmpty || record.Length > MaximumFrameSize)
			{
				throw new ProtocolViolationException("Invalid or oversized JSON-RPC line.");
			}

			payload = record.ToArray();
			consumed = buffer.GetPosition(1, newline.Value);
			return true;
		}

		if (buffer.Length > MaximumHeaderSize && buffer.Slice(0, MaximumHeaderSize).ToArray().AsSpan().IndexOf("\r\n\r\n"u8) < 0)
		{
			throw new ProtocolViolationException("JSON-RPC header exceeds the size limit.");
		}

		byte[] prefix = buffer.Slice(0, Math.Min(buffer.Length, MaximumHeaderSize)).ToArray();
		int headerEnd = prefix.AsSpan().IndexOf("\r\n\r\n"u8);
		if (headerEnd < 0)
		{
			return false;
		}

		foreach (byte headerByte in prefix.AsSpan(0, headerEnd))
		{
			if (headerByte > 127 || (headerByte < 32 && headerByte != (byte)'\r' && headerByte != (byte)'\n'))
			{
				throw new ProtocolViolationException("JSON-RPC headers must be ASCII.");
			}
		}

		string[] lines = System.Text.Encoding.ASCII.GetString(prefix, 0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
		int? length = null;
		foreach (string line in lines)
		{
			int separator = line.IndexOf(':');
			if (separator <= 0 || line.Any(c => c > 127 || c < 32))
			{
				throw new ProtocolViolationException("Invalid JSON-RPC frame header.");
			}

			if (line.Substring(0, separator).Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
			{
				if (length.HasValue || !int.TryParse(line.Substring(separator + 1).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed) || parsed <= 0 || parsed > MaximumFrameSize)
				{
					throw new ProtocolViolationException("Invalid JSON-RPC Content-Length header.");
				}

				length = parsed;
			}
		}

		if (!length.HasValue)
		{
			throw new ProtocolViolationException("Missing JSON-RPC Content-Length header.");
		}

		long total = headerEnd + 4L + length.Value;
		if (buffer.Length < total)
		{
			return false;
		}

		payload = buffer.Slice(headerEnd + 4L, length.Value).ToArray();
		consumed = buffer.GetPosition(total);
		return true;
	}
}
