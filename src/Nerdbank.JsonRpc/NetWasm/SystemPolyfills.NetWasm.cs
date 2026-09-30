// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETWASM
#pragma warning disable SA1402, SA1403, SA1649 // multiple types/namespaces in one file

namespace System.Net
{
	/// <summary>NetWasm has no System.Net; this stands in for the BCL exception of the same name.</summary>
	internal class ProtocolViolationException : InvalidOperationException
	{
		/// <summary>Initializes a new instance of the <see cref="ProtocolViolationException"/> class.</summary>
		/// <param name="message">The message.</param>
		public ProtocolViolationException(string? message)
			: base(message)
		{
		}
	}
}

namespace Nerdbank.JsonRpc
{
	/// <summary>
	/// NetWasm's CoreLib exposes only reference-type <c>Interlocked.CompareExchange</c>/<c>Exchange</c>.
	/// NetWasm is single-threaded, so plain operations are equivalent.
	/// This type shadows <see cref="System.Threading.Interlocked"/> within this namespace.
	/// </summary>
	internal static class Interlocked
	{
		internal static int Increment(ref int location) => ++location;

		internal static long Increment(ref long location) => ++location;

		internal static int Decrement(ref int location) => --location;

		internal static long Decrement(ref long location) => --location;

		internal static int Exchange(ref int location, int value)
		{
			int original = location;
			location = value;
			return original;
		}

		internal static T Exchange<T>(ref T location, T value)
			where T : class?
		{
			T original = location;
			location = value;
			return original;
		}

		internal static int CompareExchange(ref int location, int value, int comparand)
		{
			int original = location;
			if (original == comparand)
			{
				location = value;
			}

			return original;
		}

		internal static long CompareExchange(ref long location, long value, long comparand)
		{
			long original = location;
			if (original == comparand)
			{
				location = value;
			}

			return original;
		}

		internal static T CompareExchange<T>(ref T location, T value, T comparand)
			where T : class?
		{
			T original = location;
			if (ReferenceEquals(original, comparand))
			{
				location = value;
			}

			return original;
		}
	}

	/// <summary>NetWasm's <c>System.Threading.Volatile</c> is internal. NetWasm is single-threaded, so plain reads/writes are equivalent.</summary>
	internal static class Volatile
	{
		internal static int Read(ref int location) => location;

		internal static long Read(ref long location) => location;

		internal static bool Read(ref bool location) => location;

		internal static T Read<T>(ref T location)
			where T : class?
			=> location;

		internal static void Write(ref int location, int value) => location = value;

		internal static void Write(ref bool location, bool value) => location = value;

		internal static void Write<T>(ref T location, T value)
			where T : class?
			=> location = value;
	}
}

#endif
