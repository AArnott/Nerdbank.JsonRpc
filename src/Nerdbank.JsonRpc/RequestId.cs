// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Globalization;
using System.Text;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

[GenerateShape]
[MessagePackConverter(typeof(Converter))]
public partial struct RequestId : IEquatable<RequestId>
{
	/// <summary>The <see cref="kind"/> of a signed integer ID, whose value is in <see cref="number"/>.</summary>
	private static readonly object SignedKind = new();

	/// <summary>The <see cref="kind"/> of an unsigned integer ID too large for a signed one, whose bits are in <see cref="number"/>.</summary>
	private static readonly object UnsignedKind = new();

	/// <summary>
	/// <see langword="null"/> for a null ID, <see cref="SignedKind"/> or <see cref="UnsignedKind"/> for an integer ID,
	/// or a <see cref="Utf8Id"/> for a string ID.
	/// </summary>
	/// <remarks>
	/// IDs are embedded in every message and keyed into the pending request tables,
	/// so they are kept to two fields rather than one per possible representation.
	/// </remarks>
	private readonly object? kind;

	private readonly long number;

	/// <summary>
	/// Initializes a new instance of the <see cref="RequestId"/> struct
	/// with a string value.
	/// </summary>
	/// <param name="value">The string request ID.</param>
	public RequestId(string value)
	{
		this.kind = new Utf8Id(Encoding.UTF8.GetBytes(value), value);
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="RequestId"/> struct
	/// with UTF-8 encoded bytes for a string value.
	/// </summary>
	/// <param name="value">The UTF-8 encoded bytes of the string request ID.</param>
	public RequestId(ReadOnlyMemory<byte> value)
	{
		this.kind = new Utf8Id(value, null);
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="RequestId"/> struct
	/// with an integer value.
	/// </summary>
	/// <param name="value">The request ID.</param>
	public RequestId(long value)
	{
		this.kind = SignedKind;
		this.number = value;
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="RequestId"/> struct with an unsigned integer value.
	/// </summary>
	/// <param name="value">The integer request ID.</param>
	public RequestId(ulong value)
	{
		this.kind = value <= long.MaxValue ? SignedKind : UnsignedKind;
		this.number = unchecked((long)value);
	}

	/// <summary>Gets a value indicating whether this ID is a string token.</summary>
	internal readonly bool IsString => this.kind is Utf8Id;

	/// <summary>Gets the signed integer token, when present.</summary>
	internal readonly long? SignedValue => ReferenceEquals(this.kind, SignedKind) ? this.number : null;

	/// <summary>Gets the unsigned integer token, when present.</summary>
	internal readonly ulong? UnsignedValue => ReferenceEquals(this.kind, UnsignedKind) ? unchecked((ulong)this.number) : null;

	/// <summary>Gets a value indicating whether this ID is explicitly null.</summary>
	internal readonly bool IsNull => this.kind is null;

	private readonly ReadOnlySpan<byte> Utf8Value => this.kind is Utf8Id utf8 ? utf8.Value.Span : default;

	public static implicit operator RequestId(ReadOnlyMemory<byte> value) => new RequestId(value);

	public static implicit operator RequestId(string value) => new RequestId(value);

	public static implicit operator RequestId(long value) => new RequestId(value);

	public override readonly string ToString() => this.kind switch
	{
		Utf8Id utf8 => utf8.Text,
		null => "null",
		_ when ReferenceEquals(this.kind, SignedKind) => this.number.ToString(CultureInfo.InvariantCulture),
		_ => unchecked((ulong)this.number).ToString(CultureInfo.InvariantCulture),
	};

	public readonly override int GetHashCode()
	{
		if (ReferenceEquals(this.kind, SignedKind))
		{
			HashCode hash = default;
			hash.Add(this.number);
			hash.Add(false);
			return hash.ToHashCode();
		}
		else if (ReferenceEquals(this.kind, UnsignedKind))
		{
			HashCode hash = default;
			hash.Add(unchecked((ulong)this.number));
			hash.Add(false);
			return hash.ToHashCode();
		}
		else if (this.kind is Utf8Id)
		{
			ReadOnlySpan<byte> utf8Value = this.Utf8Value;
			HashCode hash = default;
			hash.Add(true);
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

	public readonly bool Equals(RequestId other)
		=> this.kind is Utf8Id
			? other.kind is Utf8Id && this.Utf8Value.SequenceEqual(other.Utf8Value)
			: ReferenceEquals(this.kind, other.kind) && this.number == other.number;

	[EditorBrowsable(EditorBrowsableState.Never)]
#pragma warning disable NBMsgPack031 // Exactly one of the scalar ID encodings is written.
	public class Converter : MessagePackConverter<RequestId>
	{
		public override RequestId Read(ref MessagePackReader reader, SerializationContext context)
		{
			return reader.NextMessagePackType switch
			{
				MessagePackType.Integer => ReadInteger(ref reader),
				MessagePackType.String => new RequestId(reader.ReadStringSpan().ToArray()),
				MessagePackType.Nil when reader.TryReadNil() => default,
				_ => throw new MessagePackSerializationException($"Cannot convert {reader.NextMessagePackType} to RequestId."),
			};
		}

		public override void Write(ref MessagePackWriter writer, in RequestId value, SerializationContext context)
		{
			if (value.SignedValue is long n)
			{
				writer.Write(n);
			}
			else if (value.UnsignedValue is ulong unsigned)
			{
				writer.Write(unsigned);
			}
			else if (value.IsString)
			{
				writer.WriteString(value.Utf8Value);
			}
			else
			{
				writer.WriteNil();
			}
		}

		private static RequestId ReadInteger(ref MessagePackReader reader)
		{
			MessagePackReader peek = reader.CreatePeekReader();
			try
			{
				long signed = peek.ReadInt64();
				reader = peek;
				return new RequestId(signed);
			}
			catch (OverflowException)
			{
				return new RequestId(reader.ReadUInt64());
			}
		}
	}

#pragma warning restore NBMsgPack031

	/// <summary>The value of a string ID.</summary>
	/// <param name="value">The UTF-8 encoded string.</param>
	/// <param name="text">The string, if already known.</param>
	private sealed class Utf8Id(ReadOnlyMemory<byte> value, string? text)
	{
		internal ReadOnlyMemory<byte> Value => value;

		internal string Text => text ??= Encoding.UTF8.GetString(value.Span);
	}
}
