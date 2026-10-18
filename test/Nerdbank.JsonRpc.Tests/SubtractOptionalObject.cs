// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class SubtractOptionalObject : OptionalObject, ISubtractCapability
{
	public Task<int> CalculateAsync(int value, CancellationToken cancellationToken) => Task.FromResult(10 - value);
}
