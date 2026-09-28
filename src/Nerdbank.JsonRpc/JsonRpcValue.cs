// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Runtime.InteropServices;
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
	internal ReadOnlyMemory<byte> OwnedBytes => this.storage is null ? ReadOnlyMemory<byte>.Empty : GetMemory(this.storage);

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
	internal static JsonRpcValue FromPooledBytes(ReadOnlySequence<byte> bytes, JsonRpcEncoding encoding, MarshaledObjectManager.HandleSet? marshaledHandles = null) => FromPooledBytes(bytes, encoding, singleUse: false, marshaledHandles);

	/// <summary>Copies an encoded value into a pooled buffer.</summary>
	/// <param name="bytes">The encoded bytes.</param>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="singleUse">A value indicating whether the transport may return the buffer to the pool once it has been transmitted.</param>
	/// <param name="marshaledHandles">The marshaled local handles owned by this encoded value.</param>
	/// <returns>The pooled value.</returns>
	internal static JsonRpcValue FromPooledBytes(ReadOnlySequence<byte> bytes, JsonRpcEncoding encoding, bool singleUse, MarshaledObjectManager.HandleSet? marshaledHandles = null)
	{
		PooledByteBuffer buffer = new(checked((int)bytes.Length), singleUse);
		bytes.CopyTo(buffer.Memory.Span);
		return new(buffer, encoding, marshaledHandles);
	}

	/// <summary>Copies a raw MessagePack value into a pooled buffer.</summary>
	/// <param name="value">The raw MessagePack value.</param>
	/// <returns>The pooled value, which its consumer may <see cref="Release">release</see> once it is no longer needed.</returns>
	internal static JsonRpcValue FromPooledMessagePack(RawMessagePack value) => FromPooledBytes(value.MsgPack, JsonRpcEncoding.MessagePack);

	/// <summary>Retains a slice of an internally owned MessagePack buffer without copying.</summary>
	/// <param name="owner">The value that owns the raw value's backing buffer.</param>
	/// <param name="value">The raw value backed by the owner's buffer.</param>
	/// <returns>The owned value.</returns>
	internal static JsonRpcValue FromOwnedMessagePack(JsonRpcValue owner, RawMessagePack value)
	{
		ReadOnlySequence<byte> sequence = value.MsgPack;
		object root = owner.storage is OwnedSlice parent ? parent.Owner : owner.storage!;
		if (sequence.IsSingleSegment
			&& MemoryMarshal.TryGetArray(sequence.First, out ArraySegment<byte> slice)
			&& MemoryMarshal.TryGetArray(GetMemory(root), out ArraySegment<byte> whole)
			&& ReferenceEquals(slice.Array, whole.Array))
		{
			return new(new OwnedSlice(root, slice.Offset - whole.Offset, slice.Count), JsonRpcEncoding.MessagePack);
		}

		return new(sequence.ToArray(), JsonRpcEncoding.MessagePack);
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

	/// <summary>Returns a pooled buffer that backs this value to the pool.</summary>
	/// <remarks>
	/// Only the exclusive owner of a value may call this, and only after every use of the value (and any slice of it) is complete.
	/// Values that are not backed by a pooled buffer are unaffected.
	/// </remarks>
	internal void Release() => (this.storage as PooledByteBuffer)?.Release();

	/// <summary>Returns the pooled buffer that backs this value to the pool if it was created for a single transmission.</summary>
	internal void ReleaseIfSingleUse()
	{
		if (this.storage is PooledByteBuffer { IsSingleUse: true } pooled)
		{
			pooled.Release();
		}
	}

	private static ReadOnlyMemory<byte> GetMemory(object storage) => storage switch
	{
		byte[] bytes => bytes,
		PooledByteBuffer pooled => pooled.Memory,
		OwnedSlice slice => slice.Memory,
		_ => ReadOnlyMemory<byte>.Empty,
	};

	/// <summary>A slice of a buffer owned by another value.</summary>
	/// <param name="owner">The <see cref="byte"/> array or <see cref="PooledByteBuffer"/> that owns the bytes.</param>
	/// <param name="offset">The offset of the slice within the owner's memory.</param>
	/// <param name="length">The length of the slice.</param>
	/// <remarks>The memory is resolved from the owner on each access so that use after the owner is released fails rather than reading recycled data.</remarks>
	private sealed class OwnedSlice(object owner, int offset, int length)
	{
		internal object Owner { get; } = owner;

		internal ReadOnlyMemory<byte> Memory => GetMemory(this.Owner).Slice(offset, length);
	}
}
