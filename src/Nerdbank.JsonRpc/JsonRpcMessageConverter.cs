// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Net;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

#pragma warning disable NBMsgPack031 // This discriminating converter conditionally reads exactly one MessagePack structure.
internal class JsonRpcMessageConverter : MessagePackConverter<JsonRpcMessage>
{
	public override JsonRpcMessage? Read(ref MessagePackReader reader, SerializationContext context)
	{
		return reader.NextMessagePackType switch
		{
			MessagePackType.Map => ReadSingleMessage(ref reader, context),
			MessagePackType.Array => ReadBatch(ref reader, context),
			_ => ReadInvalidMessage(ref reader, context),
		};
	}

	public override void Write(ref MessagePackWriter writer, in JsonRpcMessage? value, SerializationContext context)
	{
		switch (value)
		{
			case JsonRpcRequest request:
				context.GetConverter<JsonRpcRequest>().Write(ref writer, request, context);
				break;
			case JsonRpcResult result:
				context.GetConverter<JsonRpcResult>().Write(ref writer, result, context);
				break;
			case JsonRpcError error:
				context.GetConverter<JsonRpcError>().Write(ref writer, error, context);
				break;
			case JsonRpcMessageBatch batch:
				writer.WriteArrayHeader(batch.Messages.Length);
				foreach (JsonRpcMessage message in batch.Messages)
				{
					this.Write(ref writer, message, context);
				}

				break;
			case JsonRpcInvalidMessage:
				throw new ArgumentException("Invalid protocol markers cannot be serialized.", nameof(value));
			case null: throw new ArgumentNullException(nameof(value));
			default: throw new ArgumentException($"Unrecognized JSON-RPC message type: {value.GetType().FullName}");
		}
	}

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

	private static JsonRpcMessage ReadInvalidMessage(ref MessagePackReader reader, SerializationContext context)
		=> throw new ProtocolViolationException("A JSON-RPC payload must be a message object or a non-empty batch.");

	private static JsonRpcMessage ReadSingleMessage(ref MessagePackReader reader, SerializationContext context)
	{
		MessagePackReader peekReader = reader.CreatePeekReader();
		int count = peekReader.ReadMapHeader();
		bool version = false, method = false, result = false, error = false, idPresent = false, parameters = false;
		RequestId id = default;
		for (int i = 0; i < count; i++)
		{
			if (peekReader.NextMessagePackType != MessagePackType.String)
			{
				throw new ProtocolViolationException("A JSON-RPC property name must be a string.");
			}

			ReadOnlySpan<byte> name = peekReader.ReadStringSpan();
			if (name.SequenceEqual("jsonrpc"u8))
			{
				if (version || peekReader.NextMessagePackType != MessagePackType.String || !peekReader.ReadStringSpan().SequenceEqual("2.0"u8))
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
				id = context.GetConverter<RequestId>().Read(ref peekReader, context);
			}
			else if (name.SequenceEqual("method"u8))
			{
				if (method || peekReader.NextMessagePackType != MessagePackType.String)
				{
					throw new ProtocolViolationException("A JSON-RPC method must be a string supplied once.");
				}

				method = true;
				peekReader.Skip(context);
			}
			else if (name.SequenceEqual("params"u8))
			{
				if (parameters || peekReader.NextMessagePackType is not (MessagePackType.Map or MessagePackType.Array))
				{
					throw new ProtocolViolationException("JSON-RPC params must be an array or object supplied once.");
				}

				parameters = true;
				peekReader.Skip(context);
			}
			else if (name.SequenceEqual("result"u8))
			{
				if (result)
				{
					throw new ProtocolViolationException("A JSON-RPC result must be supplied once.");
				}

				result = true;
				peekReader.Skip(context);
			}
			else if (name.SequenceEqual("error"u8))
			{
				if (error || peekReader.NextMessagePackType != MessagePackType.Map)
				{
					throw new ProtocolViolationException("A JSON-RPC error must be an object supplied once.");
				}

				error = true;
				peekReader.Skip(context);
			}
			else
			{
				peekReader.Skip(context);
			}
		}

		if (!version || (method ? result || error : result == error || !idPresent || parameters))
		{
			throw new ProtocolViolationException("Unexpected JSON-RPC message envelope.");
		}

		JsonRpcMessage message = method
			? context.GetConverter<JsonRpcRequest>().Read(ref reader, context) ?? throw new ProtocolViolationException("Unexpected nil JSON-RPC request.")
			: result
				? context.GetConverter<JsonRpcResult>().Read(ref reader, context) ?? throw new ProtocolViolationException("Unexpected nil JSON-RPC result.")
				: context.GetConverter<JsonRpcError>().Read(ref reader, context) ?? throw new ProtocolViolationException("Unexpected nil JSON-RPC error.");
		if (idPresent)
		{
			message.SetReceivedId(id);
		}

		return message;
	}
}
