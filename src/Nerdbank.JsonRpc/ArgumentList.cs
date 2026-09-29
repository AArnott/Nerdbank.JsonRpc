// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;

namespace Nerdbank.JsonRpc;

/// <summary>The individual arguments of a received request, held in a pooled array.</summary>
internal readonly struct ArgumentList
{
	private readonly (string? Name, JsonRpcValue Value)[]? array;

	/// <summary>Initializes a new instance of the <see cref="ArgumentList"/> struct.</summary>
	/// <param name="array">An array from <see cref="Rent"/>.</param>
	/// <param name="count">The number of initialized arguments in <paramref name="array"/>.</param>
	/// <param name="named">Whether the arguments were supplied by name.</param>
	internal ArgumentList((string? Name, JsonRpcValue Value)[] array, int count, bool named)
	{
		this.array = array;
		this.Count = count;
		this.Named = named;
	}

	/// <summary>Gets the number of arguments.</summary>
	internal int Count { get; }

	/// <summary>Gets a value indicating whether the arguments were supplied by name.</summary>
	internal bool Named { get; }

	/// <summary>Gets a value indicating whether this is the <see langword="default"/> value, which represents no argument list at all.</summary>
	internal bool IsDefault => this.array is null;

	/// <summary>Gets an argument.</summary>
	/// <param name="index">The position of the argument.</param>
	internal ref readonly (string? Name, JsonRpcValue Value) this[int index] => ref this.AsSpan()[index];

	/// <summary>Enumerates the arguments.</summary>
	/// <returns>The enumerator.</returns>
	public ReadOnlySpan<(string? Name, JsonRpcValue Value)>.Enumerator GetEnumerator() => this.AsSpan().GetEnumerator();

	/// <summary>Rents an array that can hold at least the specified number of arguments.</summary>
	/// <param name="minimumLength">The minimum number of arguments the array must hold.</param>
	/// <returns>The array, which may be longer than requested.</returns>
	internal static (string? Name, JsonRpcValue Value)[] Rent(int minimumLength)
		=> minimumLength == 0 ? [] : ArrayPool<(string? Name, JsonRpcValue Value)>.Shared.Rent(minimumLength);

	/// <summary>Replaces a rented array with a larger one that retains its initialized elements.</summary>
	/// <param name="array">The rented array.</param>
	/// <param name="count">The number of initialized elements.</param>
	internal static void Grow(ref (string? Name, JsonRpcValue Value)[] array, int count)
	{
		(string? Name, JsonRpcValue Value)[] larger = Rent(Math.Max(4, array.Length * 2));
		Array.Copy(array, larger, count);
		new ArgumentList(array, count, named: false).Return();
		array = larger;
	}

	/// <summary>Returns the array to the pool. Neither this value nor any copy of it may be used afterward.</summary>
	internal void Return()
	{
		if (this.array is { Length: > 0 } array)
		{
			// Clear the arguments so that the pool does not keep their buffers alive.
			Array.Clear(array, 0, this.Count);
			ArrayPool<(string? Name, JsonRpcValue Value)>.Shared.Return(array);
		}
	}

	private ReadOnlySpan<(string? Name, JsonRpcValue Value)> AsSpan() => this.array.AsSpan(0, this.Count);
}
