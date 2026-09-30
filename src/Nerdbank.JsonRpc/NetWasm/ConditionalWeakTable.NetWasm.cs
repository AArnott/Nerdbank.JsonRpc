// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETWASM
#pragma warning disable SA1402, SA1649, SA1600, SA1611, SA1615, SA1618, CS1591

// NetWasm (netwasm0.1) proof of concept: CoreLib has no ConditionalWeakTable (and no weak GC handles).
// This stand-in holds its keys strongly, so entries live until explicitly removed.
namespace System.Runtime.CompilerServices;

using System.Diagnostics.CodeAnalysis;

internal sealed class ConditionalWeakTable<TKey, TValue>
	where TKey : class
	where TValue : class?
{
	private readonly Dictionary<TKey, TValue> map = new(ReferenceEqualityComparer.Instance);

	public delegate TValue CreateValueCallback(TKey key);

	public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
	{
		lock (this.map)
		{
			return this.map.TryGetValue(key, out value);
		}
	}

	public void Add(TKey key, TValue value)
	{
		lock (this.map)
		{
			this.map.Add(key, value);
		}
	}

	public void AddOrUpdate(TKey key, TValue value)
	{
		lock (this.map)
		{
			this.map[key] = value;
		}
	}

	public bool Remove(TKey key)
	{
		lock (this.map)
		{
			return this.map.Remove(key);
		}
	}

	public TValue GetValue(TKey key, CreateValueCallback createValueCallback)
	{
		lock (this.map)
		{
			if (!this.map.TryGetValue(key, out TValue? value))
			{
				value = createValueCallback(key);
				this.map.Add(key, value);
			}

			return value;
		}
	}
}
#endif
