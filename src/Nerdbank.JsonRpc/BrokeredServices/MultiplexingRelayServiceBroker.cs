// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Relays brokered services over channels on a caller-owned multiplexing stream.</summary>
public sealed class MultiplexingRelayServiceBroker : IRemoteServiceBroker, IDisposable
{
	private readonly IServiceBroker broker;
	private readonly MultiplexingStream multiplexer;
	private readonly object syncObject = new();
	private readonly Dictionary<Guid, MultiplexingStream.Channel> pending = [];
	private readonly HashSet<MultiplexingStream.Channel> channels = [];
	private bool disposed;

	/// <summary>Initializes a new instance of the <see cref="MultiplexingRelayServiceBroker"/> class.</summary>
	/// <param name="broker">The broker, including per-registration capabilities.</param>
	/// <param name="multiplexer">The caller-owned stream carrying service channels.</param>
	public MultiplexingRelayServiceBroker(IServiceBroker broker, MultiplexingStream multiplexer)
	{
		this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
		this.multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
		broker.AvailabilityChanged += this.OnAvailabilityChanged;
	}

	/// <inheritdoc/>
	public event EventHandler<BrokeredServicesChangedEventArgs>? AvailabilityChanged;

	/// <summary>Gets the logger used to observe channel and cleanup failures.</summary>
	public ILogger<MultiplexingRelayServiceBroker> Logger { get; init; } = NullLogger<MultiplexingRelayServiceBroker>.Instance;

	/// <summary>Gets the time allowed for clients to accept an offered channel.</summary>
	public TimeSpan ConnectionTimeout
	{
		get;
		init => field = value > TimeSpan.Zero && value.TotalMilliseconds <= int.MaxValue
			? value
			: throw new ArgumentOutOfRangeException(nameof(value));
	} = TimeSpan.FromSeconds(30);

	/// <inheritdoc/>
	public Task<int> HandshakeAsync(RemoteServiceConnections connections, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (this.syncObject)
		{
			this.ThrowIfDisposed();
		}

		return (connections & RemoteServiceConnections.Multiplexing) != 0
			? Task.FromResult(1)
			: throw new NotSupportedException("The client does not support multiplexed service channels.");
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<string>?> GetCapabilitiesAsync(ServiceMoniker moniker, CancellationToken cancellationToken = default)
	{
		using BrokeredServiceRequest request = new(this.Logger, moniker, "MultiplexingRelayGetCapabilities");
		try
		{
			lock (this.syncObject)
			{
				this.ThrowIfDisposed();
			}

			IReadOnlyList<string>? capabilities = await this.broker.GetCapabilitiesAsync(moniker, cancellationToken).ConfigureAwait(false);
			request.RejectionReason = "BrokerUnavailable";
			request.Complete(capabilities is not null);
			return capabilities;
		}
		catch (Exception ex)
		{
			request.Fail(ex, cancellationToken);
			throw;
		}
	}

	/// <inheritdoc/>
	public async Task<RemoteServiceConnectionInfo?> RequestServiceChannelAsync(ServiceMoniker moniker, RemoteServiceActivationOptions options, CancellationToken cancellationToken)
	{
		using BrokeredServiceRequest request = new(this.Logger, moniker, "MultiplexingRelayGetConnection");
		try
		{
			RemoteServiceConnectionInfo? info = await this.RequestServiceChannelCoreAsync(moniker, options, cancellationToken).ConfigureAwait(false);
			request.RejectionReason = "BrokerUnavailable";
			request.Complete(info is not null);
			if (info is not null)
			{
				this.Logger.LogInformation("Service {ServiceMoniker} allocated multiplexed channel with request {RequestId}.", moniker, info.RequestId);
			}

			return info;
		}
		catch (Exception ex)
		{
			request.Fail(ex, cancellationToken);
			throw;
		}
	}

	/// <inheritdoc/>
	public Task CancelServiceRequestAsync(Guid requestId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		MultiplexingStream.Channel? channel;
		lock (this.syncObject)
		{
			this.pending.TryGetValue(requestId, out channel);
			this.pending.Remove(requestId);
		}

		if (channel is not null)
		{
			this.Logger.LogInformation("Canceling unclaimed service channel {RequestId}.", requestId);
			channel.Dispose();
		}

		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		MultiplexingStream.Channel[] snapshot;
		lock (this.syncObject)
		{
			if (this.disposed)
			{
				return;
			}

			this.disposed = true;
			snapshot = this.channels.ToArray();
			this.pending.Clear();
		}

		this.broker.AvailabilityChanged -= this.OnAvailabilityChanged;
		foreach (MultiplexingStream.Channel channel in snapshot)
		{
			channel.Dispose();
		}
	}

	private async Task<RemoteServiceConnectionInfo?> RequestServiceChannelCoreAsync(ServiceMoniker moniker, RemoteServiceActivationOptions options, CancellationToken cancellationToken)
	{
		lock (this.syncObject)
		{
			this.ThrowIfDisposed();
		}

		BrokeredServiceConnection? connection = await this.broker.GetConnectionAsync(moniker, options.ToLocal(), cancellationToken).ConfigureAwait(false);
		if (connection is null)
		{
			return null;
		}

		MultiplexingStream.Channel channel;
		Guid id = Guid.NewGuid();
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			lock (this.syncObject)
			{
				this.ThrowIfDisposed();
				channel = this.multiplexer.CreateChannel(new() { ExistingPipe = connection.Pipe });
				this.pending.Add(id, channel);
				this.channels.Add(channel);
			}
		}
		catch
		{
			await connection.DisposeAsync().ConfigureAwait(false);
			throw;
		}

		_ = this.ObserveChannelAsync(id, channel);
		return new() { RequestId = id, MultiplexingChannelId = channel.QualifiedId.Id, Capabilities = connection.Capabilities };
	}

	private async Task ObserveChannelAsync(Guid id, MultiplexingStream.Channel channel)
	{
		using CancellationTokenSource timeout = new();
		try
		{
			Task expiration = Task.Delay(this.ConnectionTimeout, timeout.Token);
#pragma warning disable VSTHRD003 // Channel lifecycle tasks are completed asynchronously by the multiplexer.
			if (await Task.WhenAny(channel.Acceptance, expiration).ConfigureAwait(false) == expiration)
			{
				this.Logger.LogWarning("Service channel {RequestId} expired before the client accepted it.", id);
				await this.CancelServiceRequestAsync(id, CancellationToken.None).ConfigureAwait(false);
			}
			else
			{
				await channel.Acceptance.ConfigureAwait(false);
			}

#if NET
			await timeout.CancelAsync().ConfigureAwait(false);
#else
			timeout.Cancel();
#endif
			lock (this.syncObject)
			{
				this.pending.Remove(id);
			}

			await channel.Completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}
		catch (OperationCanceledException) when (channel.IsDisposed)
		{
		}
		catch (Exception ex)
		{
			this.Logger.LogError(ex, "Relayed service channel {RequestId} failed.", id);
		}
		finally
		{
			lock (this.syncObject)
			{
				this.pending.Remove(id);
				this.channels.Remove(channel);
			}

			channel.Dispose();
		}
	}

	private void ThrowIfDisposed()
	{
		if (this.disposed)
		{
			throw new ObjectDisposedException(nameof(MultiplexingRelayServiceBroker));
		}
	}

	private void OnAvailabilityChanged(object? sender, BrokeredServicesChangedEventArgs args) => this.AvailabilityChanged?.Invoke(this, args);
}
