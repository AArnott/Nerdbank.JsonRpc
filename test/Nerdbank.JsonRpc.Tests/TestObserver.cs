// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using Microsoft.VisualStudio.Threading;

internal sealed class TestObserver : IObserver<int>, IDisposable
{
	internal TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal TaskCompletionSource<Exception> Error { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal TaskCompletionSource<int> Next { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Gets all values received, in callback order.</summary>
	internal ConcurrentQueue<int> Values { get; } = new();

	/// <summary>Gets the queue used to await individual updates.</summary>
	internal AsyncQueue<int> Updates { get; } = new();

	internal bool IsDisposed { get; private set; }

	public void Dispose() => this.IsDisposed = true;

	public void OnCompleted() => this.Completed.TrySetResult(true);

	public void OnError(Exception error) => this.Error.TrySetResult(error);

	public void OnNext(int value)
	{
		this.Values.Enqueue(value);
		this.Updates.Enqueue(value);
		this.Next.TrySetResult(value);
	}
}
