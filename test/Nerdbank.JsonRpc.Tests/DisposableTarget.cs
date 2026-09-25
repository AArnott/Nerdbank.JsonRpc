// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class DisposableTarget : IDisposableContract
{
	internal TestDisposable ReturnedDisposable { get; } = new();

	internal TaskCompletionSource<SerializableDisposable> ConcreteDisposableReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public Task<IDisposable> GetDisposableAsync(CancellationToken cancellationToken) => Task.FromResult<IDisposable>(this.ReturnedDisposable);

	public Task UseDisposableAsync(IDisposable value, CancellationToken cancellationToken)
	{
		value.Dispose();
		return Task.CompletedTask;
	}

	public Task UseDisposableContainerAsync(DisposableContainer value, CancellationToken cancellationToken)
	{
		value.Value.Dispose();
		return Task.CompletedTask;
	}

	public Task UseConcreteDisposableContainerAsync(ConcreteDisposableContainer value, CancellationToken cancellationToken)
	{
		value.Value.Dispose();
		this.ConcreteDisposableReceived.TrySetResult(value.Value);
		return Task.CompletedTask;
	}
}
