// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Text.Json;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Maps the UTF-8 encoding of short protocol strings, such as method and parameter names, to previously decoded strings
/// so that each received message need not allocate them again.
/// </summary>
/// <remarks>
/// The cache is direct-mapped: each string may only occupy the slot its hash selects, and a newcomer simply replaces the
/// previous occupant. Memory use is therefore bounded no matter how many distinct names a remote party sends, and lookups
/// never lock. Entries are immutable, so a racing reader observes either the old or the new entry, never a torn one.
/// </remarks>
internal static class Utf8StringCache
{
	/// <summary>The number of slots, which must be a power of two.</summary>
	private const int SlotCount = 256;

	/// <summary>The longest UTF-8 encoding that will be cached.</summary>
	private const int MaxCachedByteLength = 128;

	private static readonly Entry?[] Slots = new Entry?[SlotCount];

	/// <summary>Gets the string for a UTF-8 encoded msgpack string token, which may be nil.</summary>
	/// <param name="reader">The reader, positioned at a string or nil token.</param>
	/// <returns>The decoded string, or <see langword="null"/> for a nil token.</returns>
	internal static string? ReadString(ref MessagePackReader reader)
	{
		if (reader.ReadStringSequence() is not ReadOnlySequence<byte> sequence)
		{
			return null;
		}

		return sequence.IsSingleSegment ? GetOrAdd(sequence.First.Span) : StringEncoding.UTF8.GetString(sequence.ToArray());
	}

	/// <summary>Gets the string for a JSON string token.</summary>
	/// <param name="reader">The reader, positioned at a string token.</param>
	/// <returns>The decoded string.</returns>
	internal static string ReadString(ref Utf8JsonReader reader)
	{
		if (reader.HasValueSequence || reader.ValueIsEscaped || reader.ValueSpan.Length > MaxCachedByteLength)
		{
			return reader.GetString()!;
		}

		ReadOnlySpan<byte> utf8 = reader.ValueSpan;
		ref Entry? slot = ref GetSlot(utf8);
		if (Volatile.Read(ref slot) is Entry entry && utf8.SequenceEqual(entry.Utf8))
		{
			return entry.Value;
		}

		// Decode through the reader so that validation is unchanged; only bytes it accepted are ever cached.
		string value = reader.GetString()!;
		Volatile.Write(ref slot, new Entry(utf8.ToArray(), value));
		return value;
	}

	/// <summary>Gets the string for a UTF-8 encoded span, decoding it only if it is not already cached.</summary>
	/// <param name="utf8">The UTF-8 encoded string.</param>
	/// <returns>The decoded string.</returns>
	internal static string GetOrAdd(ReadOnlySpan<byte> utf8)
	{
		if (utf8.Length > MaxCachedByteLength)
		{
			return StringEncoding.UTF8.GetString(utf8);
		}

		ref Entry? slot = ref GetSlot(utf8);
		if (Volatile.Read(ref slot) is Entry entry && utf8.SequenceEqual(entry.Utf8))
		{
			return entry.Value;
		}

		string value = StringEncoding.UTF8.GetString(utf8);
		Volatile.Write(ref slot, new Entry(utf8.ToArray(), value));
		return value;
	}

	private static ref Entry? GetSlot(ReadOnlySpan<byte> utf8)
	{
		// FNV-1a: cheap for short names, and adversarial collisions can only cost cache misses, never unbounded work.
		uint hash = 2166136261;
		foreach (byte b in utf8)
		{
			hash = unchecked((hash ^ b) * 16777619);
		}

		return ref Slots[hash & (SlotCount - 1)];
	}

	/// <summary>An immutable pairing of a string with its UTF-8 encoding.</summary>
	/// <param name="Utf8">The UTF-8 encoding.</param>
	/// <param name="Value">The decoded string.</param>
	private sealed record Entry(byte[] Utf8, string Value);
}
