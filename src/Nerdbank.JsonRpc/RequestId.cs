// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

[MessagePackConverter(typeof(Converter))]
public struct RequestId
{
	private string? stringValue;
	private long? numberValue;

	public RequestId(string value)
	{
		this.stringValue = value;
	}

	public RequestId(long value)
	{
		this.numberValue = value;
	}

	public static implicit operator RequestId(string value) => new RequestId(value);

	public static implicit operator RequestId(long value) => new RequestId(value);

	public override string ToString() => this.stringValue ?? this.numberValue?.ToString() ?? "null";

	internal class Converter : MessagePackConverter<RequestId>
	{
		public override RequestId Read(ref MessagePackReader reader, SerializationContext context)
		{
			return reader.NextMessagePackType switch
			{
				MessagePackType.Integer => new RequestId(reader.ReadInt64()),
				MessagePackType.String => new RequestId(reader.ReadString()!),
				MessagePackType.Nil when reader.TryReadNil() => default,
				_ => throw new MessagePackSerializationException($"Cannot convert {reader.NextMessagePackType} to RequestId."),
			};
		}

		public override void Write(ref MessagePackWriter writer, in RequestId value, SerializationContext context)
		{
			if (value.numberValue is long n)
			{
				writer.Write(n);
			}
			else if (value.stringValue is string s)
			{
				writer.Write(s);
			}
			else
			{
				writer.WriteNil();
			}
		}
	}
}
