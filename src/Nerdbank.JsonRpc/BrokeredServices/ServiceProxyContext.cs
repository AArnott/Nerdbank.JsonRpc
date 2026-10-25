// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Supplies a leased service target and local invocation boundary policy.</summary>
public abstract class ServiceProxyContext : IDisposable, IAsyncDisposable
{
	private readonly List<EventAttachment> eventAttachments = [];
	private readonly object eventLock = new();

	/// <summary>Gets the logger used to observe notification and cleanup failures.</summary>
	public ILogger<ServiceProxyContext> Logger { get; init; } = NullLogger<ServiceProxyContext>.Instance;

	/// <summary>Gets the exception disclosure policy for direct calls.</summary>
	/// <remarks>Local failures retain their original inner exception when details are enabled; no typed exception payload is serialized or reconstructed.</remarks>
	public JsonRpcOptions RpcOptions { get; init; } = JsonRpcOptions.Default;

	/// <summary>Gets a value indicating whether the proxy is disposed.</summary>
	public abstract bool IsDisposed { get; }

	/// <summary>Gets the current target for event attachment.</summary>
	protected abstract object? EventTarget { get; }

	/// <summary>Owns a local target for a newly acquired proxy.</summary>
	/// <param name="target">The local service object.</param>
	/// <param name="owner">The object to dispose instead of the target, if supplied.</param>
	/// <param name="translateExceptions">Whether service exceptions cross a local RPC boundary.</param>
	/// <param name="logger">The direct-call logger.</param>
	/// <param name="rpcOptions">The exception disclosure policy.</param>
	/// <returns>The context, which owns the target until disposed.</returns>
	public static ServiceProxyContext Own(object target, IDisposable? owner = null, bool translateExceptions = true, ILogger<ServiceProxyContext>? logger = null, JsonRpcOptions? rpcOptions = null)
		=> new OwnedContext(target ?? throw new ArgumentNullException(nameof(target)), owner, translateExceptions)
		{
			Logger = logger ?? NullLogger<ServiceProxyContext>.Instance,
			RpcOptions = rpcOptions ?? JsonRpcOptions.Default,
		};

	/// <summary>Creates a callback boundary without taking ownership of the caller's target.</summary>
	/// <param name="target">The caller-owned callback target.</param>
	/// <param name="logger">The callback logger.</param>
	/// <param name="rpcOptions">The exception disclosure policy.</param>
	/// <returns>The disposable boundary. Disposing it does not dispose the supplied target.</returns>
	public static ServiceProxyContext Borrow(object target, ILogger<ServiceProxyContext>? logger = null, JsonRpcOptions? rpcOptions = null)
		=> Own(target, NoopDisposable.Instance, logger: logger, rpcOptions: rpcOptions);

	/// <summary>Acquires a target for the duration of an invocation.</summary>
	/// <param name="cancellationToken">Cancels acquisition.</param>
	/// <returns>A lease that must be disposed exactly once.</returns>
	public abstract ValueTask<ServiceProxyLease> RentAsync(CancellationToken cancellationToken);

	/// <summary>Tests whether a generated optional interface is currently available.</summary>
	/// <param name="interfaceType">The optional interface.</param>
	/// <returns>Whether the context currently supports it.</returns>
	public virtual bool IsInterfaceEnabled(Type interfaceType) => !this.IsDisposed;

	/// <summary>Translates a local target exception without serializing arguments or results.</summary>
	/// <param name="exception">The target failure.</param>
	/// <returns>The exception to report to the caller.</returns>
	public virtual Exception TranslateException(Exception exception)
	{
		if (exception is null)
		{
			throw new ArgumentNullException(nameof(exception));
		}

		if (exception is OperationCanceledException || (exception is RemoteInvocationException && this.RpcOptions.IncludeExceptionDetails))
		{
			return exception;
		}

		if (exception is JsonRpcException explicitError && exception is not RemoteInvocationException)
		{
			return new RemoteInvocationException(explicitError.ErrorDetails, null, null);
		}

		this.Logger.LogWarning(exception, "A local service request failed.");
		RemoteExceptionData? details = this.RpcOptions.IncludeExceptionDetails
			? RemoteExceptionData.CaptureLocal(exception, ex => this.Logger.LogWarning(ex, "Local exception diagnostics could not be captured."))
			: null;
		return new RemoteInvocationException(
			new JsonRpcErrorDetails { Code = JsonRpcErrorCode.InvocationError, Message = details?.Message ?? "The request could not be completed." },
			details,
			details is not null ? exception : null);
	}

	/// <summary>Isolates observer callbacks from local service code without serialization.</summary>
	/// <typeparam name="T">The observed value.</typeparam>
	/// <param name="observer">The caller's observer.</param>
	/// <returns>A direct callback boundary.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(observer))]
	public IObserver<T>? Wrap<T>(IObserver<T>? observer) => this.Wrap(observer, out _);

	/// <summary>Creates an observer callback boundary that can be revoked if activation or invocation fails.</summary>
	/// <typeparam name="T">The observed value.</typeparam>
	/// <param name="observer">The caller's observer.</param>
	/// <param name="boundary">The borrowed boundary, never the caller's observer.</param>
	/// <returns>The service's observer argument.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(observer))]
	public IObserver<T>? Wrap<T>(IObserver<T>? observer, out IDisposable? boundary)
	{
		boundary = null;
		if (observer is null)
		{
			return null;
		}

		if (observer is LocalObserver<T> { Exported: true } exported && ReferenceEquals(exported.Origin, this))
		{
			return exported.Original;
		}

		LocalObserver<T> wrapped = new(this, observer);
		boundary = wrapped;
		return wrapped;
	}

	/// <summary>Isolates progress callbacks from local service code without serialization.</summary>
	/// <typeparam name="T">The progress value.</typeparam>
	/// <param name="progress">The caller's progress receiver.</param>
	/// <returns>A direct callback boundary.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(progress))]
	public IProgress<T>? Wrap<T>(IProgress<T>? progress) => this.Wrap(progress, out _);

	/// <summary>Creates a progress callback boundary that lasts for one invocation or enumeration.</summary>
	/// <typeparam name="T">The progress value.</typeparam>
	/// <param name="progress">The caller's receiver.</param>
	/// <param name="boundary">The boundary to dispose when the invocation ends.</param>
	/// <returns>The service's progress argument.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(progress))]
	public IProgress<T>? Wrap<T>(IProgress<T>? progress, out IDisposable? boundary)
	{
		LocalProgress<T>? wrapped = progress is null ? null : new(this, progress);
		boundary = wrapped;
		return wrapped;
	}

	/// <summary>Retains a generation for a returned observer without taking ownership of its target.</summary>
	/// <typeparam name="T">The observed value.</typeparam>
	/// <param name="observer">The returned observer.</param>
	/// <param name="lease">The active invocation.</param>
	/// <returns>The observer boundary, or the caller's original observer when round-tripping.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(observer))]
	public IObserver<T>? Retain<T>(IObserver<T>? observer, ServiceProxyLease lease)
	{
		if (observer is null)
		{
			return null;
		}

		if (observer is LocalObserver<T> { Exported: false } borrowed && ReferenceEquals(borrowed.Origin, this))
		{
			return borrowed.Original;
		}

		ServiceProxyContext lifetime = new OwnedContext(observer, new RetainedServiceObject(observer, lease.Retain(), disposeTarget: observer is LocalObserver<T> or ServiceProxy), true, this, exported: true)
		{
			Logger = this.Logger,
			RpcOptions = this.RpcOptions,
		};
		return new LocalObserver<T>(this, observer, lifetime);
	}

	/// <summary>Retains a generation until a returned stream is disposed.</summary>
	/// <param name="stream">The returned stream.</param>
	/// <param name="lease">The active invocation.</param>
	/// <returns>The caller-owned stream boundary.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(stream))]
	public Stream? Retain(Stream? stream, ServiceProxyLease lease) => stream is null ? null : new RetainedServiceStream(stream, lease.Retain());

	/// <summary>Retains a generation until a returned reader is completed.</summary>
	/// <param name="reader">The returned reader.</param>
	/// <param name="lease">The active invocation.</param>
	/// <returns>The caller-owned reader boundary.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(reader))]
	public PipeReader? Retain(PipeReader? reader, ServiceProxyLease lease) => reader is null ? null : new RetainedServicePipeReader(reader, lease.Retain());

	/// <summary>Retains a generation until a returned writer is completed.</summary>
	/// <param name="writer">The returned writer.</param>
	/// <param name="lease">The active invocation.</param>
	/// <returns>The caller-owned writer boundary.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(writer))]
	public PipeWriter? Retain(PipeWriter? writer, ServiceProxyLease lease) => writer is null ? null : new RetainedServicePipeWriter(writer, lease.Retain());

	/// <summary>Retains a generation until both returned pipe ends are completed.</summary>
	/// <param name="pipe">The returned pipe.</param>
	/// <param name="lease">The active invocation.</param>
	/// <returns>The caller-owned pipe boundary.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(pipe))]
	public IDuplexPipe? Retain(IDuplexPipe? pipe, ServiceProxyLease lease) => pipe is null ? null : new RetainedServiceDuplexPipe(pipe, lease);

	/// <summary>Creates a generated direct proxy for a returned marshalable object.</summary>
	/// <typeparam name="T">The returned source-generated contract.</typeparam>
	/// <param name="target">The returned object.</param>
	/// <param name="lease">The active invocation.</param>
	/// <returns>A caller-owned object boundary retaining its parent generation.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(target))]
	public T? RetainProxy<T>(T? target, ServiceProxyLease lease)
		where T : class
	{
		if (target is null)
		{
			return null;
		}

		if (this.TryUnwrapProxy(target, exported: false, out T? original))
		{
			return original;
		}

		ServiceProxyContext context = new OwnedContext(target, new RetainedServiceObject(target, lease.Retain()), translateExceptions: true, this, exported: true)
		{
			Logger = this.Logger,
			RpcOptions = this.RpcOptions,
		};
		try
		{
			return ServiceProxyFactoryAttribute.CreateForTarget<T>(context, target);
		}
		catch
		{
			context.Dispose();
			throw;
		}
	}

	/// <summary>Creates a borrowed generated callback boundary, preserving round trips of service-owned objects.</summary>
	/// <typeparam name="T">The source-generated callback interface.</typeparam>
	/// <param name="target">The caller-owned target.</param>
	/// <returns>The borrowed proxy, or the original service object when returning across its own boundary.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(target))]
	public T? WrapProxy<T>(T? target)
		where T : class => this.WrapProxy(target, out _);

	/// <summary>Creates a callback boundary whose lifetime can be limited to one invocation.</summary>
	/// <typeparam name="T">The source-generated callback interface.</typeparam>
	/// <param name="target">The caller-owned target.</param>
	/// <param name="boundary">The boundary to dispose when the call or returned enumeration ends; never the caller's target.</param>
	/// <returns>The generated callback proxy.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(target))]
	public T? WrapProxy<T>(T? target, out IDisposable? boundary)
		where T : class
	{
		boundary = null;
		if (target is null)
		{
			return null;
		}

		if (this.TryUnwrapProxy(target, exported: true, out T? original))
		{
			return original;
		}

		ServiceProxyContext context = new OwnedContext(target, NoopDisposable.Instance, translateExceptions: true, this)
		{
			Logger = this.Logger,
			RpcOptions = this.RpcOptions,
		};
		try
		{
			T proxy = ServiceProxyFactoryAttribute.CreateForTarget<T>(context, target);
			boundary = context;
			return proxy;
		}
		catch
		{
			context.Dispose();
			throw;
		}
	}

	/// <summary>Enforces the request-argument-only rule for call-scoped contracts and progress receivers on direct calls.</summary>
	/// <typeparam name="T">The call-scoped contract.</typeparam>
	/// <param name="target">The returned value.</param>
	/// <param name="lease">The active invocation.</param>
	/// <returns>Null when the result is null; non-null call-scoped results always fail.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(target))]
	public T? RejectArgumentOnlyResult<T>(T? target, ServiceProxyLease lease)
		where T : class => target is null ? null : throw new InvalidOperationException("Call-scoped RPC objects and progress receivers cannot be returned from service methods.");

	/// <summary>Retains a service generation while a returned subscription is active.</summary>
	/// <param name="subscription">The service's subscription.</param>
	/// <param name="lease">The active invocation lease.</param>
	/// <param name="resubscribe">The generated operation for restoring observer subscriptions after replacement.</param>
	/// <returns>A subscription that releases its generation exactly once.</returns>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(subscription))]
	public virtual IDisposable? RetainSubscription(IDisposable? subscription, ServiceProxyLease lease, Func<object, CancellationToken, ValueTask<IDisposable?>>? resubscribe = null)
		=> subscription is null ? null : new Subscription(subscription, lease.Retain(), this);

	/// <summary>Observes a callback failure without throwing into service code.</summary>
	/// <param name="exception">The callback failure.</param>
	public void ReportCallbackException(Exception exception) => this.Logger.LogError(exception, "A local service event handler failed.");

	/// <summary>Updates an event attachment owned by this proxy.</summary>
	/// <typeparam name="THandler">The event delegate.</typeparam>
	/// <param name="key">The generated event identity.</param>
	/// <param name="handler">The client handler.</param>
	/// <param name="attach">The generated typed add operation.</param>
	/// <param name="detach">The generated typed remove operation.</param>
	/// <param name="add">Whether to add rather than remove.</param>
	/// <param name="wrap">The generated callback boundary, if needed.</param>
	public void UpdateEvent<THandler>(string key, THandler? handler, Action<object, THandler> attach, Action<object, THandler> detach, bool add, Func<THandler, THandler>? wrap = null)
		where THandler : Delegate
	{
		if (attach is null)
		{
			throw new ArgumentNullException(nameof(attach));
		}

		if (detach is null)
		{
			throw new ArgumentNullException(nameof(detach));
		}

		if (handler is null)
		{
			return;
		}

		lock (this.eventLock)
		{
			if (this.IsDisposed)
			{
				throw new ObjectDisposedException(nameof(ServiceProxyContext));
			}

			if (add)
			{
				THandler callback = wrap is null ? handler : wrap(handler);
				EventAttachment attachment = new(key, handler, target => attach(target, callback), target => detach(target, callback));
				attachment.MoveTo(this.EventTarget);
				if (this.IsDisposed)
				{
					attachment.MoveTo(null);
					throw new ObjectDisposedException(nameof(ServiceProxyContext));
				}

				this.eventAttachments.Add(attachment);
			}
			else
			{
				int index = this.eventAttachments.FindLastIndex(item => item.Key == key && item.Handler.Equals(handler));
				if (index >= 0)
				{
					EventAttachment attachment = this.eventAttachments[index];
					attachment.MoveTo(null);
					this.eventAttachments.Remove(attachment);
				}
			}
		}
	}

	/// <summary>Observes a generated fire-and-forget invocation.</summary>
	/// <param name="operation">The invocation to observe.</param>
	public void ObserveNotification(ValueTask operation) => _ = this.ObserveNotificationAsync(operation);

	/// <summary>Disposes this context and relinquishes its owning reference.</summary>
	public abstract void Dispose();

	/// <summary>Disposes this context and awaits owned resource cleanup.</summary>
	/// <returns>A task tracking cleanup.</returns>
	public abstract ValueTask DisposeAsync();

	/// <summary>Owns a service and its borrowed callback boundary.</summary>
	/// <param name="target">The service implementation.</param>
	/// <param name="callback">The generated callback proxy.</param>
	/// <param name="logger">The direct-call logger.</param>
	/// <param name="rpcOptions">The exception disclosure policy.</param>
	/// <returns>The shared activation lifetime.</returns>
	internal static ServiceProxyContext OwnActivation(object target, object callback, ILogger<ServiceProxyContext> logger, JsonRpcOptions rpcOptions)
		=> new OwnedContext(target, new ServiceLifetime(target, callback), translateExceptions: true) { Logger = logger, RpcOptions = rpcOptions };

	/// <summary>Detaches all event subscriptions before releasing a target.</summary>
	protected void DetachEvents()
	{
		lock (this.eventLock)
		{
			try
			{
				this.MoveEvents(null);
			}
			finally
			{
				this.eventAttachments.Clear();
			}
		}
	}

	/// <summary>Moves event attachments to a replacement generation.</summary>
	/// <param name="target">The replacement target, or null while unavailable.</param>
	protected void MoveEvents(object? target)
	{
		lock (this.eventLock)
		{
			List<Exception>? failures = null;
			foreach (EventAttachment attachment in this.eventAttachments.ToArray())
			{
				if (!this.eventAttachments.Contains(attachment))
				{
					continue;
				}

				try
				{
					attachment.MoveTo(this.IsDisposed ? null : target);
				}
				catch (Exception ex)
				{
					(failures ??= []).Add(ex);
				}
			}

			if (failures is not null)
			{
				throw new AggregateException("Service event attachments could not be updated.", failures);
			}
		}
	}

	private bool TryUnwrapProxy<T>(T target, bool exported, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? original)
		where T : class
	{
		if (target is ServiceProxy { ProxyContext: OwnedContext context } && context.Unwrap(this, exported) is { } value)
		{
			original = (T)value;
			return true;
		}

		original = null;
		return false;
	}

	private async Task ObserveNotificationAsync(ValueTask operation)
	{
		try
		{
			await operation.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			this.Logger.LogError(ex, "A brokered-service notification failed.");
		}
	}

	private sealed class EventAttachment(string key, Delegate handler, Action<object> attach, Action<object> detach)
	{
		private object? target;
		private int version;

		internal string Key => key;

		internal Delegate Handler => handler;

		internal void MoveTo(object? next)
		{
			int requestVersion = ++this.version;
			if (ReferenceEquals(this.target, next))
			{
				return;
			}

			if (this.target is { } previous)
			{
				this.target = null;
				detach(previous);
			}

			if (requestVersion != this.version)
			{
				return;
			}

			if (next is not null)
			{
				try
				{
					attach(next);
				}
				catch (Exception attachmentFailure)
				{
					try
					{
						detach(next);
					}
					catch (Exception cleanupFailure)
					{
						throw new AggregateException(attachmentFailure, cleanupFailure);
					}

					throw;
				}

				if (requestVersion == this.version)
				{
					this.target = next;
				}
				else
				{
					detach(next);
				}
			}
		}
	}

	private sealed class LocalObserver<T>(ServiceProxyContext origin, IObserver<T> observer, ServiceProxyContext? lifetime = null) : IObserver<T>, IDisposable, IAsyncDisposable
	{
		private int disposed;

		internal ServiceProxyContext Origin => origin;

		internal bool Exported => lifetime is not null;

		internal IObserver<T> Original => !this.IsDisposed ? observer : throw new ObjectDisposedException(nameof(LocalObserver<T>));

		private bool IsDisposed => Volatile.Read(ref this.disposed) != 0 || (lifetime ?? origin).IsDisposed;

		public void OnNext(T value)
		{
			if (this.IsDisposed)
			{
				origin.Logger.LogDebug("An observer callback was ignored after its boundary closed.");
				return;
			}

			try
			{
				observer.OnNext(value);
			}
			catch (Exception ex)
			{
				origin.Logger.LogError(ex, "A local observer OnNext callback failed.");
			}
		}

		public void OnCompleted()
		{
			if (this.IsDisposed)
			{
				origin.Logger.LogDebug("An observer completion was ignored after its boundary closed.");
				return;
			}

			try
			{
				observer.OnCompleted();
			}
			catch (Exception ex)
			{
				origin.Logger.LogError(ex, "A local observer OnCompleted callback failed.");
			}
			finally
			{
				this.Dispose();
			}
		}

		public void OnError(Exception error)
		{
			if (this.IsDisposed)
			{
				origin.Logger.LogDebug("An observer error was ignored after its boundary closed.");
				return;
			}

			try
			{
				observer.OnError(error);
			}
			catch (Exception ex)
			{
				origin.Logger.LogError(ex, "A local observer OnError callback failed.");
			}
			finally
			{
				this.Dispose();
			}
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref this.disposed, 1) == 0)
			{
				lifetime?.Dispose();
			}
		}

		public async ValueTask DisposeAsync()
		{
			this.Dispose();
			if (lifetime is not null)
			{
				await lifetime.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	private sealed class LocalProgress<T>(ServiceProxyContext context, IProgress<T> progress) : IProgress<T>, IDisposable
	{
		private int disposed;

		public void Report(T value)
		{
			if (Volatile.Read(ref this.disposed) != 0 || context.IsDisposed)
			{
				context.Logger.LogDebug("A progress report was ignored after its call completed.");
				return;
			}

			try
			{
				progress.Report(value);
			}
			catch (Exception ex)
			{
				context.Logger.LogError(ex, "A local progress callback failed.");
			}
		}

		public void Dispose() => Interlocked.Exchange(ref this.disposed, 1);
	}

	private sealed class Subscription(IDisposable subscription, ServiceProxyLease lease, ServiceProxyContext context) : IDisposable
	{
		private int disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref this.disposed, 1) != 0)
			{
				return;
			}

			try
			{
				subscription.Dispose();
			}
			catch (Exception ex)
			{
				throw context.TranslateException(ex);
			}
			finally
			{
				lease.Dispose();
			}
		}
	}

	private sealed class NoopDisposable : IDisposable
	{
		internal static readonly NoopDisposable Instance = new();

		public void Dispose()
		{
		}
	}

	private sealed class OwnedContext : ServiceProxyContext
	{
		private readonly ServiceProxyLease.Generation generation;
		private readonly bool translateExceptions;
		private readonly ServiceProxyContext? origin;
		private readonly bool exported;
		private int disposed;

		internal OwnedContext(object target, object? owner, bool translateExceptions, ServiceProxyContext? origin = null, bool exported = false)
		{
			this.generation = new(target, owner ?? target, ex => this.Logger.LogError(ex, "Local service cleanup failed."));
			this.translateExceptions = translateExceptions;
			this.origin = origin;
			this.exported = exported;
		}

		public override bool IsDisposed => Volatile.Read(ref this.disposed) != 0 || (!this.exported && this.origin?.IsDisposed == true);

		protected override object EventTarget => this.generation.Target;

		public override bool IsInterfaceEnabled(Type interfaceType)
			=> !this.IsDisposed && (this.generation.Target is IClientProxy proxy ? proxy.Is(interfaceType) : interfaceType.IsInstanceOfType(this.generation.Target));

		public override ValueTask<ServiceProxyLease> RentAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (this.IsDisposed || !this.generation.TryRent())
			{
				throw new ObjectDisposedException(nameof(ServiceProxyContext));
			}

			return new(new ServiceProxyLease(this.generation));
		}

		public override Exception TranslateException(Exception exception) => this.translateExceptions ? base.TranslateException(exception) : exception;

		public override void Dispose()
		{
			if (Interlocked.Exchange(ref this.disposed, 1) == 0)
			{
				try
				{
					this.DetachEvents();
				}
				finally
				{
					this.generation.Release();
				}
			}
		}

		public override async ValueTask DisposeAsync()
		{
			this.Dispose();
#pragma warning disable VSTHRD003 // Generation cleanup is completed asynchronously when the last invocation lease exits.
			await this.generation.Completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}

		/// <summary>Recognizes a target returning across the boundary that originally wrapped it.</summary>
		/// <param name="boundary">The original boundary.</param>
		/// <param name="exported">Whether to recognize an exported result rather than a borrowed argument.</param>
		/// <returns>The original target, or null when this is a different boundary.</returns>
		internal object? Unwrap(ServiceProxyContext boundary, bool exported)
		{
			if (!ReferenceEquals(this.origin, boundary) || this.exported != exported)
			{
				return null;
			}

			return !this.IsDisposed ? this.generation.Target : throw new ObjectDisposedException(nameof(ServiceProxy));
		}
	}
}
