// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class CallScopedCounter : ICallScopedCounter
{
	internal int Count { get; private set; }

	public Task<int> IncrementAsync(CancellationToken cancellationToken) => Task.FromResult(++this.Count);
}
