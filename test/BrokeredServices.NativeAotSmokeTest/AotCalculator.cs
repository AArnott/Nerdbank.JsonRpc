// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class AotCalculator : IAotCalculator, IAotMultiply, IObserver<int>
{
	private readonly TaskCompletionSource<int> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public event EventHandler<int>? Changed;

	internal Task<int> Observed => this.observed.Task;

	public ValueTask<int> AddAsync(int __lease, int __current, CancellationToken __enumerationCancellation)
	{
		__enumerationCancellation.ThrowIfCancellationRequested();
		int sum = __lease + __current;
		this.Changed?.Invoke(this, sum);
		return new(sum);
	}

	public ValueTask<int> MultiplyAsync(int left, int right, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return new(left * right);
	}

	public ValueTask<IDisposable?> ObserveAsync(IObserver<int> __target, int __subscription, CancellationToken __cancellationToken)
	{
		__cancellationToken.ThrowIfCancellationRequested();
		__target.OnNext(__subscription);
		return new((IDisposable?)null);
	}

	public void OnNext(int value) => this.observed.TrySetResult(value);

	public void OnError(Exception error) => this.observed.TrySetException(error);

	public void OnCompleted() => this.observed.TrySetException(new InvalidOperationException("No observed value."));
}
