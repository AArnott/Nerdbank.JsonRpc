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
	private readonly object? storage;

	private JsonRpcValue(object storage, JsonRpcEncoding encoding, MarshaledObjectManager.HandleSet? marshaledHandles = null, ProgressManager.RegistrationSet? progressRegistrations = null, OutOfBandStreamManager.ChannelSet? outOfBandChannels = null, AsyncEnumerableManager.TokenSet? asyncEnumerableTokens = null)
	{
		this.storage = storage;
		this.Encoding = encoding;
		this.MarshaledHandles = marshaledHandles;
		this.ProgressRegistrations = progressRegistrations;
		this.OutOfBandChannels = outOfBandChannels;
		this.AsyncEnumerableTokens = asyncEnumerableTokens;
	}

	/// <summary>Gets the encoding of this value.</summary>
	public JsonRpcEncoding Encoding { get; }

	/// <summary>Gets a value indicating whether this value is present.</summary>
	public bool HasValue => this.storage is not null;

	/// <summary>Gets a copy of the encoded bytes.</summary>
	public ReadOnlyMemory<byte> Bytes => this.storage is null ? default : this.OwnedBytes.ToArray();

	/// <summary>Gets the internally owned bytes without copying.</summary>
	internal ReadOnlyMemory<byte> OwnedBytes => this.storage switch
	{
		byte[] bytes => bytes,
		PooledByteBuffer pooled => pooled.Memory,
		OwnedSlice slice => slice.Memory,
		_ => ReadOnlyMemory<byte>.Empty,
	};

	internal MarshaledObjectManager.HandleSet? MarshaledHandles { get; }

	internal ProgressManager.RegistrationSet? ProgressRegistrations { get; }

	internal OutOfBandStreamManager.ChannelSet? OutOfBandChannels { get; }

	internal AsyncEnumerableManager.TokenSet? AsyncEnumerableTokens { get; }

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

	/// <inheritdoc cref="FromJson(ReadOnlyMemory{byte})"/>
	public static JsonRpcValue FromJson(ReadOnlySequence<byte> utf8) => new(utf8.ToArray(), JsonRpcEncoding.Json);

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

	/// <summary>Wraps an exclusively owned encoded byte array without copying it.</summary>
	/// <param name="bytes">The caller-owned byte array.</param>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="marshaledHandles">The marshaled local handles owned by this encoded value.</param>
	/// <returns>The owned value.</returns>
	internal static JsonRpcValue FromOwnedBytes(byte[] bytes, JsonRpcEncoding encoding, MarshaledObjectManager.HandleSet? marshaledHandles = null) => new(bytes, encoding, marshaledHandles);

	/// <summary>Copies an encoded value into a pooled buffer.</summary>
	/// <param name="bytes">The encoded bytes.</param>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="marshaledHandles">The marshaled local handles owned by this encoded value.</param>
	/// <returns>The pooled value.</returns>
	internal static JsonRpcValue FromPooledBytes(ReadOnlySequence<byte> bytes, JsonRpcEncoding encoding, MarshaledObjectManager.HandleSet? marshaledHandles = null)
	{
		PooledByteBuffer buffer = new(checked((int)bytes.Length));
		bytes.CopyTo(buffer.Memory.Span);
		return new(buffer, encoding, marshaledHandles);
	}

	/// <summary>Copies a raw MessagePack value into a pooled buffer.</summary>
	/// <param name="value">The raw MessagePack value.</param>
	/// <returns>The pooled value.</returns>
	internal static JsonRpcValue FromPooledMessagePack(RawMessagePack value) => FromPooledBytes(value.MsgPack, JsonRpcEncoding.MessagePack);

	/// <summary>Retains a slice of an internally owned MessagePack buffer without copying.</summary>
	/// <param name="owner">The value that owns the raw value's backing buffer.</param>
	/// <param name="value">The raw value backed by the owner's buffer.</param>
	/// <returns>The owned value.</returns>
	internal static JsonRpcValue FromOwnedMessagePack(JsonRpcValue owner, RawMessagePack value)
	{
		ReadOnlySequence<byte> sequence = value.MsgPack;
		return sequence.IsSingleSegment
			? new(new OwnedSlice(owner.storage!, sequence.First), JsonRpcEncoding.MessagePack)
			: new(sequence.ToArray(), JsonRpcEncoding.MessagePack);
	}

	/// <summary>Returns the internally owned MessagePack bytes without copying.</summary>
	/// <returns>The internally owned MessagePack value.</returns>
	internal RawMessagePack AsOwnedMessagePack() => this.Encoding == JsonRpcEncoding.MessagePack && this.HasValue ? (RawMessagePack)this.OwnedBytes : throw new InvalidOperationException("Expected a MessagePack value.");

	/// <summary>Creates a copy of this value with marshaled handle ownership attached.</summary>
	/// <param name="marshaledHandles">The marshaled local handles owned by this encoded value.</param>
	/// <returns>The copied value.</returns>
	internal JsonRpcValue WithMarshaledHandles(MarshaledObjectManager.HandleSet marshaledHandles) => new(this.storage!, this.Encoding, marshaledHandles, this.ProgressRegistrations, this.OutOfBandChannels, this.AsyncEnumerableTokens);

	internal JsonRpcValue WithProgressRegistrations(ProgressManager.RegistrationSet progressRegistrations) => new(this.storage!, this.Encoding, this.MarshaledHandles, progressRegistrations, this.OutOfBandChannels, this.AsyncEnumerableTokens);

	internal JsonRpcValue WithOutOfBandChannels(OutOfBandStreamManager.ChannelSet outOfBandChannels) => new(this.storage!, this.Encoding, this.MarshaledHandles, this.ProgressRegistrations, outOfBandChannels, this.AsyncEnumerableTokens);

	/// <summary>Creates a copy of this value with async enumerable generator ownership attached.</summary>
	/// <param name="asyncEnumerableTokens">The generator tokens encoded into this value.</param>
	/// <returns>The copied value.</returns>
	internal JsonRpcValue WithAsyncEnumerableTokens(AsyncEnumerableManager.TokenSet asyncEnumerableTokens) => new(this.storage!, this.Encoding, this.MarshaledHandles, this.ProgressRegistrations, this.OutOfBandChannels, asyncEnumerableTokens);

	private sealed class OwnedSlice(object owner, ReadOnlyMemory<byte> memory)
	{
		internal object Owner { get; } = owner;

		internal ReadOnlyMemory<byte> Memory { get; } = memory;
	}
}
