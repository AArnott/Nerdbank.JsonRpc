// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Adapts a remote broker control contract to typed local service acquisition.</summary>
public sealed class RemoteServiceBroker : IServiceBroker, IDisposable
{
	private readonly IRemoteServiceBroker remote;
	private readonly IDisposable? owner;
	private readonly ServiceRpcContext? context;
	private readonly MultiplexingStream? multiplexer;
	private int disposed;

	private RemoteServiceBroker(IRemoteServiceBroker remote, IDisposable? owner, ServiceRpcContext? context = null, MultiplexingStream? multiplexer = null)
	{
		this.remote = remote;
		this.owner = owner;
		this.context = context;
		this.multiplexer = multiplexer;
		this.Logger = context?.LoggerFactory.CreateLogger<RemoteServiceBroker>() ?? NullLogger<RemoteServiceBroker>.Instance;
		this.remote.AvailabilityChanged += this.OnAvailabilityChanged;
	}

	/// <inheritdoc/>
	public event EventHandler<BrokeredServicesChangedEventArgs>? AvailabilityChanged;

	/// <summary>Gets or sets the client broker logger.</summary>
	public ILogger<RemoteServiceBroker> Logger
	{
		get;
		set => field = value ?? throw new ArgumentNullException(nameof(value));
	}

	/// <summary>Attaches a generated broker proxy, subscribes to events, starts dispatch, and negotiates.</summary>
	/// <param name="pipe">The broker control channel, whose ownership transfers when the connection is constructed, including when initialization subsequently fails.</param>
	/// <param name="descriptor">The agreed control descriptor, or the framework default.</param>
	/// <param name="context">The infrastructure for the control connection and subsequent service connections.</param>
	/// <param name="cancellationToken">Cancels initialization.</param>
	/// <returns>The initialized broker, which owns its control connection.</returns>
	public static Task<RemoteServiceBroker> ConnectAsync(IDuplexPipe pipe, JsonRpcDescriptor<IRemoteServiceBroker>? descriptor = null, ServiceRpcContext? context = null, CancellationToken cancellationToken = default)
		=> ConnectCoreAsync(pipe, descriptor, context, multiplexer: null, cancellationToken);

	/// <summary>Connects a broker control pipe with a separate caller-owned multiplexer carrying opaque service transports.</summary>
	/// <param name="pipe">The broker control channel, whose ownership transfers when the connection is constructed.</param>
	/// <param name="multiplexer">The caller-owned transport multiplexer. It is never used for service resource marshalling.</param>
	/// <param name="descriptor">The agreed control descriptor, or the framework default.</param>
	/// <param name="context">Runtime infrastructure for the control and service connections.</param>
	/// <param name="cancellationToken">Cancels initialization.</param>
	/// <returns>The initialized broker, which owns its control connection but not the transport multiplexer.</returns>
	public static Task<RemoteServiceBroker> ConnectAsync(IDuplexPipe pipe, MultiplexingStream multiplexer, JsonRpcDescriptor<IRemoteServiceBroker>? descriptor = null, ServiceRpcContext? context = null, CancellationToken cancellationToken = default)
		=> ConnectCoreAsync(pipe, descriptor, context, multiplexer ?? throw new ArgumentNullException(nameof(multiplexer)), cancellationToken);

	/// <summary>Adapts an already connected remote control contract.</summary>
	/// <param name="remote">The contract, whose lifetime remains caller-owned.</param>
	/// <param name="cancellationToken">Cancels negotiation.</param>
	/// <returns>The initialized broker.</returns>
	public static async Task<RemoteServiceBroker> ConnectAsync(IRemoteServiceBroker remote, CancellationToken cancellationToken = default)
		=> await ConnectAsync(remote, multiplexer: null, cancellationToken).ConfigureAwait(false);

	/// <summary>Adapts a connected control contract with caller-owned multiplexed service channels.</summary>
	/// <param name="remote">The caller-owned control contract.</param>
	/// <param name="multiplexer">The caller-owned multiplexer, or null for IPC.</param>
	/// <param name="cancellationToken">Cancels negotiation.</param>
	/// <returns>The initialized broker.</returns>
	public static async Task<RemoteServiceBroker> ConnectAsync(IRemoteServiceBroker remote, MultiplexingStream? multiplexer, CancellationToken cancellationToken = default)
	{
		RemoteServiceBroker broker = new(remote ?? throw new ArgumentNullException(nameof(remote)), null, multiplexer: multiplexer);
		try
		{
			await broker.NegotiateAsync(cancellationToken).ConfigureAwait(false);
			return broker;
		}
		catch
		{
			broker.Dispose();
			throw;
		}
	}

	/// <inheritdoc/>
	public async ValueTask<T?> GetProxyAsync<T>(IJsonRpcDescriptor<T> descriptor, ServiceActivationOptions options = default, CancellationToken cancellationToken = default)
		where T : class
	{
		if (descriptor is null)
		{
			throw new ArgumentNullException(nameof(descriptor));
		}

		using BrokeredServiceRequest request = new(this.Logger, descriptor.Moniker, "RemoteGetProxy", ActivityKind.Client);
		try
		{
			T? proxy = await this.GetProxyCoreAsync(descriptor, options, cancellationToken).ConfigureAwait(false);
			request.RejectionReason = "RemoteUnavailable";
			request.Complete(proxy is not null);
			return proxy;
		}
		catch (Exception ex)
		{
			request.Fail(ex, cancellationToken);
			throw;
		}
	}

	/// <inheritdoc/>
	public async ValueTask<IReadOnlyList<string>?> GetCapabilitiesAsync(ServiceMoniker moniker, CancellationToken cancellationToken = default)
	{
		using BrokeredServiceRequest request = new(this.Logger, moniker, "RemoteGetCapabilities", ActivityKind.Client);
		try
		{
			this.ThrowIfDisposed();
			IReadOnlyList<string>? capabilities = await this.remote.GetCapabilitiesAsync(moniker, cancellationToken).ConfigureAwait(false);
			request.RejectionReason = "RemoteUnavailable";
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
	public async ValueTask<IDuplexPipe?> GetPipeAsync(ServiceMoniker moniker, ServiceActivationOptions options = default, CancellationToken cancellationToken = default)
		=> (await this.GetConnectionAsync(moniker, options, cancellationToken).ConfigureAwait(false))?.Pipe;

	/// <inheritdoc/>
	public async ValueTask<BrokeredServiceConnection?> GetConnectionAsync(ServiceMoniker moniker, ServiceActivationOptions options = default, CancellationToken cancellationToken = default)
	{
		using BrokeredServiceRequest request = new(this.Logger, moniker, "RemoteGetConnection", ActivityKind.Client);
		try
		{
			BrokeredServiceConnection? connection = await this.GetConnectionCoreAsync(moniker, options, cancellationToken).ConfigureAwait(false);
			request.RejectionReason = "RemoteUnavailable";
			request.Complete(connection is not null);
			return connection;
		}
		catch (Exception ex)
		{
			request.Fail(ex, cancellationToken);
			throw;
		}
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		if (Interlocked.Exchange(ref this.disposed, 1) == 0)
		{
			this.remote.AvailabilityChanged -= this.OnAvailabilityChanged;
			this.owner?.Dispose();
		}
	}

	private static async Task<RemoteServiceBroker> ConnectCoreAsync(IDuplexPipe pipe, JsonRpcDescriptor<IRemoteServiceBroker>? descriptor, ServiceRpcContext? context, MultiplexingStream? multiplexer, CancellationToken cancellationToken)
	{
		descriptor ??= FrameworkServices.RemoteServiceBroker;
		JsonRpc rpc = descriptor.CreateConnection(pipe, context);
		RemoteServiceBroker? broker = null;
		try
		{
			IRemoteServiceBroker proxy = descriptor.CreateProxy(rpc, []);
			broker = new(proxy, (IDisposable)proxy, context, multiplexer);
			rpc.Start();
			await broker.NegotiateAsync(cancellationToken).ConfigureAwait(false);
			return broker;
		}
		catch
		{
			broker?.Dispose();
			rpc.Dispose();
			throw;
		}
	}

	private async ValueTask<T?> GetProxyCoreAsync<T>(IJsonRpcDescriptor<T> descriptor, ServiceActivationOptions options, CancellationToken cancellationToken)
		where T : class
	{
		this.ThrowIfDisposed();
		RemoteServiceConnectionInfo? info = await this.RequestServiceChannelAsync(descriptor.Moniker, options, cancellationToken).ConfigureAwait(false);
		if (info is null)
		{
			return null;
		}

		this.Logger.LogInformation("Service {ServiceMoniker} connecting to remote request {RequestId}.", descriptor.Moniker, info.RequestId);
		IDuplexPipe? pipe = null;
		JsonRpc? rpc = null;
		try
		{
			ServiceInterface[] additional = descriptor.AdditionalInterfaces.Where(item => info.Capabilities.Contains(item.Id, StringComparer.Ordinal)).ToArray();
			pipe = await this.OpenPipeAsync(info, cancellationToken).ConfigureAwait(false);
			rpc = descriptor.CreateConnection(pipe, this.context);
			T proxy = descriptor.CreateProxy(rpc, additional, options);
			options.ConfigureProxy?.Invoke(proxy);
			rpc.Start();
			return proxy;
		}
		catch
		{
			rpc?.Dispose();
			if (rpc is null && pipe is not null)
			{
				await pipe.Input.CompleteAsync().ConfigureAwait(false);
				await pipe.Output.CompleteAsync().ConfigureAwait(false);
			}

			await this.CancelFailedRequestAsync(info.RequestId).ConfigureAwait(false);
			throw;
		}
	}

	private async ValueTask<BrokeredServiceConnection?> GetConnectionCoreAsync(ServiceMoniker moniker, ServiceActivationOptions options, CancellationToken cancellationToken)
	{
		this.ThrowIfDisposed();
		RemoteServiceConnectionInfo? info = await this.RequestServiceChannelAsync(moniker, options, cancellationToken).ConfigureAwait(false);
		if (info is null)
		{
			return null;
		}

		this.Logger.LogInformation("Service {ServiceMoniker} connecting to remote request {RequestId}.", moniker, info.RequestId);
		try
		{
			return new(await this.OpenPipeAsync(info, cancellationToken).ConfigureAwait(false), info.Capabilities);
		}
		catch
		{
			await this.CancelFailedRequestAsync(info.RequestId).ConfigureAwait(false);
			throw;
		}
	}

	private async Task<RemoteServiceConnectionInfo?> RequestServiceChannelAsync(ServiceMoniker moniker, ServiceActivationOptions options, CancellationToken cancellationToken)
	{
		if (this.multiplexer is null)
		{
			return await this.remote.RequestServiceChannelAsync(moniker, RemoteServiceActivationOptions.FromLocal(options), cancellationToken).ConfigureAwait(false);
		}

		// Control responses and channel offers may travel on independent transports.
		HashSet<ulong> offered = [];
		ulong? expected = null;
		TaskCompletionSource<bool> arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnChannelOffered(object? sender, MultiplexingStream.ChannelOfferEventArgs args)
		{
			lock (offered)
			{
				offered.Add(args.QualifiedId.Id);
				if (expected == args.QualifiedId.Id)
				{
					arrived.TrySetResult(true);
				}
			}
		}

		RemoteServiceConnectionInfo? info = null;
		this.multiplexer.ChannelOffered += OnChannelOffered;
		try
		{
			info = await this.remote.RequestServiceChannelAsync(moniker, RemoteServiceActivationOptions.FromLocal(options), cancellationToken).ConfigureAwait(false);
			if (info?.MultiplexingChannelId is { } id)
			{
				lock (offered)
				{
					expected = id;
					if (offered.Contains(id))
					{
						arrived.TrySetResult(true);
					}
				}

#pragma warning disable VSTHRD003 // Both tasks are completed by asynchronous transport processing.
				if (await Task.WhenAny(arrived.Task, this.multiplexer.Completion).WithCancellation(cancellationToken).ConfigureAwait(false) != arrived.Task)
				{
					await this.multiplexer.Completion.ConfigureAwait(false);
					throw new EndOfStreamException("The multiplexer closed before the service channel was offered.");
				}
#pragma warning restore VSTHRD003
			}

			return info;
		}
		catch
		{
			if (info is not null)
			{
				await this.CancelFailedRequestAsync(info.RequestId).ConfigureAwait(false);
			}

			throw;
		}
		finally
		{
			this.multiplexer.ChannelOffered -= OnChannelOffered;
		}
	}

	private async Task NegotiateAsync(CancellationToken cancellationToken)
	{
		RemoteServiceConnections transports = RemoteServiceConnections.IpcPipe
			| (this.multiplexer is null ? RemoteServiceConnections.None : RemoteServiceConnections.Multiplexing);
		if (await this.remote.HandshakeAsync(transports, cancellationToken).ConfigureAwait(false) != 1)
		{
			throw new NotSupportedException("The remote broker selected an unsupported protocol version.");
		}
	}

	private async ValueTask<IDuplexPipe> OpenPipeAsync(RemoteServiceConnectionInfo info, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if ((info.PipeName is null) == (info.MultiplexingChannelId is null))
		{
			throw new InvalidOperationException("The broker must supply exactly one service transport endpoint.");
		}

		if (info.MultiplexingChannelId is { } id)
		{
			return (this.multiplexer ?? throw new NotSupportedException("No multiplexer is available for the offered service channel.")).AcceptChannel(id);
		}

		Stream stream = await IpcClient.ConnectAsync(info.PipeName!, cancellationToken).ConfigureAwait(false);
		return stream.UsePipe(cancellationToken: CancellationToken.None);
	}

	private async Task CancelFailedRequestAsync(Guid id)
	{
		try
		{
			await this.remote.CancelServiceRequestAsync(id, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			this.Logger.LogError(ex, "Could not cancel abandoned service request {RequestId}.", id);
		}
	}

	private void ThrowIfDisposed()
	{
		if (Volatile.Read(ref this.disposed) != 0)
		{
			throw new ObjectDisposedException(nameof(RemoteServiceBroker));
		}
	}

	private void OnAvailabilityChanged(object? sender, BrokeredServicesChangedEventArgs args) => this.AvailabilityChanged?.Invoke(this, args);
}
