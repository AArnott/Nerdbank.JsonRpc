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

	/// <summary>The resources owned by this value, which most values do not have.</summary>
	/// <remarks>These are held out of line so that the many values without them stay small.</remarks>
	private readonly Attachments? attachments;

	private JsonRpcValue(object storage, JsonRpcEncoding encoding, Attachments? attachments = null)
	{
		this.storage = storage;
		this.Encoding = encoding;
		this.attachments = attachments;
	}

	/// <summary>Gets the encoding of this value.</summary>
	public JsonRpcEncoding Encoding { get; }

	/// <summary>Gets a value indicating whether this value is present.</summary>
	public bool HasValue => this.storage is not null;

	/// <summary>Gets a copy of the encoded bytes.</summary>
	public ReadOnlyMemory<byte> Bytes => this.storage is null ? default : this.OwnedBytes.ToArray();

	/// <summary>Gets the internally owned bytes without copying.</summary>
	internal ReadOnlyMemory<byte> OwnedBytes => this.storage is null ? ReadOnlyMemory<byte>.Empty : GetMemory(this.storage);

	internal MarshaledObjectManager.HandleSet? MarshaledHandles => this.attachments?.MarshaledHandles;

	internal ProgressManager.RegistrationSet? ProgressRegistrations => this.attachments?.ProgressRegistrations;

	internal OutOfBandStreamManager.ChannelSet? OutOfBandChannels => this.attachments?.OutOfBandChannels;

	internal AsyncEnumerableManager.TokenSet? AsyncEnumerableTokens => this.attachments?.AsyncEnumerableTokens;

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

	/// <summary>Retains a range of this value's bytes as a separate value without copying.</summary>
	/// <param name="offset">The offset of the range within this value.</param>
	/// <param name="length">The length of the range.</param>
	/// <returns>A value that shares this value's backing buffer.</returns>
	internal JsonRpcValue Slice(int offset, int length)
	{
		(object root, int baseOffset) = this.storage is OwnedSlice parent ? (parent.Owner, parent.Offset) : (this.storage!, 0);
		return new(new OwnedSlice(root, baseOffset + offset, length), this.Encoding);
	}

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
		return new(this.storage!, this.Encoding, updated.IsEmpty ? null : updated);
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

	/// <summary>A slice of a buffer owned by another value.</summary>
	/// <param name="owner">The <see cref="byte"/> array or <see cref="PooledByteBuffer"/> that owns the bytes.</param>
	/// <param name="offset">The offset of the slice within the owner's memory.</param>
	/// <param name="length">The length of the slice.</param>
	/// <remarks>The memory is resolved from the owner on each access so that use after the owner is released fails rather than reading recycled data.</remarks>
	private sealed class OwnedSlice(object owner, int offset, int length)
	{
		internal object Owner { get; } = owner;

		internal int Offset => offset;

		internal ReadOnlyMemory<byte> Memory => GetMemory(this.Owner).Slice(offset, length);
	}
}
