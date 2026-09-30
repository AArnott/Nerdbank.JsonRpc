// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETWASM

using System.Diagnostics.CodeAnalysis;
#pragma warning disable SA1402, SA1649, SA1600, SA1611, SA1615, SA1618, SA1201, SA1202, SA1204, CS1591

// NetWasm (netwasm0.1) proof of concept: there is no System.Threading.Channels port for NetWasm.
// This is a minimal, internal, single-threaded-friendly implementation of the subset Nerdbank.JsonRpc uses.
// Continuations always run asynchronously to match the RunContinuationsAsynchronously behavior of the real channels.
namespace System.Threading.Channels;

internal sealed class ChannelClosedException : InvalidOperationException
{
	public ChannelClosedException()
		: base("The channel has been closed.")
	{
	}

	public ChannelClosedException(Exception? innerException)
		: base("The channel has been closed.", innerException)
	{
	}
}

internal abstract class ChannelOptions
{
	public bool SingleWriter { get; set; }

	public bool SingleReader { get; set; }

	public bool AllowSynchronousContinuations { get; set; }
}

internal sealed class UnboundedChannelOptions : ChannelOptions
{
}

internal sealed class BoundedChannelOptions : ChannelOptions
{
	public BoundedChannelOptions(int capacity)
	{
		if (capacity < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(capacity));
		}

		this.Capacity = capacity;
	}

	public int Capacity { get; set; }
}

internal abstract class ChannelReader<T>
{
	public abstract Task Completion { get; }

	public abstract bool TryRead([MaybeNullWhen(false)] out T item);

	public abstract ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default);

	public virtual async ValueTask<T> ReadAsync(CancellationToken cancellationToken = default)
	{
		while (true)
		{
			if (this.TryRead(out T? item))
			{
				return item;
			}

			if (!await this.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
			{
				throw new ChannelClosedException();
			}
		}
	}

	public virtual async IAsyncEnumerable<T> ReadAllAsync([Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		while (await this.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
		{
			while (this.TryRead(out T? item))
			{
				yield return item;
			}
		}
	}
}

internal abstract class ChannelWriter<T>
{
	public abstract bool TryWrite(T item);

	public abstract ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default);

	public abstract bool TryComplete(Exception? error = null);

	public void Complete(Exception? error = null)
	{
		if (!this.TryComplete(error))
		{
			throw new ChannelClosedException();
		}
	}

	public virtual async ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
	{
		while (true)
		{
			if (this.TryWrite(item))
			{
				return;
			}

			if (!await this.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
			{
				throw new ChannelClosedException();
			}
		}
	}
}

internal abstract class Channel<T> : Channel<T, T>
{
}

internal abstract class Channel<TWrite, TRead>
{
	public ChannelReader<TRead> Reader { get; protected set; } = null!;

	public ChannelWriter<TWrite> Writer { get; protected set; } = null!;
}

internal static class Channel
{
	public static Channel<T> CreateUnbounded<T>() => new QueueChannel<T>(null);

	public static Channel<T> CreateUnbounded<T>(UnboundedChannelOptions options) => new QueueChannel<T>(null);

	public static Channel<T> CreateBounded<T>(int capacity) => new QueueChannel<T>(capacity);

	public static Channel<T> CreateBounded<T>(BoundedChannelOptions options) => new QueueChannel<T>(options.Capacity);

	private sealed class QueueChannel<T> : Channel<T>
	{
		private readonly Queue<T> items = new();
		private readonly int? capacity;
		private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private TaskCompletionSource<bool>? readerWaiter;
		private TaskCompletionSource<bool>? writerWaiter;
		private bool writingCompleted;
		private Exception? completionError;

		internal QueueChannel(int? capacity)
		{
			this.capacity = capacity;
			this.Reader = new QueueReader(this);
			this.Writer = new QueueWriter(this);
		}

		private static void Release(ref TaskCompletionSource<bool>? waiter, bool result)
		{
			TaskCompletionSource<bool>? w = waiter;
			waiter = null;
			w?.TrySetResult(result);
		}

		private static async ValueTask<bool> WaitAsync(Task<bool> task, CancellationToken cancellationToken)
		{
			if (!cancellationToken.CanBeCanceled || task.IsCompleted)
			{
				return await task.ConfigureAwait(false);
			}

			TaskCompletionSource<bool> canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
			using (cancellationToken.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), canceled))
			{
				Task winner = await Task.WhenAny(task, canceled.Task).ConfigureAwait(false);
				return await ((Task<bool>)winner).ConfigureAwait(false);
			}
		}

		private void CompleteIfDrained()
		{
			if (this.writingCompleted && this.items.Count == 0)
			{
				if (this.completionError is OperationCanceledException oce)
				{
					this.completion.TrySetCanceled(oce.CancellationToken);
				}
				else if (this.completionError is not null)
				{
					this.completion.TrySetException(this.completionError);
				}
				else
				{
					this.completion.TrySetResult(true);
				}
			}
		}

		private sealed class QueueReader(QueueChannel<T> owner) : ChannelReader<T>
		{
			public override Task Completion => owner.completion.Task;

			public override bool TryRead([MaybeNullWhen(false)] out T item)
			{
				lock (owner.items)
				{
					if (owner.items.Count > 0)
					{
						item = owner.items.Dequeue();
						Release(ref owner.writerWaiter, true);
						owner.CompleteIfDrained();
						return true;
					}

					item = default;
					return false;
				}
			}

			public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					return ValueTask.FromCanceled<bool>(cancellationToken);
				}

				lock (owner.items)
				{
					if (owner.items.Count > 0)
					{
						return new(true);
					}

					if (owner.writingCompleted)
					{
						return owner.completionError is { } error && error is not ChannelClosedException
							? ValueTask.FromException<bool>(new ChannelClosedException(error))
							: new(false);
					}

					owner.readerWaiter ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
					return WaitAsync(owner.readerWaiter.Task, cancellationToken);
				}
			}
		}

		private sealed class QueueWriter(QueueChannel<T> owner) : ChannelWriter<T>
		{
			public override bool TryWrite(T item)
			{
				lock (owner.items)
				{
					if (owner.writingCompleted || (owner.capacity is int cap && owner.items.Count >= cap))
					{
						return false;
					}

					owner.items.Enqueue(item);
					Release(ref owner.readerWaiter, true);
					return true;
				}
			}

			public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					return ValueTask.FromCanceled<bool>(cancellationToken);
				}

				lock (owner.items)
				{
					if (owner.writingCompleted)
					{
						return new(false);
					}

					if (owner.capacity is not int cap || owner.items.Count < cap)
					{
						return new(true);
					}

					owner.writerWaiter ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
					return WaitAsync(owner.writerWaiter.Task, cancellationToken);
				}
			}

			public override bool TryComplete(Exception? error = null)
			{
				lock (owner.items)
				{
					if (owner.writingCompleted)
					{
						return false;
					}

					owner.writingCompleted = true;
					owner.completionError = error;
					Release(ref owner.readerWaiter, false);
					Release(ref owner.writerWaiter, false);
					owner.CompleteIfDrained();
					return true;
				}
			}
		}
	}
}
#endif
