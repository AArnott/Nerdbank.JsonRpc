// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;

namespace Nerdbank.JsonRpc;

/// <summary>Owns an array-pool rental used for an encoded RPC value.</summary>
internal sealed class PooledByteBuffer
{
	private readonly byte[] buffer;

	/// <summary>Initializes a new instance of the <see cref="PooledByteBuffer"/> class.</summary>
	/// <param name="length">The required buffer length.</param>
	internal PooledByteBuffer(int length)
	{
		this.buffer = ArrayPool<byte>.Shared.Rent(length);
		this.Length = length;
	}

	~PooledByteBuffer() => ArrayPool<byte>.Shared.Return(this.buffer);

	/// <summary>Gets the requested length of the rented buffer.</summary>
	internal int Length { get; }

	/// <summary>Gets the active portion of the rented buffer.</summary>
	internal Memory<byte> Memory => this.buffer.AsMemory(0, this.Length);
}
