// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

/// <summary>Identifies the encoding of an already serialized JSON-RPC application value.</summary>
public enum JsonRpcEncoding
{
	/// <summary>The MessagePack binary encoding.</summary>
	MessagePack,

	/// <summary>The UTF-8 JSON encoding.</summary>
	Json,
}

/// <summary>An owned, encoded application value. The default value represents absence, not JSON null.</summary>
public readonly struct JsonRpcValue : IEquatable<JsonRpcValue>
{
	private readonly byte[]? bytes;

	private JsonRpcValue(byte[] bytes, JsonRpcEncoding encoding)
	{
		this.bytes = bytes;
		this.Encoding = encoding;
	}

	/// <summary>Gets the encoding of this value.</summary>
	public JsonRpcEncoding Encoding { get; }

	/// <summary>Gets a value indicating whether this value is present.</summary>
	public bool HasValue => this.bytes is not null;

	/// <summary>Gets a copy of the encoded bytes.</summary>
	public ReadOnlyMemory<byte> Bytes => this.bytes is null ? default : (byte[])this.bytes.Clone();

	/// <summary>Gets the internally owned bytes without copying.</summary>
	internal ReadOnlyMemory<byte> OwnedBytes => this.bytes ?? ReadOnlyMemory<byte>.Empty;

	/// <summary>Converts a MessagePack raw value into a tagged value.</summary>
	/// <param name="value">The raw value.</param>
	public static implicit operator JsonRpcValue(RawMessagePack value) => FromMessagePack(value);

	/// <summary>Extracts a MessagePack value, rejecting JSON.</summary>
	/// <param name="value">The tagged value.</param>
	public static implicit operator RawMessagePack(JsonRpcValue value) => value.AsMessagePack();

	/// <summary>Copies raw UTF-8 JSON bytes into an owned buffer without validating them.</summary>
	/// <param name="utf8">A complete UTF-8 JSON value supplied by the caller.</param>
	/// <returns>The owned value.</returns>
	public static JsonRpcValue FromJson(ReadOnlyMemory<byte> utf8) => new(utf8.ToArray(), JsonRpcEncoding.Json);

	/// <summary>Copies raw MessagePack bytes into an owned buffer without validating them.</summary>
	/// <param name="value">A complete MessagePack value supplied by the caller.</param>
	/// <returns>The owned value.</returns>
	public static JsonRpcValue FromMessagePack(RawMessagePack value) => new(value.MsgPack.ToArray(), JsonRpcEncoding.MessagePack);

	/// <summary>Returns this value as MessagePack, rejecting any other encoding.</summary>
	/// <returns>The MessagePack value.</returns>
	public RawMessagePack AsMessagePack() => this.Encoding == JsonRpcEncoding.MessagePack && this.HasValue ? (RawMessagePack)this.OwnedBytes.ToArray() : throw new InvalidOperationException("Expected a MessagePack value.");

	/// <inheritdoc/>
	public bool Equals(JsonRpcValue other) => this.Encoding == other.Encoding && this.HasValue == other.HasValue && this.OwnedBytes.Span.SequenceEqual(other.OwnedBytes.Span);

	/// <inheritdoc/>
	public override bool Equals(object? obj) => obj is JsonRpcValue other && this.Equals(other);

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		HashCode hash = default(HashCode);
		hash.Add(this.Encoding);
		hash.Add(this.HasValue);
		foreach (byte b in this.OwnedBytes.Span)
		{
			hash.Add(b);
		}

		return hash.ToHashCode();
	}
}
