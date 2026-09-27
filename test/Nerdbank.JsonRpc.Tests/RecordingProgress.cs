// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class RecordingProgress : IProgress<int>
{
	private readonly List<int> values = [];

	internal IReadOnlyList<int> Values
	{
		get
		{
			lock (this.values)
			{
				return [.. this.values];
			}
		}
	}

	public void Report(int value)
	{
		lock (this.values)
		{
			this.values.Add(value);
		}
	}
}
