// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class AotCalculator : IAotCalculator, IAotMultiply, IObserver<int>
{
	private readonly TaskCompletionSource<int> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public event EventHandler<int>? Changed;

	internal Task<int> Observed => this.observed.Task;

	public ValueTask<int> AddAsync(int left, int right, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		int sum = left + right;
		this.Changed?.Invoke(this, sum);
		return new(sum);
	}

	public ValueTask<int> MultiplyAsync(int left, int right, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return new(left * right);
	}

	public ValueTask<IDisposable?> ObserveAsync(IObserver<int> observer, int value, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		observer.OnNext(value);
		return new((IDisposable?)null);
	}

	public void OnNext(int value) => this.observed.TrySetResult(value);

	public void OnError(Exception error) => this.observed.TrySetException(error);

	public void OnCompleted() => this.observed.TrySetException(new InvalidOperationException("No observed value."));
}
