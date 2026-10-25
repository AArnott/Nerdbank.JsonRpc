// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Queries brokers in priority order, falling through only when a service is unavailable.</summary>
/// <remarks>Errors and cancellation propagate. The aggregate does not own its constituent brokers.</remarks>
public sealed class ServiceBrokerAggregator : IServiceBroker, IDisposable
{
	private readonly IReadOnlyList<IServiceBroker> brokers;
	private bool disposed;

	/// <summary>Initializes a new instance of the <see cref="ServiceBrokerAggregator"/> class.</summary>
	/// <param name="brokers">Brokers in priority order.</param>
	public ServiceBrokerAggregator(params IServiceBroker[] brokers)
	{
		if (brokers is null || brokers.Any(static broker => broker is null))
		{
			throw new ArgumentException("Non-null brokers are required.", nameof(brokers));
		}

		this.brokers = Array.AsReadOnly((IServiceBroker[])brokers.Clone());
		foreach (IServiceBroker broker in this.brokers)
		{
			broker.AvailabilityChanged += this.OnAvailabilityChanged;
		}
	}

	/// <inheritdoc/>
	public event EventHandler<BrokeredServicesChangedEventArgs>? AvailabilityChanged;

	/// <inheritdoc/>
	public async ValueTask<T?> GetProxyAsync<T>(IJsonRpcDescriptor<T> descriptor, ServiceActivationOptions options = default, CancellationToken cancellationToken = default)
		where T : class
	{
		this.ThrowIfDisposed();
		foreach (IServiceBroker broker in this.brokers)
		{
			if (await broker.GetProxyAsync(descriptor, options, cancellationToken).ConfigureAwait(false) is { } proxy)
			{
				return proxy;
			}
		}

		return null;
	}

	/// <inheritdoc/>
	public async ValueTask<IDuplexPipe?> GetPipeAsync(ServiceMoniker moniker, ServiceActivationOptions options = default, CancellationToken cancellationToken = default)
	{
		this.ThrowIfDisposed();
		foreach (IServiceBroker broker in this.brokers)
		{
			if (await broker.GetPipeAsync(moniker, options, cancellationToken).ConfigureAwait(false) is { } pipe)
			{
				return pipe;
			}
		}

		return null;
	}

	/// <inheritdoc/>
	public async ValueTask<BrokeredServiceConnection?> GetConnectionAsync(ServiceMoniker moniker, ServiceActivationOptions options = default, CancellationToken cancellationToken = default)
	{
		this.ThrowIfDisposed();
		foreach (IServiceBroker broker in this.brokers)
		{
			if (await broker.GetConnectionAsync(moniker, options, cancellationToken).ConfigureAwait(false) is { } connection)
			{
				return connection;
			}
		}

		return null;
	}

	/// <inheritdoc/>
	public async ValueTask<IReadOnlyList<string>?> GetCapabilitiesAsync(ServiceMoniker moniker, CancellationToken cancellationToken = default)
	{
		this.ThrowIfDisposed();
		foreach (IServiceBroker broker in this.brokers)
		{
			if (await broker.GetCapabilitiesAsync(moniker, cancellationToken).ConfigureAwait(false) is { } capabilities)
			{
				return capabilities;
			}
		}

		return null;
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		if (!this.disposed)
		{
			this.disposed = true;
			foreach (IServiceBroker broker in this.brokers)
			{
				broker.AvailabilityChanged -= this.OnAvailabilityChanged;
			}
		}
	}

	private void ThrowIfDisposed()
	{
		if (this.disposed)
		{
			throw new ObjectDisposedException(nameof(ServiceBrokerAggregator));
		}
	}

	private void OnAvailabilityChanged(object? sender, BrokeredServicesChangedEventArgs args) => this.AvailabilityChanged?.Invoke(this, args);
}
