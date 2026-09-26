// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class RemoteCounterService : IRemoteCounterService
{
	internal RemoteCounter Counter { get; } = new();

	public Task<IRemoteCounter> GetCounterAsync(CancellationToken cancellationToken) => Task.FromResult<IRemoteCounter>(this.Counter);

	public Task<bool> IsSameCounterAsync(IRemoteCounter counter, CancellationToken cancellationToken) => Task.FromResult(ReferenceEquals(this.Counter, counter));
}
