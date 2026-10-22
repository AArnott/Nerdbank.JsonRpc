// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;

namespace Nerdbank.JsonRpc;

internal sealed class MarshaledDisposableJsonConverter(MarshaledObjectManager manager) : Nerdbank.Json.JsonConverter<IDisposable>
{
	public override IDisposable? Read(ref Nerdbank.Json.JsonReader reader, Nerdbank.Json.SerializationContext context)
	{
		string rawValue = reader.ReadRawValue();
		return rawValue == "null" ? null : manager.Unmarshal(JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(rawValue)), RpcCallState.Current);
	}

	public override void Write(ref Nerdbank.Json.JsonWriter writer, in IDisposable? value, Nerdbank.Json.SerializationContext context)
	{
		if (value is null)
		{
			writer.WriteNull();
			return;
		}

		JsonRpcValue marker = manager.Marshal(value, JsonRpcEncoding.Json, RpcCallState.Current);
		writer.WriteRaw(marker.OwnedBytes.Span);
	}
}
