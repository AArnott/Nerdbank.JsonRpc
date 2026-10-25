// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Keeps one service generation alive for an invocation.</summary>
/// <remarks>Dispose exactly once. Copies of a lease do not acquire additional references.</remarks>
public readonly struct ServiceProxyLease : IDisposable
{
	private readonly Generation generation;

	/// <summary>Initializes a new instance of the <see cref="ServiceProxyLease"/> struct.</summary>
	/// <param name="generation">The rented generation.</param>
	internal ServiceProxyLease(Generation generation) => this.generation = generation;

	/// <summary>Gets the target interface implementation.</summary>
	public object Target => this.generation.Target;

	/// <summary>Gets the generation retained by this lease.</summary>
	internal Generation ServiceGeneration => this.generation;

	/// <inheritdoc/>
	public void Dispose() => this.generation.Release();

	/// <summary>Duplicates an active lease for an operation that outlives its invocation.</summary>
	/// <returns>The retained lease.</returns>
	internal ServiceProxyLease Retain()
	{
		if (!this.generation.TryRent())
		{
			throw new ObjectDisposedException(nameof(ServiceProxyLease));
		}

		return new(this.generation);
	}

	/// <summary>Owns a target until its last lease is released.</summary>
	internal sealed class Generation
	{
		private readonly object owner;
		private readonly Action<Exception>? logFailure;
		private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int references = 1;

		/// <summary>Initializes a new instance of the <see cref="Generation"/> class.</summary>
		/// <param name="target">The invocation target.</param>
		/// <param name="owner">The resource disposed with the generation.</param>
		/// <param name="logFailure">Records failures even when synchronous disposal cannot await pending leases.</param>
		internal Generation(object target, object owner, Action<Exception>? logFailure = null)
		{
			this.Target = target;
			this.owner = owner;
			this.logFailure = logFailure;
		}

		/// <summary>Gets the invocation target.</summary>
		internal object Target { get; }

		/// <summary>Gets the disposal completion task.</summary>
#pragma warning disable VSTHRD003 // RunContinuationsAsynchronously prevents cleanup continuations from requiring a blocked context.
		internal Task Completion => this.completion.Task;
#pragma warning restore VSTHRD003

		/// <summary>Acquires a reference unless cleanup has started.</summary>
		/// <returns>Whether a reference was acquired.</returns>
		internal bool TryRent()
		{
			int count;
			do
			{
				count = Volatile.Read(ref this.references);
				if (count == 0)
				{
					return false;
				}
			}
			while (Interlocked.CompareExchange(ref this.references, count + 1, count) != count);
			return true;
		}

		/// <summary>Releases a reference and starts cleanup when the count reaches zero.</summary>
		internal void Release()
		{
			if (Interlocked.Decrement(ref this.references) == 0)
			{
				_ = this.CleanupAsync();
			}
		}

		private async Task CleanupAsync()
		{
			try
			{
				if (this.owner is IAsyncDisposable asynchronous)
				{
					await asynchronous.DisposeAsync().ConfigureAwait(false);
				}
				else if (this.owner is IDisposable disposable)
				{
					disposable.Dispose();
				}

				this.completion.TrySetResult(true);
			}
			catch (Exception ex)
			{
				this.logFailure?.Invoke(ex);
				this.completion.TrySetException(ex);
				_ = this.completion.Task.Exception;
			}
		}
	}
}
