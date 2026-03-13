// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Text;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

[GenerateShape]
[MessagePackConverter(typeof(Converter))]
public partial struct RequestId : IEquatable<RequestId>
{
	private ReadOnlyMemory<byte>? utf8Value;
	private long? numberValue;
	private string? stringCache;

	/// <summary>
	/// Initializes a new instance of the <see cref="RequestId"/> struct
	/// with a string value.
	/// </summary>
	/// <param name="value">The UTF-8 encoded bytes of the string request ID.</param>
	public RequestId(ReadOnlyMemory<byte> value)
	{
		this.utf8Value = value;
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="RequestId"/> struct
	/// with an integer value.
	/// </summary>
	/// <param name="value">The request ID.</param>
	public RequestId(long value)
	{
		this.numberValue = value;
	}

	private readonly ReadOnlySpan<byte> Utf8Value => (this.utf8Value ?? default).Span;

	public static implicit operator RequestId(ReadOnlyMemory<byte> value) => new RequestId(value);

	public static implicit operator RequestId(string value) => new RequestId(Encoding.UTF8.GetBytes(value)) { stringCache = value };

	public static implicit operator RequestId(long value) => new RequestId(value);

	public override string ToString() => this.stringCache ??= this.numberValue?.ToString() ?? (this.utf8Value.HasValue ? Encoding.UTF8.GetString(this.utf8Value.Value.Span) : "null");

	public readonly override int GetHashCode()
	{
		if (this.numberValue is long n)
		{
			return n.GetHashCode();
		}
		else if (this.utf8Value is { Span: { } utf8Value })
		{
			HashCode hash = default;
#if NET
			hash.AddBytes(utf8Value);
#else
			for (int i = 0; i < utf8Value.Length; i++)
			{
				hash.Add(utf8Value[i]);
			}
#endif
			return hash.ToHashCode();
		}
		else
		{
			return 0;
		}
	}

	public readonly override bool Equals(object? obj) => obj is RequestId other && this.Equals(other);

	public readonly bool Equals(RequestId other) => this.numberValue == other.numberValue && this.Utf8Value.SequenceEqual(other.Utf8Value);

	[EditorBrowsable(EditorBrowsableState.Never)]
	public class Converter : MessagePackConverter<RequestId>
	{
		public override RequestId Read(ref MessagePackReader reader, SerializationContext context)
		{
			return reader.NextMessagePackType switch
			{
				MessagePackType.Integer => new RequestId(reader.ReadInt64()),
				MessagePackType.String => new RequestId(reader.ReadStringSpan().ToArray()),
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
			else if (value.utf8Value is { Span: { } span })
			{
				writer.WriteString(span);
			}
			else
			{
				writer.WriteNil();
			}
		}
	}
}
