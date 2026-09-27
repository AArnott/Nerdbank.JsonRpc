// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class RecordingProgress<T> : IProgress<T>
{
	private readonly List<T> values = [];

	internal IReadOnlyList<T> Values
	{
		get
		{
			lock (this.values)
			{
				return [.. this.values];
			}
		}
	}

	public void Report(T value)
	{
		lock (this.values)
		{
			this.values.Add(value);
		}
	}
}
