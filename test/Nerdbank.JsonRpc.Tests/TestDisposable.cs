// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

internal class TestDisposable : IDisposableObservable
{
	private readonly TaskCompletionSource<bool> disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public bool IsDisposed => this.DisposalCount > 0;

	public int DisposalCount { get; private set; }

	internal Task Disposed => this.disposed.Task;

	public void Dispose()
	{
		this.DisposalCount++;
		this.disposed.TrySetResult(true);
	}
}
