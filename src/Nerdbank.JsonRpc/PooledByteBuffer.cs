// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;

namespace Nerdbank.JsonRpc;

/// <summary>Owns an array-pool rental used for an encoded RPC value.</summary>
/// <remarks>
/// The rental is returned to the pool only by an explicit call to <see cref="Release"/> from the code that exclusively owns the value.
/// A buffer that is never released is simply collected by the GC.
/// </remarks>
internal sealed class PooledByteBuffer
{
	private byte[]? buffer;

	/// <summary>Initializes a new instance of the <see cref="PooledByteBuffer"/> class.</summary>
	/// <param name="length">The required buffer length.</param>
	/// <param name="singleUse">A value indicating whether the transport may release the buffer after it has been transmitted once.</param>
	internal PooledByteBuffer(int length, bool singleUse)
	{
		this.buffer = ArrayPool<byte>.Shared.Rent(length);
		this.Length = length;
		this.IsSingleUse = singleUse;
	}

	/// <summary>Gets the requested length of the rented buffer.</summary>
	internal int Length { get; }

	/// <summary>Gets a value indicating whether the transport may release the buffer after it has been transmitted once.</summary>
	internal bool IsSingleUse { get; }

	/// <summary>Gets the active portion of the rented buffer.</summary>
	/// <exception cref="ObjectDisposedException">Thrown if the buffer has been released.</exception>
	internal Memory<byte> Memory => (this.buffer ?? throw new ObjectDisposedException(nameof(PooledByteBuffer))).AsMemory(0, this.Length);

	/// <summary>Returns the rented buffer to the pool. Subsequent access to <see cref="Memory"/> throws.</summary>
	internal void Release()
	{
		if (Interlocked.Exchange(ref this.buffer, null) is byte[] buffer)
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}
}
