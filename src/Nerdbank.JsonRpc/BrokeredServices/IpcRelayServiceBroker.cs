// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Relays a local broker through cancellable, single-client same-account IPC endpoints.</summary>
public sealed class IpcRelayServiceBroker : IRemoteServiceBroker, IDisposable, IAsyncDisposable
{
	private readonly IServiceBroker broker;
	private readonly object syncObject = new();
	private readonly Dictionary<Guid, IpcServer> pending = [];
	private readonly List<IpcServer> servers = [];
	private readonly Dictionary<IpcServer, Task> cleanupTasks = [];
	private bool disposed;

	/// <summary>Initializes a new instance of the <see cref="IpcRelayServiceBroker"/> class.</summary>
	/// <param name="broker">The broker to relay, including per-registration metadata.</param>
	public IpcRelayServiceBroker(IServiceBroker broker)
	{
		this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
		this.broker.AvailabilityChanged += this.OnAvailabilityChanged;
	}

	/// <inheritdoc/>
	public event EventHandler<BrokeredServicesChangedEventArgs>? AvailabilityChanged;

	/// <summary>Gets the relay logger.</summary>
	public ILogger<IpcRelayServiceBroker> Logger { get; init; } = NullLogger<IpcRelayServiceBroker>.Instance;

	/// <summary>Gets the maximum time to wait for an allocated endpoint to be claimed.</summary>
	/// <remarks>Unclaimed endpoints are reclaimed even if a client disappears without canceling.</remarks>
	public TimeSpan ConnectionTimeout
	{
		get;
		init => field = value > TimeSpan.Zero && value.TotalMilliseconds <= int.MaxValue
			? value : throw new ArgumentOutOfRangeException(nameof(value));
	} = TimeSpan.FromSeconds(30);

	/// <inheritdoc/>
	public Task<int> HandshakeAsync(RemoteServiceConnections connections, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		this.ThrowIfDisposed();
		return (connections & RemoteServiceConnections.IpcPipe) != 0
			? Task.FromResult(1)
			: throw new NotSupportedException("The client does not support same-account IPC service channels.");
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<string>?> GetCapabilitiesAsync(ServiceMoniker moniker, CancellationToken cancellationToken = default)
	{
		using BrokeredServiceRequest request = new(this.Logger, moniker, "IpcRelayGetCapabilities");
		try
		{
			this.ThrowIfDisposed();
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
		using BrokeredServiceRequest request = new(this.Logger, moniker, "IpcRelayGetConnection");
		try
		{
			RemoteServiceConnectionInfo? info = await this.RequestServiceChannelCoreAsync(moniker, options, cancellationToken).ConfigureAwait(false);
			request.RejectionReason = "BrokerUnavailable";
			request.Complete(info is not null);
			if (info is not null)
			{
				this.Logger.LogInformation("Service {ServiceMoniker} allocated IPC endpoint with request {RequestId}.", moniker, info.RequestId);
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
	public async Task CancelServiceRequestAsync(Guid requestId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IpcServer? server;
		Task? cleanup = null;
		lock (this.syncObject)
		{
			this.pending.TryGetValue(requestId, out server);
			this.pending.Remove(requestId);
			if (server is not null)
			{
				this.cleanupTasks.TryGetValue(server, out cleanup);
			}
		}

		if (server is not null)
		{
			this.Logger.LogInformation("Canceling unclaimed service endpoint {RequestId}.", requestId);
			await server.DisposeAsync().ConfigureAwait(false);
			if (cleanup is not null)
			{
#pragma warning disable VSTHRD003 // Request cleanup runs asynchronously after the IPC listener completes.
				await cleanup.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
		}
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		IpcServer[] snapshot;
		lock (this.syncObject)
		{
			if (this.disposed)
			{
				return;
			}

			this.disposed = true;
			snapshot = this.servers.ToArray();
			this.pending.Clear();
		}

		this.broker.AvailabilityChanged -= this.OnAvailabilityChanged;
		foreach (IpcServer server in snapshot)
		{
			server.Dispose();
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync()
	{
		this.Dispose();
		Task[] snapshot;
		lock (this.syncObject)
		{
			snapshot = this.cleanupTasks.Values.ToArray();
		}

		await Task.WhenAll(snapshot).ConfigureAwait(false);
	}

	private static async Task CopyUntilShutdownAsync(Stream source, Stream destination, CancellationToken cancellationToken)
	{
		try
		{
			await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (IOException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private async Task<RemoteServiceConnectionInfo?> RequestServiceChannelCoreAsync(ServiceMoniker moniker, RemoteServiceActivationOptions options, CancellationToken cancellationToken)
	{
		this.ThrowIfDisposed();
		BrokeredServiceConnection? connection = await this.broker.GetConnectionAsync(moniker, options.ToLocal(), cancellationToken).ConfigureAwait(false);
		if (connection is null)
		{
			return null;
		}

		Guid id = Guid.NewGuid();
		TaskCompletionSource<bool> published = new(TaskCreationOptions.RunContinuationsAsynchronously);
		IpcServer? server = null;
		try
		{
			server = IpcServer.Create(async (stream, token) =>
			{
#pragma warning disable VSTHRD003 // Publication uses RunContinuationsAsynchronously and does not require caller context.
				await published.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
				lock (this.syncObject)
				{
					this.pending.Remove(id);
				}

				IDuplexPipe servicePipe = connection.Pipe;

				using CancellationTokenSource pumping = CancellationTokenSource.CreateLinkedTokenSource(token);
				Task inbound = CopyUntilShutdownAsync(stream, servicePipe.Output.AsStream(leaveOpen: true), pumping.Token);
				Task outbound = CopyUntilShutdownAsync(servicePipe.Input.AsStream(leaveOpen: true), stream, pumping.Token);
				try
				{
					Task completed = await Task.WhenAny(inbound, outbound).ConfigureAwait(false);
					await completed.ConfigureAwait(false);
				}
				finally
				{
#if NET
					await pumping.CancelAsync().ConfigureAwait(false);
#else
					pumping.Cancel();
#endif
#if NET
					await stream.DisposeAsync().ConfigureAwait(false);
#else
#pragma warning disable VSTHRD103 // Legacy Stream has no asynchronous disposal.
					stream.Dispose();
#pragma warning restore VSTHRD103
#endif
					try
					{
						await Task.WhenAll(inbound, outbound).ConfigureAwait(false);
					}
					finally
					{
						await servicePipe.Input.CompleteAsync().ConfigureAwait(false);
						await servicePipe.Output.CompleteAsync().ConfigureAwait(false);
					}
				}
			});
			cancellationToken.ThrowIfCancellationRequested();
			lock (this.syncObject)
			{
				this.ThrowIfDisposed();
				this.pending.Add(id, server);
				this.servers.Add(server);
				this.cleanupTasks.Add(server, this.ExpireRequestAsync(id, server, connection, published.Task));
				published.SetResult(true);
			}

			return new() { RequestId = id, PipeName = server.Name, Capabilities = connection.Capabilities };
		}
		catch
		{
			published.TrySetCanceled(CancellationToken.None);
			if (server is not null)
			{
				await server.DisposeAsync().ConfigureAwait(false);
			}

			await connection.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	private async Task ExpireRequestAsync(Guid id, IpcServer server, BrokeredServiceConnection connection, Task published)
	{
		using CancellationTokenSource timeout = new();
		try
		{
#pragma warning disable VSTHRD003 // Publication and listener completion are asynchronous and never require the caller's context.
			await published.ConfigureAwait(false);
			Task expiration = Task.Delay(this.ConnectionTimeout, timeout.Token);
			if (await Task.WhenAny(server.Completion, expiration).ConfigureAwait(false) == expiration)
			{
				bool expired;
				lock (this.syncObject)
				{
					expired = this.pending.Remove(id);
				}

				if (expired)
				{
					this.Logger.LogWarning("Service endpoint {RequestId} expired before the client connected.", id);
					await server.DisposeAsync().ConfigureAwait(false);
				}
			}

			await server.Completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
#if NET
			await timeout.CancelAsync().ConfigureAwait(false);
#else
			timeout.Cancel();
#endif
		}
		catch (Exception ex)
		{
			this.Logger.LogError(ex, "Relayed service request {RequestId} failed.", id);
		}
		finally
		{
			try
			{
				await connection.DisposeAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				this.Logger.LogError(ex, "Relayed service request {RequestId} cleanup failed.", id);
			}

			lock (this.syncObject)
			{
				this.pending.Remove(id);
				this.servers.Remove(server);
				this.cleanupTasks.Remove(server);
			}
		}
	}

	private void ThrowIfDisposed()
	{
		lock (this.syncObject)
		{
			if (this.disposed)
			{
				throw new ObjectDisposedException(nameof(IpcRelayServiceBroker));
			}
		}
	}

	private void OnAvailabilityChanged(object? sender, BrokeredServicesChangedEventArgs args) => this.AvailabilityChanged?.Invoke(this, args);
}
