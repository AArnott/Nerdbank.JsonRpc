// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Owns a stable generated proxy that replaces backing services without replaying calls.</summary>
/// <typeparam name="T">The primary service interface.</typeparam>
public sealed class ResilientServiceProxy<T> : IResilientServiceProxy
	where T : class
{
	private readonly Context context;

	private ResilientServiceProxy(Context context, T proxy)
	{
		this.context = context;
		this.Proxy = proxy;
		context.EventSender = this;
	}

	/// <inheritdoc/>
	public event EventHandler<ResilientServiceProxyChangedEventArgs>? BackingServiceChanged
	{
		add => this.context.Changed += value;
		remove => this.context.Changed -= value;
	}

	/// <summary>Gets the stable generated service proxy.</summary>
	public T Proxy { get; }

	/// <inheritdoc/>
	public bool IsAvailable => this.context.Available;

	/// <inheritdoc/>
	public void Dispose() => this.context.Dispose();

	/// <inheritdoc/>
	public ValueTask DisposeAsync() => this.context.DisposeAsync();

	/// <summary>Creates and initializes a stable proxy, including when the service is currently unavailable.</summary>
	/// <param name="broker">The broker to observe.</param>
	/// <param name="descriptor">The typed contract and its complete allowed additional-interface universe.</param>
	/// <param name="options">Options for each activation.</param>
	/// <param name="logger">The lifecycle logger.</param>
	/// <param name="cancellationToken">Cancels initial acquisition.</param>
	/// <returns>The owner of the stable generated proxy.</returns>
	internal static async ValueTask<ResilientServiceProxy<T>> CreateAsync(IServiceBroker broker, IJsonRpcDescriptor<T> descriptor, ServiceActivationOptions options, ILogger<ServiceProxyContext>? logger, CancellationToken cancellationToken)
	{
		if (broker is null)
		{
			throw new ArgumentNullException(nameof(broker));
		}

		if (descriptor is null)
		{
			throw new ArgumentNullException(nameof(descriptor));
		}

		Context context = new(broker, descriptor, options) { Logger = logger ?? NullLogger<ServiceProxyContext>.Instance };
		try
		{
			T proxy = descriptor.CreateLocalProxy(context, descriptor.AdditionalInterfaces);
			await context.RefreshAsync(cancellationToken).ConfigureAwait(false);
			return new(context, proxy);
		}
		catch
		{
			await context.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	private sealed class Context : ServiceProxyContext
	{
		private readonly IServiceBroker broker;
		private readonly IJsonRpcDescriptor<T> descriptor;
		private readonly ServiceActivationOptions options;
		private readonly SemaphoreSlim refreshLock = new(1, 1);
		private readonly CancellationTokenSource shutdown = new();
		private readonly object syncObject = new();
		private readonly List<ServiceProxyLease.Generation> retired = [];
		private readonly List<Subscription> subscriptions = [];
		private readonly Queue<ResilientServiceProxyChangeKind> changes = [];
		private ServiceProxyLease.Generation? current;
		private bool disposed;
		private bool previouslyAvailable;
		private int version;
		private bool raisingChanges;

		internal Context(IServiceBroker broker, IJsonRpcDescriptor<T> descriptor, ServiceActivationOptions options)
		{
			this.broker = broker;
			this.descriptor = descriptor;
			this.options = options;
			broker.AvailabilityChanged += this.OnAvailabilityChanged;
		}

		internal event EventHandler<ResilientServiceProxyChangedEventArgs>? Changed;

		public override bool IsDisposed => Volatile.Read(ref this.disposed);

		internal bool Available => Volatile.Read(ref this.current) is not null;

		internal object? EventSender { get; set; }

		protected override object? EventTarget => Volatile.Read(ref this.current)?.Target;

		public override bool IsInterfaceEnabled(Type interfaceType)
			=> this.EventTarget is IClientProxy proxy && proxy.Is(interfaceType);

		public override Exception TranslateException(Exception exception) => exception;

		public override IDisposable? RetainSubscription(IDisposable? subscription, ServiceProxyLease lease, Func<object, CancellationToken, ValueTask<IDisposable?>>? resubscribe = null)
		{
			if (subscription is null || resubscribe is null)
			{
				return base.RetainSubscription(subscription, lease);
			}

			Subscription tracked = new(this, subscription, lease.Retain(), resubscribe);
			bool accepted;
			lock (this.syncObject)
			{
				accepted = !this.disposed;
				if (accepted)
				{
					this.subscriptions.Add(tracked);
				}
			}

			if (!accepted)
			{
				tracked.Dispose();
				throw new ObjectDisposedException(nameof(ResilientServiceProxy<T>));
			}

			return tracked;
		}

		public override async ValueTask<ServiceProxyLease> RentAsync(CancellationToken cancellationToken)
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				lock (this.syncObject)
				{
					this.ThrowIfDisposed();
					if (this.current is { } generation && generation.TryRent())
					{
						return new(generation);
					}
				}

				await this.RefreshAsync(cancellationToken).ConfigureAwait(false);
				if (!this.Available)
				{
					throw new BrokeredServiceUnavailableException(this.descriptor.Moniker);
				}
			}
		}

		public override void Dispose()
		{
			ServiceProxyLease.Generation? previous;
			lock (this.syncObject)
			{
				if (this.disposed)
				{
					return;
				}

				this.disposed = true;
				this.version++;
				previous = this.current;
				this.current = null;
				if (previous is not null)
				{
					this.retired.Add(previous);
				}
			}

			this.broker.AvailabilityChanged -= this.OnAvailabilityChanged;
			try
			{
				this.shutdown.Cancel();
			}
			finally
			{
				try
				{
					this.DisconnectBindings(disposing: true);
				}
				finally
				{
					previous?.Release();
				}
			}
		}

		public override async ValueTask DisposeAsync()
		{
			this.Dispose();
			await this.refreshLock.WaitAsync().ConfigureAwait(false);
			try
			{
				ServiceProxyLease.Generation[] generations;
				lock (this.syncObject)
				{
					generations = this.retired.ToArray();
				}

				await Task.WhenAll(generations.Select(static generation => generation.Completion)).ConfigureAwait(false);
			}
			finally
			{
				this.refreshLock.Release();
			}
		}

		internal async Task RefreshAsync(CancellationToken cancellationToken)
		{
			using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.shutdown.Token);
			await this.refreshLock.WaitAsync(linked.Token).ConfigureAwait(false);
			try
			{
				while (true)
				{
					int requestVersion;
					lock (this.syncObject)
					{
						this.ThrowIfDisposed();
						if (this.current is not null)
						{
							return;
						}

						requestVersion = this.version;
					}

					T? target;
					try
					{
#pragma warning disable NBJSONRPC004 // Generation takes ownership and disposes the proxy after its last lease is released.
						target = await this.broker.GetProxyAsync(this.descriptor, this.options, linked.Token).ConfigureAwait(false);
#pragma warning restore NBJSONRPC004
					}
					catch
					{
						this.RejectReplacement(null, requestVersion);
						throw;
					}

					ServiceProxyLease.Generation? replacement = target is null ? null : new(target, target, ex => this.Logger.LogError(ex, "Retired service cleanup failed."));
					bool accepted;
					bool hadService;
					lock (this.syncObject)
					{
						accepted = !this.disposed && requestVersion == this.version;
						hadService = this.previouslyAvailable;
						if (accepted)
						{
							this.current = replacement;
						}
					}

					if (!accepted)
					{
						replacement?.Release();
						continue;
					}

					try
					{
						this.MoveEvents(target);
						if (replacement is not null && target is IJsonRpcServiceProxy { RpcClient: JsonRpc connection })
						{
							this.ObserveDisconnectionAsync(replacement, connection).Forget();
						}

						if (replacement is not null)
						{
							Subscription[] snapshot;
							lock (this.syncObject)
							{
								snapshot = this.subscriptions.ToArray();
							}

							foreach (Subscription subscription in snapshot)
							{
								await subscription.RestoreAsync(replacement, linked.Token).ConfigureAwait(false);
							}
						}
					}
					catch
					{
						this.RejectReplacement(replacement, requestVersion);
						throw;
					}

					lock (this.syncObject)
					{
						if (this.disposed || requestVersion != this.version)
						{
							continue;
						}

						this.previouslyAvailable = replacement is not null;
						if (hadService || replacement is not null)
						{
							this.changes.Enqueue(replacement is null ? ResilientServiceProxyChangeKind.Lost
								: hadService ? ResilientServiceProxyChangeKind.Replaced : ResilientServiceProxyChangeKind.Gained);
						}
					}

					return;
				}
			}
			finally
			{
				this.refreshLock.Release();
				this.DrainChanges();
			}
		}

		private void ThrowIfDisposed()
		{
			if (this.disposed)
			{
				throw new ObjectDisposedException(nameof(ResilientServiceProxy<T>));
			}
		}

		private void RejectReplacement(ServiceProxyLease.Generation? replacement, int requestVersion)
		{
			lock (this.syncObject)
			{
				if (this.disposed || requestVersion != this.version || !ReferenceEquals(this.current, replacement))
				{
					return;
				}

				this.current = null;
				this.version++;
				if (replacement is not null)
				{
					this.retired.RemoveAll(static generation => generation.Completion.IsCompleted);
					this.retired.Add(replacement);
				}

				if (this.previouslyAvailable)
				{
					this.previouslyAvailable = false;
					this.changes.Enqueue(ResilientServiceProxyChangeKind.Lost);
				}
			}

			try
			{
				this.DisconnectBindings(disposing: false);
			}
			finally
			{
				replacement?.Release();
			}
		}

		private void DisconnectBindings(bool disposing)
		{
			try
			{
				if (disposing)
				{
					this.DetachEvents();
				}
				else
				{
					this.MoveEvents(null);
				}
			}
			catch (Exception ex)
			{
				this.Logger.LogError(ex, "Service event handlers could not be detached.");
			}

			this.DisconnectSubscriptions(dispose: disposing);
		}

		private void OnAvailabilityChanged(object? sender, BrokeredServicesChangedEventArgs args)
		{
			if (!args.OtherServicesImpacted && !args.ImpactedServices.Contains(this.descriptor.Moniker))
			{
				return;
			}

			this.Invalidate();
		}

		private void Invalidate(ServiceProxyLease.Generation? expected = null)
		{
			ServiceProxyLease.Generation? previous;
			lock (this.syncObject)
			{
				if (this.disposed || (expected is not null && !ReferenceEquals(this.current, expected)))
				{
					return;
				}

				this.version++;
				previous = this.current;
				this.current = null;
				if (previous is not null)
				{
					this.retired.RemoveAll(static generation => generation.Completion.IsCompleted);
					this.retired.Add(previous);
				}
			}

			try
			{
				this.DisconnectBindings(disposing: false);
			}
			finally
			{
				previous?.Release();
			}

			this.ObserveRefreshAsync().Forget();
		}

		private async Task ObserveRefreshAsync()
		{
			try
			{
				await this.RefreshAsync(this.shutdown.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				this.Logger.LogError(ex, "Backing service refresh failed for {ServiceMoniker}.", this.descriptor.Moniker);
			}
		}

		private async Task ObserveDisconnectionAsync(ServiceProxyLease.Generation generation, JsonRpc connection)
		{
			try
			{
#pragma warning disable VSTHRD003 // Transport completion does not depend on a synchronous waiter; shutdown cancels the observation.
				await connection.Completion.WithCancellation(this.shutdown.Token).ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
			catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				this.Logger.LogDebug(ex, "A backing service connection closed.");
			}

			this.Invalidate(generation);
		}

		private void DisconnectSubscriptions(bool dispose)
		{
			Subscription[] snapshot;
			lock (this.syncObject)
			{
				snapshot = this.subscriptions.ToArray();
			}

			foreach (Subscription subscription in snapshot)
			{
				try
				{
					if (dispose)
					{
						subscription.Dispose();
					}
					else
					{
						subscription.Disconnect();
					}
				}
				catch (Exception ex)
				{
					this.Logger.LogError(ex, "An observer subscription could not be detached.");
				}
			}
		}

		private void DrainChanges()
		{
			lock (this.syncObject)
			{
				if (this.raisingChanges)
				{
					return;
				}

				this.raisingChanges = true;
			}

			while (true)
			{
				ResilientServiceProxyChangeKind kind;
				lock (this.syncObject)
				{
					if (this.changes.Count == 0)
					{
						this.raisingChanges = false;
						return;
					}

					kind = this.changes.Dequeue();
				}

				this.RaiseChanged(kind);
			}
		}

		private void RaiseChanged(ResilientServiceProxyChangeKind kind)
		{
			if (this.Changed is { } handlers)
			{
				foreach (EventHandler<ResilientServiceProxyChangedEventArgs> handler in handlers.GetInvocationList())
				{
					try
					{
						handler(this.EventSender ?? this, new(kind));
					}
					catch (Exception ex)
					{
						this.Logger.LogError(ex, "A backing-service lifecycle handler failed.");
					}
				}
			}
		}

		private sealed class Subscription(Context owner, IDisposable initial, ServiceProxyLease initialLease, Func<object, CancellationToken, ValueTask<IDisposable?>> restore) : IDisposable
		{
			private readonly object syncObject = new();
			private IDisposable? current = initial;
			private ServiceProxyLease? lease = initialLease;
			private bool disposed;

			public void Dispose()
			{
				lock (this.syncObject)
				{
					if (this.disposed)
					{
						return;
					}

					this.disposed = true;
				}

				lock (owner.syncObject)
				{
					owner.subscriptions.Remove(this);
				}

				this.Disconnect();
			}

			internal void Disconnect()
			{
				IDisposable? previous;
				ServiceProxyLease? previousLease;
				lock (this.syncObject)
				{
					previous = this.current;
					previousLease = this.lease;
					this.current = null;
					this.lease = null;
				}

				try
				{
					previous?.Dispose();
				}
				finally
				{
					previousLease?.Dispose();
				}
			}

			internal async Task RestoreAsync(ServiceProxyLease.Generation generation, CancellationToken cancellationToken)
			{
				lock (this.syncObject)
				{
					if (this.disposed || ReferenceEquals(this.lease?.ServiceGeneration, generation))
					{
						return;
					}
				}

				if (!generation.TryRent())
				{
					return;
				}

				ServiceProxyLease replacementLease = new(generation);
				IDisposable? replacement = null;
				bool transferred = false;
				try
				{
					replacement = await restore(generation.Target, cancellationToken).ConfigureAwait(false);
					this.Disconnect();
					if (replacement is null)
					{
						return;
					}

					lock (this.syncObject)
					{
						if (!this.disposed && ReferenceEquals(Volatile.Read(ref owner.current), generation))
						{
							this.current = replacement;
							this.lease = replacementLease;
							transferred = true;
							return;
						}
					}
				}
				finally
				{
					if (!transferred)
					{
						try
						{
							replacement?.Dispose();
						}
						finally
						{
							replacementLease.Dispose();
						}
					}
				}
			}
		}
	}
}
