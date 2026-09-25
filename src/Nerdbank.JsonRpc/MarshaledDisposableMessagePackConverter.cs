// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

internal sealed class MarshaledDisposableMessagePackConverter(MarshaledObjectManager manager) : MessagePackConverter<IDisposable>
{
	public override IDisposable? Read(ref MessagePackReader reader, SerializationContext context)
		=> reader.TryReadNil() ? null : manager.Unmarshal(JsonRpcValue.FromMessagePack(reader.ReadRaw(context)));

	public override void Write(ref MessagePackWriter writer, in IDisposable? value, SerializationContext context)
	{
		if (value is null)
		{
			writer.WriteNil();
			return;
		}

		writer.Write(manager.Marshal(value, JsonRpcEncoding.MessagePack).AsMessagePack());
	}
}
