// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class RemoteCounter : IRemoteCounter
{
	private int value;

	internal TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal bool IsDisposed { get; private set; }

	public Task<int> IncrementAsync(CancellationToken cancellationToken)
	{
		if (this.IsDisposed)
		{
			throw new ObjectDisposedException(nameof(RemoteCounter));
		}

		return Task.FromResult(++this.value);
	}

	public void Dispose()
	{
		this.IsDisposed = true;
		this.Disposed.TrySetResult(true);
	}
}
