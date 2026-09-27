// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class ObserverService : IObserverService
{
	internal IObserver<int>? Subscriber { get; private set; }

	internal TestObserver OwnedObserver { get; } = new();

	public Task SubscribeAsync(IObserver<int> observer, CancellationToken cancellationToken)
	{
		this.Subscriber = observer;
		return Task.CompletedTask;
	}

	public Task<IObserver<int>> GetObserverAsync(CancellationToken cancellationToken) => Task.FromResult<IObserver<int>>(this.OwnedObserver);

	public Task<bool> IsOwnedObserverAsync(IObserver<int> observer, CancellationToken cancellationToken) => Task.FromResult(ReferenceEquals(observer, this.OwnedObserver));

	public Task FailAfterReceivingAsync(IObserver<int> observer, CancellationToken cancellationToken)
	{
		this.Subscriber = observer;
		throw new InvalidOperationException("Subscription failed.");
	}
}
