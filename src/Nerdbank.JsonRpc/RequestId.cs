// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

[GenerateShape]
[MessagePackConverter(typeof(Converter))]
public partial struct RequestId : IEquatable<RequestId>
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

	public override int GetHashCode() => this.numberValue is long n ? n.GetHashCode() : this.stringValue?.GetHashCode() ?? 0;

	public override bool Equals(object? obj) => obj is RequestId other && this.Equals(other);

	public bool Equals(RequestId other) => this.stringValue == other.stringValue && this.numberValue == other.numberValue;

	[EditorBrowsable(EditorBrowsableState.Never)]
	public class Converter : MessagePackConverter<RequestId>
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
