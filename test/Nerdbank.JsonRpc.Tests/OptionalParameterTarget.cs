// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class OptionalParameterTarget : IOptionalParameterContract
{
	public ValueTask<int> AddAsync(int left, int right, CancellationToken cancellationToken) => new(left + right);

	public ValueTask<int> GetValueAsync(int value, CancellationToken cancellationToken) => new(value);

	public Task<string?> EchoAsync(string? value, CancellationToken cancellationToken) => Task.FromResult(value);
}
