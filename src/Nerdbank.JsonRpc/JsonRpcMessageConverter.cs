// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Net;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

internal class JsonRpcMessageConverter : MessagePackConverter<JsonRpcMessage>
{
	private static readonly MessagePackString Method = new("method");
	private static readonly MessagePackString Result = new("result");
	private static readonly MessagePackString Error = new("error");

	public override JsonRpcMessage? Read(ref MessagePackReader reader, SerializationContext context)
	{
		MessagePackReader peekReader = reader.CreatePeekReader();
		int count = peekReader.ReadMapHeader();
		for (int i = 0; i < count; i++)
		{
			if (Method.TryRead(ref peekReader))
			{
				return context.GetConverter<JsonRpcRequest>().Read(ref reader, context);
			}
			else if (Result.TryRead(ref peekReader))
			{
				return context.GetConverter<JsonRpcResult>().Read(ref reader, context);
			}
			else if (Error.TryRead(ref peekReader))
			{
				return context.GetConverter<JsonRpcError>().Read(ref reader, context);
			}

			// Skip the property name and value.
			peekReader.Skip(context);
			peekReader.Skip(context);
		}

		throw new ProtocolViolationException("Unexpected JSON-RPC message format.");
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
			case null: throw new ArgumentNullException(nameof(value));
			default: throw new ArgumentException($"Unrecognized JSON-RPC message type: {value.GetType().FullName}");
		}
	}
}
