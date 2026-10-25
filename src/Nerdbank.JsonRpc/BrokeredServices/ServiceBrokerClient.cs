// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Caches service proxies while allowing independent rentals to outlive invalidation.</summary>
/// <remarks>The client does not own its broker. Dispose each rental and then the client.</remarks>
public sealed class ServiceBrokerClient : IDisposable
{
	private readonly IServiceBroker broker;
	private readonly object syncObject = new();
	private readonly Dictionary<CacheKey, Entry> entries = [];
	private readonly CancellationTokenSource shutdown = new();
	private bool disposed;

	/// <summary>Initializes a new instance of the <see cref="ServiceBrokerClient"/> class.</summary>
	/// <param name="broker">The underlying broker.</param>
	public ServiceBrokerClient(IServiceBroker broker)
	{
		this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
		broker.AvailabilityChanged += this.OnAvailabilityChanged;
	}

	/// <summary>Occurs after affected cache entries are invalidated.</summary>
	public event EventHandler<BrokeredServicesChangedEventArgs>? Invalidated;

	/// <summary>Gets the logger used for asynchronous cleanup failures.</summary>
	public ILogger<ServiceBrokerClient> Logger { get; init; } = NullLogger<ServiceBrokerClient>.Instance;

	/// <summary>Rents a cached proxy. Caller cancellation affects this rental, not other callers' activation.</summary>
	/// <typeparam name="T">The service interface inferred from the descriptor.</typeparam>
	/// <param name="descriptor">The contract and proxy settings. Cache entries are keyed by descriptor instance, not value equality.</param>
	/// <param name="options">The activation options. These participate in the cache key.</param>
	/// <param name="cancellationToken">Cancels waiting for this rental.</param>
	/// <returns>A rental whose proxy may be null when unavailable.</returns>
	public async ValueTask<Rental<T>> GetProxyAsync<T>(IJsonRpcDescriptor<T> descriptor, ServiceActivationOptions options = default, CancellationToken cancellationToken = default)
		where T : class
	{
		if (descriptor is null)
		{
			throw new ArgumentNullException(nameof(descriptor));
		}

		cancellationToken.ThrowIfCancellationRequested();
		CacheKey key = new(descriptor, options);
		Entry entry;
		lock (this.syncObject)
		{
			if (this.disposed)
			{
				throw new ObjectDisposedException(nameof(ServiceBrokerClient));
			}

			if (!this.entries.TryGetValue(key, out entry!))
			{
				entry = new(this, async () => await this.broker.GetProxyAsync(descriptor, options, this.shutdown.Token).ConfigureAwait(false));
				this.entries.Add(key, entry);
			}

			entry.AddReference();
		}

		Task<object?> activation = entry.Proxy.GetValueAsync(CancellationToken.None);
		try
		{
			object? proxy = await activation.WithCancellation(cancellationToken).ConfigureAwait(false);
			return new((T?)proxy, entry.Release);
		}
		catch
		{
			bool evicted = false;
			if (activation.IsFaulted || activation.IsCanceled)
			{
				lock (this.syncObject)
				{
					if (this.entries.TryGetValue(key, out Entry? cached) && ReferenceEquals(cached, entry))
					{
						this.entries.Remove(key);
						evicted = true;
					}
				}
			}

			if (evicted)
			{
				entry.Release();
			}

			entry.Release();
			throw;
		}
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		Entry[] snapshot;
		lock (this.syncObject)
		{
			if (this.disposed)
			{
				return;
			}

			this.disposed = true;
			snapshot = this.entries.Values.ToArray();
			this.entries.Clear();
		}

		this.broker.AvailabilityChanged -= this.OnAvailabilityChanged;
		try
		{
			this.shutdown.Cancel();
		}
		finally
		{
			foreach (Entry entry in snapshot)
			{
				entry.Release();
			}
		}
	}

	private void OnAvailabilityChanged(object? sender, BrokeredServicesChangedEventArgs args)
	{
		Entry[] invalidated;
		lock (this.syncObject)
		{
			CacheKey[] keys = this.entries.Keys.Where(key => args.OtherServicesImpacted || args.ImpactedServices.Contains(key.Descriptor.Moniker)).ToArray();
			invalidated = keys.Select(key => this.entries[key]).ToArray();
			foreach (CacheKey key in keys)
			{
				this.entries.Remove(key);
			}
		}

		foreach (Entry entry in invalidated)
		{
			entry.Release();
		}

		this.Invalidated?.Invoke(this, args);
	}

	/// <summary>A reference-counted rental whose disposal never invalidates other rentals.</summary>
	/// <typeparam name="T">The service interface.</typeparam>
	public sealed class Rental<T> : IDisposable
		where T : class
	{
		private Action? release;

		internal Rental(T? proxy, Action release)
		{
			this.Proxy = proxy;
			this.release = release;
		}

		/// <summary>Gets the rented proxy, or null if unavailable. Do not retain it beyond this rental.</summary>
		public T? Proxy { get; }

		/// <inheritdoc/>
		public void Dispose() => Interlocked.Exchange(ref this.release, null)?.Invoke();
	}

	private sealed record CacheKey(IJsonRpcDescriptor Descriptor, ServiceActivationOptions Options)
	{
		public bool Equals(CacheKey? other) => other is not null && ReferenceEquals(this.Descriptor, other.Descriptor) && this.Options.Equals(other.Options);

		public override int GetHashCode() => unchecked((RuntimeHelpers.GetHashCode(this.Descriptor) * 397) ^ this.Options.GetHashCode());
	}

	private sealed class Entry
	{
		private readonly ServiceBrokerClient client;
		private int references = 1;

		internal Entry(ServiceBrokerClient client, Func<Task<object?>> factory)
		{
			this.client = client;
#pragma warning disable VSTHRD012 // Activation never synchronously blocks and does not require a main-thread affinity.
			this.Proxy = new(factory);
#pragma warning restore VSTHRD012
		}

		internal AsyncLazy<object?> Proxy { get; }

		internal void AddReference() => Interlocked.Increment(ref this.references);

		internal void Release()
		{
			if (Interlocked.Decrement(ref this.references) == 0)
			{
				this.CleanupAsync().Forget();
			}
		}

		private async Task CleanupAsync()
		{
			try
			{
				object? proxy = await this.Proxy.GetValueAsync().ConfigureAwait(false);
				if (proxy is System.IAsyncDisposable asynchronous)
				{
					await asynchronous.DisposeAsync().ConfigureAwait(false);
				}
				else if (proxy is IDisposable disposable)
				{
					disposable.Dispose();
				}
			}
			catch (OperationCanceledException) when (this.client.shutdown.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				this.client.Logger.LogError(ex, "Cached service proxy cleanup failed.");
			}
		}
	}
}
