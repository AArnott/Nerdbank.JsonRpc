// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>
/// The cancellation source for one inbound request.
/// A prefetched async iterator keeps a lease so the source outlives the request that created it
/// and can still be canceled when the connection drops.
/// </summary>
internal sealed class InboundRequestCancellation
{
	private readonly CancellationTokenSource source = new();
	private readonly object gate = new();
	private int remaining = 1;
	private bool enumerationArmed;
	private bool cancelStarted;
	private bool cancelFinished;
	private bool disposeRequested;

	/// <summary>Gets the source canceled when the request or a surviving iterator is canceled.</summary>
	internal CancellationTokenSource Source => this.source;

	/// <summary>Gets the token handed to the target method.</summary>
	internal CancellationToken Token => this.source.Token;

	/// <summary>
	/// Holds the source for a prefetched iterator and makes a later <see cref="Cancel"/> leave the caller's stack.
	/// </summary>
	/// <returns>
	/// <see langword="false"/> when cancellation has already started on the caller's stack,
	/// so the iterator must not attach callbacks to <see cref="Token"/> from this call.
	/// </returns>
	internal bool TryArmEnumeration()
	{
		lock (this.gate)
		{
			if (this.cancelStarted && !this.enumerationArmed)
			{
				return false;
			}

			this.remaining++;
			this.enumerationArmed = true;
			return true;
		}
	}

	/// <summary>
	/// Cancels the request.
	/// An armed iterator is canceled from the thread pool so a callback cannot block connection shutdown.
	/// The source stays alive until those callbacks return.
	/// </summary>
	internal void Cancel()
	{
		bool queue;
		lock (this.gate)
		{
			if (this.cancelStarted)
			{
				return;
			}

			this.cancelStarted = true;
			queue = this.enumerationArmed;
		}

		if (!queue)
		{
			this.CancelCore();
			return;
		}

		try
		{
			ThreadPool.QueueUserWorkItem(static state => ((InboundRequestCancellation)state!).CancelCore(), this);
		}
		catch (NotSupportedException)
		{
			// The pool is unavailable during shutdown. Running here is the only way the iterator still observes cancellation.
			this.CancelCore();
		}
	}

	/// <summary>Drops one lease and disposes the source when none remain.</summary>
	internal void Release()
	{
		bool disposeNow = false;
		lock (this.gate)
		{
			if (this.remaining == 0)
			{
				return;
			}

			this.remaining--;
			if (this.remaining > 0)
			{
				return;
			}

			if (this.cancelStarted && !this.cancelFinished)
			{
				this.disposeRequested = true;
				return;
			}

			disposeNow = true;
		}

		if (disposeNow)
		{
			this.source.Dispose();
		}
	}

	private void CancelCore()
	{
		try
		{
#pragma warning disable VSTHRD103 // CancelAsync is unavailable on all target frameworks.
			this.source.Cancel();
#pragma warning restore VSTHRD103
		}
		catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
		{
			// The source may already have been released, or a callback may have thrown after cancellation was signaled.
		}
		finally
		{
			bool disposeNow = false;
			lock (this.gate)
			{
				this.cancelFinished = true;
				if (this.disposeRequested)
				{
					disposeNow = true;
				}
			}

			if (disposeNow)
			{
				this.source.Dispose();
			}
		}
	}
}
