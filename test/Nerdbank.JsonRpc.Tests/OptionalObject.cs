// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal class OptionalObject : IOptionalObject
{
	public virtual Task<int> GetValueAsync(CancellationToken cancellationToken) => Task.FromResult(10);

	public void Dispose()
	{
	}
}
