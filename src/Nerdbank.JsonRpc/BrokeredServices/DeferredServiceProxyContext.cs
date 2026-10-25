// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Registers a generated server target before asynchronous activation can issue callbacks.</summary>
internal sealed class DeferredServiceProxyContext : ServiceProxyContext
{
	private readonly Func<CancellationToken, ValueTask<object?>> activate;
	private readonly ServiceMoniker moniker;
	private readonly object syncObject = new();
	private readonly CancellationTokenSource shutdown = new();
	private Task<ServiceProxyContext>? activation;
	private ServiceProxyContext? owned;
	private object? target;
	private bool disposed;

	/// <summary>Initializes a new instance of the <see cref="DeferredServiceProxyContext"/> class.</summary>
	/// <param name="moniker">The service identity used for unavailable errors.</param>
	/// <param name="activate">The activation operation.</param>
	internal DeferredServiceProxyContext(ServiceMoniker moniker, Func<CancellationToken, ValueTask<object?>> activate)
	{
		this.moniker = moniker;
		this.activate = activate;
	}

	/// <inheritdoc/>
	public override bool IsDisposed => Volatile.Read(ref this.disposed);

	/// <inheritdoc/>
	protected override object? EventTarget => Volatile.Read(ref this.target);

	/// <inheritdoc/>
	public override Exception TranslateException(Exception exception) => exception;

	/// <inheritdoc/>
	public override async ValueTask<ServiceProxyLease> RentAsync(CancellationToken cancellationToken)
	{
		ServiceProxyContext context = await this.GetActivationAsync().WithCancellation(cancellationToken).ConfigureAwait(false);
		return await context.RentAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public override void Dispose()
	{
		ServiceProxyContext? context;
		lock (this.syncObject)
		{
			if (this.disposed)
			{
				return;
			}

			this.disposed = true;
			context = this.owned;
		}

		try
		{
			this.shutdown.Cancel();
		}
		finally
		{
			try
			{
				this.DetachEvents();
			}
			finally
			{
				context?.Dispose();
			}
		}
	}

	/// <inheritdoc/>
	public override async ValueTask DisposeAsync()
	{
		this.Dispose();
		Task<ServiceProxyContext>? pending;
		lock (this.syncObject)
		{
			pending = this.activation;
		}

		if (pending is not null)
		{
			ServiceProxyContext context;
			try
			{
#pragma warning disable VSTHRD003 // Activation is an asynchronous, connection-scoped operation with no synchronous waiter.
				context = await pending.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
			catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				this.Logger.LogError(ex, "Deferred service activation failed during cleanup.");
				return;
			}

			await context.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <summary>Starts activation after the server has registered all generated dispatchers.</summary>
	/// <returns>The activation operation.</returns>
	internal async Task InitializeAsync() => await this.GetActivationAsync().ConfigureAwait(false);

	private Task<ServiceProxyContext> GetActivationAsync()
	{
		lock (this.syncObject)
		{
			if (this.disposed)
			{
				throw new ObjectDisposedException(nameof(DeferredServiceProxyContext));
			}

			return this.activation ??= this.ActivateCoreAsync();
		}
	}

	private async Task<ServiceProxyContext> ActivateCoreAsync()
	{
		await Task.Yield();
		object? service = await this.activate(this.shutdown.Token).ConfigureAwait(false);
		if (service is null)
		{
			throw new BrokeredServiceUnavailableException(this.moniker);
		}

		ServiceProxyContext context = Own(service);
		bool accepted;
		lock (this.syncObject)
		{
			accepted = !this.disposed;
			if (accepted)
			{
				this.owned = context;
				this.target = service;
			}
		}

		if (!accepted)
		{
			await context.DisposeAsync().ConfigureAwait(false);
			throw new OperationCanceledException(this.shutdown.Token);
		}

		try
		{
			this.MoveEvents(service);
			return context;
		}
		catch
		{
			await context.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}
}
