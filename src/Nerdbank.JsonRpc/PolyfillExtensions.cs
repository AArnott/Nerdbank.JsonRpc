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

	internal static void Deconstruct<K, V>(this KeyValuePair<K, V> pair, out K key, out V value) => (key, value) = (pair.Key, pair.Value);
}

#endif
