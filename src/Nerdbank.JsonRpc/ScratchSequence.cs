// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

/// <summary>Lends a recycled <see cref="Sequence{T}"/> to one synchronous encoding operation on the calling thread.</summary>
/// <remarks>
/// A sequence carries segment bookkeeping that would otherwise be allocated for every RPC message encoded.
/// An encoding operation never yields, so a single instance per thread can serve them all; a converter that
/// encodes a nested value reenters this type and simply receives a fresh sequence.
/// </remarks>
internal readonly struct ScratchSequence : IDisposable
{
	[ThreadStatic]
	private static Sequence<byte>? cached;

	private readonly Sequence<byte> sequence;

	private ScratchSequence(Sequence<byte> sequence) => this.sequence = sequence;

	/// <summary>Gets the borrowed sequence.</summary>
	internal Sequence<byte> Sequence => this.sequence;

	/// <inheritdoc/>
	public void Dispose()
	{
		this.sequence.Reset();
		cached = this.sequence;
	}

	/// <summary>Borrows a sequence for the duration of one encoding operation.</summary>
	/// <returns>A lease that must be disposed when encoding completes.</returns>
	internal static ScratchSequence Rent()
	{
		Sequence<byte>? sequence = cached;
		if (sequence is null)
		{
			sequence = new(ArrayPool<byte>.Shared);
		}
		else
		{
			cached = null;
		}

		return new(sequence);
	}
}
