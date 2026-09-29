// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Diagnostics;
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

	/// <summary>The resources owned by this value, which most values do not have.</summary>
	/// <remarks>These are held out of line so that the many values without them stay small.</remarks>
	private readonly Attachments? attachments;

	/// <summary>The offset of this value within <see cref="storage"/>, with the encoding in the sign bit.</summary>
	/// <remarks>Packing the encoding here keeps slices from growing the struct beyond its unsliced size.</remarks>
	private readonly int offsetAndEncoding;

	/// <summary>The length of this value within <see cref="storage"/>, or 0 if this value spans all of it.</summary>
	/// <remarks>Encoded values are never empty, so 0 is free to mean "not a slice".</remarks>
	private readonly int sliceLength;

	private JsonRpcValue(object storage, JsonRpcEncoding encoding, Attachments? attachments = null)
		: this(storage, encoding, offset: 0, sliceLength: 0, attachments)
	{
	}

	private JsonRpcValue(object storage, JsonRpcEncoding encoding, int offset, int sliceLength, Attachments? attachments)
	{
		Debug.Assert(encoding is JsonRpcEncoding.MessagePack or JsonRpcEncoding.Json, "Only two encodings fit in the sign bit.");
		Debug.Assert(offset >= 0 && sliceLength >= 0, "Offsets and lengths are non-negative.");
		this.storage = storage;
		this.attachments = attachments;
		this.offsetAndEncoding = offset | (encoding == JsonRpcEncoding.Json ? int.MinValue : 0);
		this.sliceLength = sliceLength;
	}

	/// <summary>Gets the encoding of this value.</summary>
	public JsonRpcEncoding Encoding => this.offsetAndEncoding < 0 ? JsonRpcEncoding.Json : JsonRpcEncoding.MessagePack;

	/// <summary>Gets a value indicating whether this value is present.</summary>
	public bool HasValue => this.storage is not null;

	/// <summary>Gets a copy of the encoded bytes.</summary>
	public ReadOnlyMemory<byte> Bytes => this.storage is null ? default : this.OwnedBytes.ToArray();

	/// <summary>Gets the internally owned bytes without copying.</summary>
	internal ReadOnlyMemory<byte> OwnedBytes => this.storage is null ? ReadOnlyMemory<byte>.Empty
		: this.IsSlice ? GetMemory(this.storage).Slice(this.Offset, this.sliceLength)
		: GetMemory(this.storage);

	internal MarshaledObjectManager.HandleSet? MarshaledHandles => this.attachments?.MarshaledHandles;

	internal ProgressManager.RegistrationSet? ProgressRegistrations => this.attachments?.ProgressRegistrations;

	internal OutOfBandStreamManager.ChannelSet? OutOfBandChannels => this.attachments?.OutOfBandChannels;

	internal AsyncEnumerableManager.TokenSet? AsyncEnumerableTokens => this.attachments?.AsyncEnumerableTokens;

	/// <summary>Gets a value indicating whether this value shares a buffer owned by another value.</summary>
	/// <remarks>The memory is resolved from the owner on each access so that use after the owner is released fails rather than reading recycled data.</remarks>
	private bool IsSlice => this.sliceLength != 0;

	private int Offset => this.offsetAndEncoding & int.MaxValue;

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
	internal static JsonRpcValue FromOwnedBytes(byte[] bytes, JsonRpcEncoding encoding, MarshaledObjectManager.HandleSet? marshaledHandles = null) => new JsonRpcValue(bytes, encoding).WithMarshaledHandles(marshaledHandles);

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
		return new JsonRpcValue(buffer, encoding).WithMarshaledHandles(marshaledHandles);
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
		object root = owner.storage!;
		if (sequence.IsSingleSegment
			&& MemoryMarshal.TryGetArray(sequence.First, out ArraySegment<byte> slice)
			&& MemoryMarshal.TryGetArray(GetMemory(root), out ArraySegment<byte> whole)
			&& ReferenceEquals(slice.Array, whole.Array))
		{
			return CreateSlice(root, JsonRpcEncoding.MessagePack, slice.Offset - whole.Offset, slice.Count);
		}

		return new(sequence.ToArray(), JsonRpcEncoding.MessagePack);
	}

	/// <summary>Retains a range of this value's bytes as a separate value without copying.</summary>
	/// <param name="offset">The offset of the range within this value.</param>
	/// <param name="length">The length of the range.</param>
	/// <returns>A value that shares this value's backing buffer.</returns>
	internal JsonRpcValue Slice(int offset, int length) => CreateSlice(this.storage!, this.Encoding, this.Offset + offset, length);

	/// <summary>Returns the internally owned MessagePack bytes without copying.</summary>
	/// <returns>The internally owned MessagePack value.</returns>
	internal RawMessagePack AsOwnedMessagePack() => this.Encoding == JsonRpcEncoding.MessagePack && this.HasValue ? (RawMessagePack)this.OwnedBytes : throw new InvalidOperationException("Expected a MessagePack value.");

	/// <summary>Creates a copy of this value with marshaled handle ownership attached.</summary>
	/// <param name="marshaledHandles">The marshaled local handles owned by this encoded value.</param>
	/// <returns>The copied value.</returns>
	internal JsonRpcValue WithMarshaledHandles(MarshaledObjectManager.HandleSet? marshaledHandles)
		=> this.With(ReferenceEquals(marshaledHandles, MarshaledObjectManager.HandleSet.Empty) ? null : marshaledHandles, this.MarshaledHandles, static (a, v) => a with { MarshaledHandles = v });

	internal JsonRpcValue WithProgressRegistrations(ProgressManager.RegistrationSet progressRegistrations)
		=> this.With(ReferenceEquals(progressRegistrations, ProgressManager.RegistrationSet.Empty) ? null : progressRegistrations, this.ProgressRegistrations, static (a, v) => a with { ProgressRegistrations = v });

	internal JsonRpcValue WithOutOfBandChannels(OutOfBandStreamManager.ChannelSet outOfBandChannels)
		=> this.With(ReferenceEquals(outOfBandChannels, OutOfBandStreamManager.ChannelSet.Empty) ? null : outOfBandChannels, this.OutOfBandChannels, static (a, v) => a with { OutOfBandChannels = v });

	/// <summary>Creates a copy of this value with async enumerable generator ownership attached.</summary>
	/// <param name="asyncEnumerableTokens">The generator tokens encoded into this value.</param>
	/// <returns>The copied value.</returns>
	internal JsonRpcValue WithAsyncEnumerableTokens(AsyncEnumerableManager.TokenSet asyncEnumerableTokens)
		=> this.With(ReferenceEquals(asyncEnumerableTokens, AsyncEnumerableManager.TokenSet.Empty) ? null : asyncEnumerableTokens, this.AsyncEnumerableTokens, static (a, v) => a with { AsyncEnumerableTokens = v });

	/// <summary>Returns a pooled buffer that backs this value to the pool.</summary>
	/// <remarks>
	/// Only the exclusive owner of a value may call this, and only after every use of the value (and any slice of it) is complete.
	/// Values that are not backed by a pooled buffer, including slices of one, are unaffected.
	/// </remarks>
	internal void Release()
	{
		if (!this.IsSlice)
		{
			(this.storage as PooledByteBuffer)?.Release();
		}
	}

	/// <summary>Returns the pooled buffer that backs this value to the pool if it was created for a single transmission.</summary>
	internal void ReleaseIfSingleUse()
	{
		if (!this.IsSlice && this.storage is PooledByteBuffer { IsSingleUse: true } pooled)
		{
			pooled.Release();
		}
	}

	private static ReadOnlyMemory<byte> GetMemory(object storage) => storage switch
	{
		byte[] bytes => bytes,
		PooledByteBuffer pooled => pooled.Memory,
		_ => ReadOnlyMemory<byte>.Empty,
	};

	/// <summary>Creates a value that shares a range of another value's buffer.</summary>
	/// <param name="root">The <see cref="byte"/> array or <see cref="PooledByteBuffer"/> that owns the bytes.</param>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="offset">The offset of the range within the owner's memory.</param>
	/// <param name="length">The length of the range.</param>
	/// <returns>The sliced value.</returns>
	private static JsonRpcValue CreateSlice(object root, JsonRpcEncoding encoding, int offset, int length)
		=> length == 0 ? new(Array.Empty<byte>(), encoding) : new(root, encoding, offset, length, attachments: null);

	/// <summary>Creates a copy of this value with one attached resource replaced.</summary>
	/// <typeparam name="T">The type of the attached resource.</typeparam>
	/// <param name="value">The new resource, or <see langword="null"/> if there is none (empty sets are treated as none).</param>
	/// <param name="current">The resource currently attached.</param>
	/// <param name="set">Copies attachments, replacing the resource.</param>
	/// <returns>The copied value.</returns>
	private JsonRpcValue With<T>(T? value, T? current, Func<Attachments, T?, Attachments> set)
		where T : class
	{
		if (ReferenceEquals(value, current))
		{
			return this;
		}

		Attachments updated = set(this.attachments ?? Attachments.None, value);
		return new(this.storage!, this.Encoding, this.Offset, this.sliceLength, updated.IsEmpty ? null : updated);
	}

	/// <summary>The resources owned by an encoded value.</summary>
	private sealed record Attachments
	{
		internal static readonly Attachments None = new();

		internal MarshaledObjectManager.HandleSet? MarshaledHandles { get; init; }

		internal ProgressManager.RegistrationSet? ProgressRegistrations { get; init; }

		internal OutOfBandStreamManager.ChannelSet? OutOfBandChannels { get; init; }

		internal AsyncEnumerableManager.TokenSet? AsyncEnumerableTokens { get; init; }

		internal bool IsEmpty => this.MarshaledHandles is null && this.ProgressRegistrations is null && this.OutOfBandChannels is null && this.AsyncEnumerableTokens is null;
	}
}
