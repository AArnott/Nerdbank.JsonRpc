// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if !NET

using System.Text;

namespace Nerdbank.JsonRpc;

internal static class PolyfillExtensions
{
	internal static unsafe int GetBytes(this Encoding encoding, ReadOnlySpan<char> source, Span<byte> destination)
	{
		fixed (char* pSource = source)
		{
			fixed (byte* pDestination = destination)
			{
				return encoding.GetBytes(pSource, source.Length, pDestination, destination.Length);
			}
		}
	}

	internal static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value) => (key, value) = (pair.Key, pair.Value);
}

#endif
