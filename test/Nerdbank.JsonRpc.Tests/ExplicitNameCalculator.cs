// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class ExplicitNameCalculator : IExplicitNameCalculator
{
	public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);
}
