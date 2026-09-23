// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Net;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

#pragma warning disable NBMsgPack031 // This discriminating converter conditionally reads exactly one MessagePack structure.
internal class JsonRpcMessageConverter : MessagePackConverter<JsonRpcMessage>
{
	private static readonly MessagePackString Method = new("method");
	private static readonly MessagePackString Result = new("result");
	private static readonly MessagePackString Error = new("error");

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
			return new JsonRpcInvalidMessage(JsonRpcErrorCode.InvalidRequest, "A JSON-RPC batch must contain at least one entry.");
		}

		ImmutableArray<JsonRpcMessage>.Builder messages = ImmutableArray.CreateBuilder<JsonRpcMessage>(count);
		for (int i = 0; i < count; i++)
		{
			MessagePackReader elementReader = reader;
			try
			{
				JsonRpcMessage message = context.GetConverter<JsonRpcMessage>().Read(ref reader, context)
					?? new JsonRpcInvalidMessage(JsonRpcErrorCode.InvalidRequest, "A JSON-RPC batch entry cannot be nil.");
				if (message is JsonRpcMessageBatch)
				{
					for (int j = i + 1; j < count; j++)
					{
						reader.Skip(context);
					}

					return new JsonRpcInvalidMessage(JsonRpcErrorCode.InvalidRequest, "A JSON-RPC batch entry must be a message object, not another batch.");
				}

				messages.Add(message);
			}
			catch (Exception ex) when (ex is MessagePackSerializationException or ProtocolViolationException or InvalidOperationException or ArgumentException)
			{
				try
				{
					elementReader.Skip(context);
					reader = elementReader;
					messages.Add(new JsonRpcInvalidMessage(JsonRpcErrorCode.InvalidRequest, "A JSON-RPC batch entry was not a valid request or response."));
				}
				catch (Exception skipException) when (skipException is MessagePackSerializationException or InvalidOperationException or ArgumentException)
				{
					throw new ProtocolViolationException("The JSON-RPC batch payload could not be parsed.");
				}
			}
		}

		return new JsonRpcMessageBatch(messages.MoveToImmutable());
	}

	private static JsonRpcMessage ReadInvalidMessage(ref MessagePackReader reader, SerializationContext context)
	{
		reader.Skip(context);
		return new JsonRpcInvalidMessage(JsonRpcErrorCode.InvalidRequest, "A JSON-RPC payload must be an object or a non-empty array.");
	}

	private static JsonRpcMessage ReadSingleMessage(ref MessagePackReader reader, SerializationContext context)
	{
		MessagePackReader peekReader = reader.CreatePeekReader();
		int count = peekReader.ReadMapHeader();
		for (int i = 0; i < count; i++)
		{
			if (Method.TryRead(ref peekReader))
			{
				return context.GetConverter<JsonRpcRequest>().Read(ref reader, context) ?? throw new ProtocolViolationException("Unexpected nil JSON-RPC request.");
			}
			else if (Result.TryRead(ref peekReader))
			{
				return context.GetConverter<JsonRpcResult>().Read(ref reader, context) ?? throw new ProtocolViolationException("Unexpected nil JSON-RPC result.");
			}
			else if (Error.TryRead(ref peekReader))
			{
				return context.GetConverter<JsonRpcError>().Read(ref reader, context) ?? throw new ProtocolViolationException("Unexpected nil JSON-RPC error.");
			}

			peekReader.Skip(context);
			peekReader.Skip(context);
		}

		throw new ProtocolViolationException("Unexpected JSON-RPC message format.");
	}
}
