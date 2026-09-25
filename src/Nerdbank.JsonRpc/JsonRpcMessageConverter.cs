// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Net;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

/// <summary>Adapts an encoding-neutral message to the MessagePack transport serializer.</summary>
[GenerateShape]
[MessagePackConverter(typeof(JsonRpcMessageConverter))]
internal readonly partial struct JsonRpcMessagePackEnvelope(JsonRpcMessage message)
{
	/// <summary>Gets the protocol message represented by this wire envelope.</summary>
	internal JsonRpcMessage Message { get; } = message;
}

#pragma warning disable NBMsgPack031 // This discriminating converter conditionally reads exactly one MessagePack structure.
internal class JsonRpcMessageConverter : MessagePackConverter<JsonRpcMessagePackEnvelope>
{
	private static readonly JsonRpcErrorDetailsConverter ErrorConverter = new();

	public override JsonRpcMessagePackEnvelope Read(ref MessagePackReader reader, SerializationContext context)
	{
		return new(reader.NextMessagePackType switch
		{
			MessagePackType.Map => ReadSingleMessage(ref reader, context),
			MessagePackType.Array => ReadBatch(ref reader, context),
			_ => throw new ProtocolViolationException("A JSON-RPC payload must be a message object or a non-empty batch."),
		});
	}

	public override void Write(ref MessagePackWriter writer, in JsonRpcMessagePackEnvelope value, SerializationContext context)
		=> WriteMessage(ref writer, value.Message, context);

	private static JsonRpcMessage ReadBatch(ref MessagePackReader reader, SerializationContext context)
	{
		int count = reader.ReadArrayHeader();
		if (count == 0)
		{
			throw new ProtocolViolationException("A JSON-RPC batch must not be empty.");
		}

		ImmutableArray<JsonRpcMessage>.Builder messages = ImmutableArray.CreateBuilder<JsonRpcMessage>(count);
		for (int i = 0; i < count; i++)
		{
			if (reader.NextMessagePackType != MessagePackType.Map)
			{
				throw new ProtocolViolationException("Every JSON-RPC batch entry must be a message object.");
			}

			messages.Add(ReadSingleMessage(ref reader, context));
		}

		return new JsonRpcMessageBatch(messages.MoveToImmutable());
	}

	private static JsonRpcMessage ReadSingleMessage(ref MessagePackReader reader, SerializationContext context)
	{
		int count = reader.ReadMapHeader();
		bool version = false, method = false, result = false, error = false, idPresent = false, parameters = false;
		RequestId id = default;
		string? methodName = null;
		JsonRpcValue arguments = default, resultValue = default;
		JsonRpcErrorDetails? errorDetails = null;
		TopLevelProperties? extensions = null;
		for (int i = 0; i < count; i++)
		{
			if (reader.NextMessagePackType != MessagePackType.String)
			{
				throw new ProtocolViolationException("A JSON-RPC property name must be a string.");
			}

			ReadOnlySpan<byte> name = reader.ReadStringSpan();
			if (name.SequenceEqual("jsonrpc"u8))
			{
				if (version || reader.NextMessagePackType != MessagePackType.String || !reader.ReadStringSpan().SequenceEqual("2.0"u8))
				{
					throw new ProtocolViolationException("A JSON-RPC message must declare version 2.0 exactly once.");
				}

				version = true;
			}
			else if (name.SequenceEqual("id"u8))
			{
				if (idPresent)
				{
					throw new ProtocolViolationException("A JSON-RPC message contains duplicate IDs.");
				}

				idPresent = true;
				id = context.GetConverter<RequestId>().Read(ref reader, context);
			}
			else if (name.SequenceEqual("method"u8))
			{
				if (method || reader.NextMessagePackType != MessagePackType.String)
				{
					throw new ProtocolViolationException("A JSON-RPC method must be a string supplied once.");
				}

				method = true;
				methodName = reader.ReadString();
			}
			else if (name.SequenceEqual("params"u8))
			{
				if (parameters || reader.NextMessagePackType is not (MessagePackType.Map or MessagePackType.Array))
				{
					throw new ProtocolViolationException("JSON-RPC params must be an array or object supplied once.");
				}

				parameters = true;
				arguments = JsonRpcValue.FromMessagePack(reader.ReadRaw(context));
			}
			else if (name.SequenceEqual("result"u8))
			{
				if (result)
				{
					throw new ProtocolViolationException("A JSON-RPC result must be supplied once.");
				}

				result = true;
				resultValue = JsonRpcValue.FromMessagePack(reader.ReadRaw(context));
			}
			else if (name.SequenceEqual("error"u8))
			{
				if (error || reader.NextMessagePackType != MessagePackType.Map)
				{
					throw new ProtocolViolationException("A JSON-RPC error must be an object supplied once.");
				}

				error = true;
				errorDetails = ErrorConverter.Read(ref reader, context);
			}
			else
			{
				ReadExtension(ref reader, name, ref extensions, context);
			}
		}

		if (!version || (method ? result || error : result == error || !idPresent || parameters))
		{
			throw new ProtocolViolationException("Unexpected JSON-RPC message envelope.");
		}

		return method
			? new JsonRpcRequest { Method = methodName!, Arguments = arguments, Id = idPresent ? id : (RequestId?)null, TopLevelProperties = extensions }
			: result
				? new JsonRpcResult { Id = id, Result = resultValue, TopLevelProperties = extensions }
				: new JsonRpcError { Id = id, Error = errorDetails!, TopLevelProperties = extensions };
	}

	/// <summary>Retains a primitive extension property, skipping values of other types.</summary>
	/// <param name="reader">The reader, positioned at the property value.</param>
	/// <param name="name">The UTF-8 property name.</param>
	/// <param name="extensions">The lazily created property bag.</param>
	/// <param name="context">The serialization context.</param>
	private static void ReadExtension(ref MessagePackReader reader, ReadOnlySpan<byte> name, ref TopLevelProperties? extensions, SerializationContext context)
	{
		TopLevelPropertyValue? primitive = null;
		bool isNil = reader.NextMessagePackType == MessagePackType.Nil;
		switch (reader.NextMessagePackType)
		{
			case MessagePackType.String:
				primitive = reader.ReadString()!;
				break;
			case MessagePackType.Integer when reader.NextCode != MessagePackCode.UInt64:
				primitive = reader.ReadInt64();
				break;
			case MessagePackType.Integer:
				ulong unsigned = reader.ReadUInt64();
				if (unsigned <= long.MaxValue)
				{
					primitive = (long)unsigned;
				}

				break;
			case MessagePackType.Nil:
				reader.ReadNil();
				break;
			default:
				reader.Skip(context);
				break;
		}

		string propertyName = StringEncoding.UTF8.GetString(name);
		if (primitive is null && !isNil && TopLevelProperties.TryGetRequiredKind(propertyName, out TopLevelPropertyKind kind))
		{
			throw new ProtocolViolationException($"The JSON-RPC '{propertyName}' property must be a {kind} value.");
		}

		(extensions ??= new()).AddReceived(propertyName, primitive);
	}

	private static void WriteMessage(ref MessagePackWriter writer, JsonRpcMessage message, SerializationContext context)
	{
		int extensionCount = message.TopLevelProperties?.Count ?? 0;
		switch (message)
		{
			case JsonRpcRequest request:
				writer.WriteMapHeader(2 + (request.HasId ? 1 : 0) + (request.Arguments.HasValue ? 1 : 0) + extensionCount);
				writer.Write("jsonrpc");
				writer.Write(request.Version);
				writer.Write("method");
				writer.Write(request.Method);
				if (request.Arguments.HasValue)
				{
					writer.Write("params");
					writer.WriteRaw(request.Arguments.AsMessagePack().MsgPack);
				}

				if (request.HasId)
				{
					WriteId(ref writer, request.Id!.Value, context);
				}

				WriteExtensions(ref writer, message);
				break;
			case JsonRpcResult result:
				writer.WriteMapHeader(3 + extensionCount);
				writer.Write("jsonrpc");
				writer.Write(result.Version);
				writer.Write("result");
				writer.WriteRaw(result.Result.AsMessagePack().MsgPack);
				WriteId(ref writer, result.Id, context);
				WriteExtensions(ref writer, message);
				break;
			case JsonRpcError error:
				writer.WriteMapHeader(3 + extensionCount);
				writer.Write("jsonrpc");
				writer.Write(error.Version);
				writer.Write("error");
				ErrorConverter.Write(ref writer, error.Error, context);
				WriteId(ref writer, error.Id, context);
				WriteExtensions(ref writer, message);
				break;
			case JsonRpcMessageBatch batch:
				writer.WriteArrayHeader(batch.Messages.Length);
				foreach (JsonRpcMessage entry in batch.Messages)
				{
					WriteMessage(ref writer, entry, context);
				}

				break;
			case JsonRpcInvalidMessage:
				throw new ArgumentException("Invalid protocol markers cannot be serialized.", nameof(message));
			case null:
				throw new ArgumentNullException(nameof(message));
			default:
				throw new ArgumentException($"Unrecognized JSON-RPC message type: {message.GetType().FullName}", nameof(message));
		}
	}

	private static void WriteId(ref MessagePackWriter writer, RequestId id, SerializationContext context)
	{
		writer.Write("id");
		context.GetConverter<RequestId>().Write(ref writer, id, context);
	}

	private static void WriteExtensions(ref MessagePackWriter writer, JsonRpcMessage message)
	{
		if (message.TopLevelProperties is { Count: > 0 } extensions)
		{
			foreach (KeyValuePair<string, TopLevelPropertyValue> property in extensions.Properties)
			{
				writer.Write(property.Key);
				if (property.Value.Int64Value is long integer)
				{
					writer.Write(integer);
				}
				else
				{
					writer.Write(property.Value.StringValue);
				}
			}
		}
	}
}
