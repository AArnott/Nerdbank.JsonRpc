// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

/// <summary>Waits for a cancellation notification from a JSON-RPC peer.</summary>
internal sealed class CancellableTarget : ICancellableTarget
{
	internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public async ValueTask<int> WaitAsync(CancellationToken cancellationToken)
	{
		this.Started.TrySetResult(true);
		await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
		return 0;
	}
}
