// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Net;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

/// <summary>Preserves the difference between absent and explicit nil MessagePack error data.</summary>
internal sealed class JsonRpcErrorDetailsConverter : MessagePackConverter<JsonRpcErrorDetails>
{
	/// <inheritdoc/>
	public override JsonRpcErrorDetails Read(ref MessagePackReader reader, SerializationContext context)
	{
		int count = reader.ReadMapHeader();
		bool hasCode = false, hasMessage = false, hasData = false;
		long code = 0;
		string? message = null;
		JsonRpcValue data = default;
		for (int i = 0; i < count; i++)
		{
			if (reader.NextMessagePackType != MessagePackType.String)
			{
				throw new ProtocolViolationException("An error property name must be a string.");
			}

			ReadOnlySpan<byte> name = reader.ReadStringSpan();
			if (name.SequenceEqual("code"u8))
			{
				if (hasCode || reader.NextMessagePackType != MessagePackType.Integer)
				{
					throw new ProtocolViolationException("An error code must be an integer supplied once.");
				}

				hasCode = true;
				code = reader.ReadInt64();
			}
			else if (name.SequenceEqual("message"u8))
			{
				if (hasMessage || reader.NextMessagePackType != MessagePackType.String)
				{
					throw new ProtocolViolationException("An error message must be a string supplied once.");
				}

				hasMessage = true;
				message = reader.ReadString();
			}
			else if (name.SequenceEqual("data"u8))
			{
				if (hasData)
				{
					throw new ProtocolViolationException("An error contains duplicate data.");
				}

				hasData = true;
				data = JsonRpcValue.FromMessagePack(reader.ReadRaw(context));
			}
			else
			{
				reader.Skip(context);
			}
		}

		if (!hasCode || !hasMessage)
		{
			throw new ProtocolViolationException("A JSON-RPC error requires code and message.");
		}

		return new() { Code = code, Message = message!, Data = hasData ? data : null };
	}

	/// <inheritdoc/>
	public override void Write(ref MessagePackWriter writer, in JsonRpcErrorDetails? value, SerializationContext context)
	{
		if (value is null)
		{
			throw new ArgumentNullException(nameof(value));
		}

		JsonRpcValue? data = value.Data is { HasValue: true } present ? present : null;
		writer.WriteMapHeader(data.HasValue ? 3 : 2);
		writer.Write("code");
		writer.Write(value.Code);
		writer.Write("message");
		writer.Write(value.Message);
		if (data is JsonRpcValue encoded)
		{
			writer.Write("data");
			writer.WriteRaw(encoded.AsMessagePack().MsgPack);
		}
	}
}
