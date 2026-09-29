// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

internal static class JsonRpcJsonCodec
{
	/// <summary>Reads a strict JSON-RPC envelope.</summary>
	/// <param name="frame">The complete UTF-8 JSON frame, whose buffer the message's values share.</param>
	/// <returns>A request, response, or batch.</returns>
	internal static JsonRpcMessage Read(byte[] frame)
	{
		try
		{
			Utf8JsonReader reader = new(frame);
			reader.Read();
			JsonRpcMessage message;
			if (reader.TokenType == JsonTokenType.StartArray)
			{
				ImmutableArray<JsonRpcMessage>.Builder messages = ImmutableArray.CreateBuilder<JsonRpcMessage>();
				while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
				{
					messages.Add(ReadOne(ref reader, frame));
				}

				if (messages.Count == 0)
				{
					throw new ProtocolViolationException("A JSON-RPC batch must not be empty.");
				}

				message = new JsonRpcMessageBatch(messages.ToImmutable());
			}
			else
			{
				message = ReadOne(ref reader, frame);
			}

			// Reject trailing content.
			reader.Read();
			return message;
		}
		catch (JsonException ex)
		{
			throw new ProtocolViolationException("Invalid JSON-RPC JSON payload: " + ex.Message);
		}
	}

	/// <summary>Reads an integer, string, or explicit null protocol ID.</summary>
	/// <param name="id">The reader, positioned at the JSON ID token.</param>
	/// <returns>The owned request ID.</returns>
	internal static RequestId ReadId(ref Utf8JsonReader id) => id.TokenType == JsonTokenType.Number && !IsIntegerToken(id.ValueSpan)
		? throw new ProtocolViolationException("JSON-RPC id must be an integer token.")
		: id.TokenType switch
		{
			JsonTokenType.Null => default,
			JsonTokenType.String => new RequestId(id.GetString()!),
			JsonTokenType.Number when id.TryGetInt64(out long signed) => new RequestId(signed),
			JsonTokenType.Number when id.TryGetUInt64(out ulong unsigned) => new RequestId(unsigned),
			_ => throw new ProtocolViolationException("JSON-RPC id must be a string, integer, or null (signed 64-bit or unsigned 64-bit range)."),
		};

	/// <summary>Writes the protocol ID without coercing its JSON token kind.</summary>
	/// <param name="writer">The JSON writer.</param>
	/// <param name="id">The request ID.</param>
	internal static void WriteId(Utf8JsonWriter writer, RequestId id)
	{
		if (id.IsNull)
		{
			writer.WriteNullValue();
		}
		else if (id.IsString)
		{
			writer.WriteStringValue(id.ToString());
		}
		else if (id.SignedValue is long signed)
		{
			writer.WriteNumberValue(signed);
		}
		else
		{
			writer.WriteNumberValue(id.UnsignedValue!.Value);
		}
	}

	/// <summary>Writes a JSON-RPC message or batch as one compact UTF-8 frame.</summary>
	/// <param name="writer">The writer that receives the encoded UTF-8 payload without framing.</param>
	/// <param name="message">The complete message.</param>
	internal static void Write(Utf8JsonWriter writer, JsonRpcMessage message)
	{
		if (message is JsonRpcMessageBatch batch)
		{
			if (batch.Messages.IsEmpty)
			{
				throw new ArgumentException("A batch must not be empty.", nameof(message));
			}

			writer.WriteStartArray();
			foreach (JsonRpcMessage entry in batch.Messages)
			{
				WriteOne(writer, entry);
			}

			writer.WriteEndArray();
		}
		else
		{
			WriteOne(writer, message);
		}

		writer.Flush();
	}

	/// <summary>Reads one message, leaving the reader at the end of its object.</summary>
	/// <param name="reader">The reader, positioned at the start of the message.</param>
	/// <param name="frame">The frame that the reader reads, whose buffer the message's values share.</param>
	/// <returns>The message.</returns>
	private static JsonRpcMessage ReadOne(ref Utf8JsonReader reader, byte[] frame)
	{
		if (reader.TokenType != JsonTokenType.StartObject)
		{
			throw new ProtocolViolationException("A JSON-RPC message must be an object.");
		}

		// Record where each property is, then validate the envelope as a whole before decoding any of them.
		Token method = default, idToken = default, parameters = default, result = default, error = default;
		bool hasVersion = false, hasMethod = false, hasId = false, hasParams = false, hasResult = false, hasError = false;
		bool isVersion2 = false;
		string? methodName = null;
		TopLevelProperties? extensions = null;
		while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
		{
			if (reader.ValueTextEquals("jsonrpc"u8))
			{
				SetOnce(ref hasVersion);
				reader.Read();
				isVersion2 = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("2.0"u8);
				reader.Skip();
			}
			else if (reader.ValueTextEquals("method"u8))
			{
				SetOnce(ref hasMethod);
				method = Token.Read(ref reader);
				if (method.Type == JsonTokenType.String)
				{
					methodName = reader.GetString();
				}
			}
			else if (reader.ValueTextEquals("id"u8))
			{
				SetOnce(ref hasId);
				idToken = Token.Read(ref reader);
			}
			else if (reader.ValueTextEquals("params"u8))
			{
				SetOnce(ref hasParams);
				parameters = Token.Read(ref reader);
			}
			else if (reader.ValueTextEquals("result"u8))
			{
				SetOnce(ref hasResult);
				result = Token.Read(ref reader);
			}
			else if (reader.ValueTextEquals("error"u8))
			{
				SetOnce(ref hasError);
				error = Token.Read(ref reader);
			}
			else
			{
				string name = reader.GetString()!;
				reader.Read();
				ReadExtension(ref reader, name, ref extensions);
			}
		}

		if (!hasVersion || !isVersion2 ||
			(hasMethod ? method.Type != JsonTokenType.String || hasResult || hasError : hasResult == hasError || !hasId || hasParams) ||
			(hasParams && parameters.Type is not (JsonTokenType.StartObject or JsonTokenType.StartArray)) ||
			(hasError && error.Type != JsonTokenType.StartObject))
		{
			throw new ProtocolViolationException("Unexpected JSON-RPC message envelope.");
		}

		RequestId id = default;
		if (hasId)
		{
			Utf8JsonReader idReader = new(idToken.GetSpan(frame));
			idReader.Read();
			id = ReadId(ref idReader);
		}

		if (hasMethod)
		{
			JsonRpcRequest request = new() { Method = methodName!, Arguments = hasParams ? parameters.GetValue(frame) : default, TopLevelProperties = extensions };
			if (hasId)
			{
				request.SetReceivedId(id);
			}

			return request;
		}

		if (hasResult)
		{
			return new JsonRpcResult { Id = id, Result = result.GetValue(frame), TopLevelProperties = extensions };
		}

		return new JsonRpcError { Id = id, Error = ReadError(frame, error), TopLevelProperties = extensions };
	}

	/// <summary>Retains a primitive extension property, ignoring values of other types.</summary>
	/// <param name="reader">The reader, positioned at the property value.</param>
	/// <param name="name">The non-reserved top-level property name.</param>
	/// <param name="extensions">The lazily created property bag.</param>
	private static void ReadExtension(ref Utf8JsonReader reader, string name, ref TopLevelProperties? extensions)
	{
		TopLevelPropertyValue? primitive = reader.TokenType switch
		{
			JsonTokenType.String => reader.GetString()!,
			JsonTokenType.Number when IsIntegerToken(reader.ValueSpan) && reader.TryGetInt64(out long integer) => integer,
			_ => null,
		};
		bool isNull = reader.TokenType == JsonTokenType.Null;
		reader.Skip();

		if (primitive is null && !isNull && TopLevelProperties.TryGetRequiredKind(name, out TopLevelPropertyKind kind))
		{
			throw new ProtocolViolationException($"The JSON-RPC '{name}' property must be a {kind} value.");
		}

		(extensions ??= new()).AddReceived(name, primitive);
	}

	private static JsonRpcErrorDetails ReadError(byte[] frame, Token error)
	{
		bool hasCode = false, hasMessage = false, hasData = false;
		long code = 0;
		string? message = null;
		JsonRpcValue data = default;
		Utf8JsonReader reader = new(error.GetSpan(frame));
		reader.Read();
		while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
		{
			if (reader.ValueTextEquals("code"u8))
			{
				SetOnce(ref hasCode);
				reader.Read();
				if (reader.TokenType != JsonTokenType.Number || !IsIntegerToken(reader.ValueSpan) || !reader.TryGetInt64(out code))
				{
					throw new ProtocolViolationException("JSON-RPC error code must be an integer.");
				}
			}
			else if (reader.ValueTextEquals("message"u8))
			{
				SetOnce(ref hasMessage);
				reader.Read();
				if (reader.TokenType != JsonTokenType.String)
				{
					throw new ProtocolViolationException("JSON-RPC error message must be a string.");
				}

				message = reader.GetString();
			}
			else if (reader.ValueTextEquals("data"u8))
			{
				SetOnce(ref hasData);
				data = Token.Read(ref reader).GetValue(frame, error.Start);
			}
			else
			{
				reader.Read();
				reader.Skip();
			}
		}

		if (!hasCode || !hasMessage)
		{
			throw new ProtocolViolationException("A JSON-RPC error requires code and message.");
		}

		return new() { Code = code, Message = message!, Data = hasData ? data : null };
	}

	private static void SetOnce(ref bool seen)
	{
		if (seen)
		{
			throw new ProtocolViolationException("Duplicate JSON-RPC property.");
		}

		seen = true;
	}

	private static bool IsIntegerToken(ReadOnlySpan<byte> number) => number.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0;

	private static void WriteOne(Utf8JsonWriter writer, JsonRpcMessage message)
	{
		writer.WriteStartObject();
		writer.WriteString("jsonrpc", message.Version);
		if (message is JsonRpcRequest request)
		{
			writer.WriteString("method", request.Method);
			if (request.Arguments.HasValue)
			{
				writer.WritePropertyName("params");
				WriteValue(writer, request.Arguments);
			}
		}
		else if (message is JsonRpcResult result)
		{
			writer.WritePropertyName("result");
			WriteValue(writer, result.Result);
		}
		else if (message is JsonRpcError error)
		{
			writer.WritePropertyName("error");
			writer.WriteStartObject();
			writer.WriteNumber("code", error.Error.Code);
			writer.WriteString("message", error.Error.Message);
			if (error.Error.Data is { HasValue: true } data)
			{
				writer.WritePropertyName("data");
				WriteValue(writer, data);
			}

			writer.WriteEndObject();
		}
		else
		{
			throw new ArgumentException("Unrecognized JSON-RPC message type.", nameof(message));
		}

		if (message.HasId)
		{
			writer.WritePropertyName("id");
			RequestId id = message.Id!.Value;
			WriteId(writer, id);
		}

		if (message.TopLevelProperties is { Count: > 0 } extensions)
		{
			foreach (KeyValuePair<string, TopLevelPropertyValue> property in extensions.Properties)
			{
				if (property.Value.Int64Value is long integer)
				{
					writer.WriteNumber(property.Key, integer);
				}
				else
				{
					writer.WriteString(property.Key, property.Value.StringValue);
				}
			}
		}

		writer.WriteEndObject();
	}

	private static void WriteValue(Utf8JsonWriter writer, JsonRpcValue value)
	{
		if (!value.HasValue || value.Encoding != JsonRpcEncoding.Json)
		{
			throw new ArgumentException("Expected a JSON application value.");
		}

		ReadOnlySpan<byte> bytes = value.OwnedBytes.Span;
		if (bytes.IndexOfAny((byte)'\r', (byte)'\n') < 0)
		{
			// Copy the already encoded value as it is, which still validates it.
			writer.WriteRawValue(bytes);
		}
		else
		{
			// Re-encode values that span lines so that the frame stays on one line.
			using JsonDocument document = JsonDocument.Parse(value.OwnedBytes);
			document.RootElement.WriteTo(writer);
		}
	}

	/// <summary>The location of a JSON value within the frame being read.</summary>
	private readonly struct Token
	{
		private Token(JsonTokenType type, int start, int length)
		{
			this.Type = type;
			this.Start = start;
			this.Length = length;
		}

		internal JsonTokenType Type { get; }

		internal int Start { get; }

		internal int Length { get; }

		/// <summary>Reads the value that follows a property name, leaving the reader at its last token.</summary>
		/// <param name="reader">The reader, positioned at the property name.</param>
		/// <returns>The value's location, relative to the start of the reader's input.</returns>
		internal static Token Read(ref Utf8JsonReader reader)
		{
			reader.Read();
			JsonTokenType type = reader.TokenType;
			int start = checked((int)reader.TokenStartIndex);
			reader.Skip();
			return new(type, start, checked((int)reader.BytesConsumed) - start);
		}

		internal ReadOnlySpan<byte> GetSpan(byte[] frame) => frame.AsSpan(this.Start, this.Length);

		/// <summary>Creates a value that shares the frame's buffer.</summary>
		/// <param name="frame">The frame.</param>
		/// <param name="baseOffset">The offset within the frame of the input that this token's location is relative to.</param>
		/// <returns>The value.</returns>
		internal JsonRpcValue GetValue(byte[] frame, int baseOffset = 0) => JsonRpcValue.FromJsonFrame(frame, baseOffset + this.Start, this.Length);
	}
}
