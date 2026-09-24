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
	/// <param name="payload">The complete UTF-8 JSON frame.</param>
	/// <returns>A request, response, or batch.</returns>
	internal static JsonRpcMessage Read(ReadOnlyMemory<byte> payload)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(payload);
			JsonElement root = document.RootElement;
			if (root.ValueKind == JsonValueKind.Array)
			{
				if (root.GetArrayLength() == 0)
				{
					throw new ProtocolViolationException("A JSON-RPC batch must not be empty.");
				}

				ImmutableArray<JsonRpcMessage>.Builder messages = ImmutableArray.CreateBuilder<JsonRpcMessage>(root.GetArrayLength());
				foreach (JsonElement entry in root.EnumerateArray())
				{
					messages.Add(ReadOne(entry));
				}

				return new JsonRpcMessageBatch(messages.MoveToImmutable());
			}

			return ReadOne(root);
		}
		catch (JsonException ex)
		{
			throw new ProtocolViolationException("Invalid JSON-RPC JSON payload: " + ex.Message);
		}
	}

	/// <summary>Reads an integer, string, or explicit null protocol ID.</summary>
	/// <param name="id">The JSON ID token.</param>
	/// <returns>The owned request ID.</returns>
	internal static RequestId ReadId(JsonElement id) => id.ValueKind == JsonValueKind.Number && !IsIntegerToken(id)
		? throw new ProtocolViolationException("JSON-RPC id must be an integer token.")
		: id.ValueKind switch
	{
		JsonValueKind.Null => default,
		JsonValueKind.String => new RequestId(id.GetString()!),
		JsonValueKind.Number when id.TryGetInt64(out long signed) => new RequestId(signed),
		JsonValueKind.Number when id.TryGetUInt64(out ulong unsigned) => new RequestId(unsigned),
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
	/// <param name="message">The complete message.</param>
	/// <returns>The encoded UTF-8 payload without framing.</returns>
	internal static byte[] Write(JsonRpcMessage message)
	{
		using Sequence<byte> buffer = new();
		using Utf8JsonWriter writer = new(buffer);
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
		return buffer.AsReadOnlySequence.ToArray();
	}

	private static JsonRpcMessage ReadOne(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new ProtocolViolationException("A JSON-RPC message must be an object.");
		}

		JsonElement version = default, method = default, idElement = default, parameters = default, result = default, error = default;
		bool hasVersion = false, hasMethod = false, hasId = false, hasParams = false, hasResult = false, hasError = false;
		foreach (JsonProperty property in element.EnumerateObject())
		{
			switch (property.Name)
			{
				case "jsonrpc": SetOnce(ref hasVersion); version = property.Value; break;
				case "method": SetOnce(ref hasMethod); method = property.Value; break;
				case "id": SetOnce(ref hasId); idElement = property.Value; break;
				case "params": SetOnce(ref hasParams); parameters = property.Value; break;
				case "result": SetOnce(ref hasResult); result = property.Value; break;
				case "error": SetOnce(ref hasError); error = property.Value; break;
			}
		}

		if (!hasVersion || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
			(hasMethod ? method.ValueKind != JsonValueKind.String || hasResult || hasError : hasResult == hasError || !hasId || hasParams) ||
			(hasParams && parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) ||
			(hasError && error.ValueKind != JsonValueKind.Object))
		{
			throw new ProtocolViolationException("Unexpected JSON-RPC message envelope.");
		}

		RequestId id = hasId ? ReadId(idElement) : default;
		if (hasMethod)
		{
			JsonRpcRequest request = new() { Method = method.GetString()!, Arguments = hasParams ? Raw(parameters) : default };
			if (hasId)
			{
				request.SetReceivedId(id);
			}

			return request;
		}

		if (hasResult)
		{
			return new JsonRpcResult { Id = id, Result = Raw(result) };
		}

		return new JsonRpcError { Id = id, Error = ReadError(error) };
	}

	private static JsonRpcErrorDetails ReadError(JsonElement error)
	{
		bool hasCode = false, hasMessage = false, hasData = false;
		long code = 0;
		string? message = null;
		JsonRpcValue data = default;
		foreach (JsonProperty property in error.EnumerateObject())
		{
			switch (property.Name)
			{
				case "code":
					SetOnce(ref hasCode);
					if (!IsIntegerToken(property.Value) || !property.Value.TryGetInt64(out code))
					{
						throw new ProtocolViolationException("JSON-RPC error code must be an integer.");
					}

					break;
				case "message":
					SetOnce(ref hasMessage);
					if (property.Value.ValueKind != JsonValueKind.String)
					{
						throw new ProtocolViolationException("JSON-RPC error message must be a string.");
					}

					message = property.Value.GetString();
					break;
				case "data": SetOnce(ref hasData); data = Raw(property.Value); break;
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

	private static bool IsIntegerToken(JsonElement value)
		=> value.ValueKind == JsonValueKind.Number && value.GetRawText().IndexOfAny(new[] { '.', 'e', 'E' }) < 0;

	private static JsonRpcValue Raw(JsonElement element) => JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(element.GetRawText()));

	private static void WriteOne(Utf8JsonWriter writer, JsonRpcMessage message)
	{
		writer.WriteStartObject();
		writer.WriteString("jsonrpc", "2.0");
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

		writer.WriteEndObject();
	}

	private static void WriteValue(Utf8JsonWriter writer, JsonRpcValue value)
	{
		if (!value.HasValue || value.Encoding != JsonRpcEncoding.Json)
		{
			throw new ArgumentException("Expected a JSON application value.");
		}

		using JsonDocument document = JsonDocument.Parse(value.OwnedBytes);
		document.RootElement.WriteTo(writer);
	}
}
