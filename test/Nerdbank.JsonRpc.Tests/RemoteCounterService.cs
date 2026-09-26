// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class RemoteCounterService : IRemoteCounterService
{
	internal RemoteCounter Counter { get; } = new();

	internal IRemoteCounter? LastExplicitProxy { get; private set; }

	internal ICallScopedCounter? LastCallScopedProxy { get; private set; }

	internal ICallScopedCounter ReturnableCallScopedCounter { get; } = new CallScopedCounter();

	public Task<IRemoteCounter> GetCounterAsync(CancellationToken cancellationToken) => Task.FromResult<IRemoteCounter>(this.Counter);

	public Task<bool> IsSameCounterAsync(IRemoteCounter counter, CancellationToken cancellationToken) => Task.FromResult(ReferenceEquals(this.Counter, counter));

	public Task FailAfterReceivingAsync(IRemoteCounter counter, CancellationToken cancellationToken)
	{
		this.LastExplicitProxy = counter;
		throw new InvalidOperationException("Simulated RPC failure after receiving the marshaled argument.");
	}

	public async Task<int> UseCallScopedCounterAsync(ICallScopedCounter counter, CancellationToken cancellationToken)
	{
		this.LastCallScopedProxy = counter;
		return await counter.IncrementAsync(cancellationToken).ConfigureAwait(false);
	}

	public Task<ICallScopedCounter> ReturnCallScopedCounterAsync(CancellationToken cancellationToken) => Task.FromResult(this.ReturnableCallScopedCounter);

	public Task<ICallScopedCounter> EchoCallScopedCounterAsync(ICallScopedCounter counter, CancellationToken cancellationToken) => Task.FromResult(counter);
}
