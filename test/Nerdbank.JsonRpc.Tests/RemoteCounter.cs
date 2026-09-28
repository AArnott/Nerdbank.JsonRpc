// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class RemoteCounter : IRemoteCounter
{
	private int value;

	internal TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal bool IsDisposed { get; private set; }

	internal TaskCompletionSource<bool>? IncrementStarted { get; set; }

	internal TaskCompletionSource<bool>? ContinueIncrement { get; set; }

	public async Task<int> IncrementAsync(CancellationToken cancellationToken)
	{
		if (this.IsDisposed)
		{
			throw new ObjectDisposedException(nameof(RemoteCounter));
		}

		this.IncrementStarted?.TrySetResult(true);
		if (this.ContinueIncrement is { } continueIncrement)
		{
			await continueIncrement.Task.ConfigureAwait(false);
		}

		return ++this.value;
	}

	public void Dispose()
	{
		this.IsDisposed = true;
		this.Disposed.TrySetResult(true);
	}
}
