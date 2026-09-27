// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class TestObserver : IObserver<int>, IDisposable
{
	internal TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal TaskCompletionSource<Exception> Error { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal TaskCompletionSource<int> Next { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal bool IsDisposed { get; private set; }

	public void Dispose() => this.IsDisposed = true;

	public void OnCompleted() => this.Completed.TrySetResult(true);

	public void OnError(Exception error) => this.Error.TrySetResult(error);

	public void OnNext(int value) => this.Next.TrySetResult(value);
}
