// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class ObserverService : IObserverService
{
	private readonly object sync = new();
	private readonly List<IObserver<int>> subscribers = [];

	/// <summary>Gets a task completed when the server has removed a subscription.</summary>
	internal TaskCompletionSource<bool> Unsubscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal IObserver<int>? Subscriber { get; private set; }

	internal TestObserver OwnedObserver { get; } = new();

	public Task<IDisposable> SubscribeAsync(IObserver<int> observer, CancellationToken cancellationToken)
	{
		lock (this.sync)
		{
			this.Subscriber = observer;
			this.subscribers.Add(observer);
		}

		return Task.FromResult<IDisposable>(new Subscription(this, observer));
	}

	public Task<IObserver<int>> GetObserverAsync(CancellationToken cancellationToken) => Task.FromResult<IObserver<int>>(this.OwnedObserver);

	public Task<bool> IsOwnedObserverAsync(IObserver<int> observer, CancellationToken cancellationToken) => Task.FromResult(ReferenceEquals(observer, this.OwnedObserver));

	public Task FailAfterReceivingAsync(IObserver<int> observer, CancellationToken cancellationToken)
	{
		this.Subscriber = observer;
		throw new InvalidOperationException("Subscription failed.");
	}

	/// <summary>Publishes a value to all active subscribers.</summary>
	/// <param name="value">The value to publish.</param>
	internal void Publish(int value)
	{
		lock (this.sync)
		{
			foreach (IObserver<int> observer in this.subscribers)
			{
				observer.OnNext(value);
			}
		}
	}

	private sealed class Subscription(ObserverService owner, IObserver<int> observer) : IDisposable
	{
		public void Dispose()
		{
			lock (owner.sync)
			{
				if (owner.subscribers.Remove(observer))
				{
					owner.Unsubscribed.TrySetResult(true);
				}
			}
		}
	}
}
