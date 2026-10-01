// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NET
using System.Diagnostics.CodeAnalysis;
#endif

using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

[TypeShape(Kind = TypeShapeKind.None)]
public partial class JsonRpc : IDisposableObservable, IJsonRpcClient, IArgumentsBuilderContext
{
	internal const string SpecialCancelMethodName = "$/cancelRequest";

	/// <summary>
	/// A <see cref="System.Threading.SynchronizationContext"/> that schedules work to the thread pool with no ordering guarantees.
	/// </summary>
	/// <remarks>Used when <see cref="SynchronizationContext"/> is set to <see langword="null"/>. A base <see cref="System.Threading.SynchronizationContext"/> posts to the thread pool, so no two dispatches are serialized against each other.</remarks>
	private static readonly SynchronizationContext UnorderedDispatchSynchronizationContext = new();

	/// <summary>
	/// Proxy factories found by <see cref="Attach(Type, JsonRpcProxyOptions?)"/>, keyed by interface.
	/// A <see langword="null"/> value records that the interface has no generated proxy.
	/// </summary>
	/// <remarks>Allocated on first use so that apps that only use <see cref="Attach{T}(JsonRpcProxyOptions?)"/> never pay for it.</remarks>
	private static ConcurrentDictionary<Type, ProxyFactoryRegistration?>? proxyFactoriesByType;

	/// <summary>Requests being dispatched, keyed by ID. Guarded by locking the dictionary itself.</summary>
	/// <remarks>A locked <see cref="Dictionary{TKey, TValue}"/> stores entries inline, where a concurrent dictionary would allocate a node per request.</remarks>
	private readonly Dictionary<RequestId, PendingInboundRequest> pendingInboundRequests = [];

	private readonly MarshaledObjectManager marshaledObjects;
	private readonly ProgressManager progress;
	private readonly OutOfBandStreamManager outOfBandStreams;
	private readonly AsyncEnumerableManager asyncEnumerables;
	private readonly TaskCompletionSource<bool> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly object connectionSync = new();
	private readonly object targetRegistrationSync = new();
	private readonly CancellationTokenSource disposalSource = new();
	private readonly ConcurrentDictionary<string, (object? Target, MethodInvoker Invoker)> handlers = new();
	private readonly List<IDisposable> eventSubscriptions = [];

	/// <summary>Requests awaiting responses, keyed by ID. Guarded by <see cref="connectionSync"/>.</summary>
	private readonly Dictionary<RequestId, TaskCompletionSource<JsonRpcResponse>> pendingOutboundRequests = [];

	private readonly Action<object?> cancelOutboundRequestDelegate;
	private readonly JsonRpcPipeChannel channel;
	private readonly JsonRpcSerializer userDataSerializer;
	private bool disposed;
	private Task? readerTask;
	private int nextRequestId;

	/// <summary>
	/// Initializes a new instance of the <see cref="JsonRpc"/> class over a pipe channel.
	/// </summary>
	/// <param name="channel">The channel used to exchange messages.</param>
	public JsonRpc(JsonRpcPipeChannel channel)
	{
		this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
		JsonRpcSerializer serializer = channel.Serializer ?? throw new ArgumentException("The channel must supply a serializer.", nameof(channel));
		if (channel.Encoding != serializer.Encoding)
		{
			throw new ArgumentException("The channel encoding must match its serializer.", nameof(channel));
		}

		this.marshaledObjects = new(this);
		this.progress = new(this);
		this.outOfBandStreams = new();
		this.asyncEnumerables = new(this);
		this.userDataSerializer = serializer.WithMarshaledObjectManager(this.marshaledObjects, this.progress, this.outOfBandStreams, this.asyncEnumerables);

		// Store a delegate we can reuse to avoid allocations.
		this.cancelOutboundRequestDelegate = this.CancelOutboundRequest;

		this.AddRpcTarget(new SpecialMethodsTarget(this));
	}

	/// <summary>Gets or initializes the maximum encoded message size, in bytes.</summary>
	/// <value>Defaults to 8 MiB. The built-in JSON and MessagePack channels apply this limit to received messages; the JSON channel also applies it to sent messages.</value>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when set to zero or a negative value.</exception>
	public int MaximumMessageSize
	{
		get => this.channel.GetMaximumMessageSize();
		init => this.channel.SetMaximumMessageSize(value);
	}

	/// <summary>Gets or initializes the multiplexing stream used to send and receive out-of-band streams.</summary>
	/// <remarks>Initialize this property before calling <see cref="Start"/>.</remarks>
	public MultiplexingStream? MultiplexingStream
	{
		get => this.outOfBandStreams.MultiplexingStream;
		init => this.outOfBandStreams.MultiplexingStream = value;
	}

	/// <summary>Gets or initializes the options used for proxies implicitly created for RPC-marshalable objects.</summary>
	/// <value>Defaults to <see cref="JsonRpcProxyOptions.Default"/>, the same naming convention used for ordinary RPC proxies.</value>
	/// <remarks>
	/// To communicate with StreamJsonRpc's default RPC-marshalable objects, set this property to
	/// <c>new() { MethodNameTransform = CommonMethodNameTransforms.Identity }</c> in the <see cref="JsonRpc"/> object initializer.
	/// This does not change options for proxies attached through <see cref="Attach{T}(JsonRpcProxyOptions?)"/>.
	/// </remarks>
	/// <exception cref="ArgumentNullException">Thrown when set to <see langword="null"/>.</exception>
	public JsonRpcProxyOptions MarshaledProxyOptions
	{
		get => field ??= JsonRpcProxyOptions.Default;
		init => field = Requires.NotNull(value);
	}

	/// <summary>Gets or initializes the options used when implicitly registering RPC-marshalable targets.</summary>
	/// <value>Defaults to <see cref="JsonRpcTargetOptions.Default"/>, the same naming convention used for ordinary RPC targets.</value>
	/// <remarks>
	/// To communicate with StreamJsonRpc's default RPC-marshalable objects, set this property to
	/// <c>new() { MethodNameTransform = CommonMethodNameTransforms.Identity }</c> in the <see cref="JsonRpc"/> object initializer.
	/// This does not change options for targets registered through <see cref="AddRpcTarget{T}(T, ITypeShape{T}, JsonRpcTargetOptions?)"/>.
	/// </remarks>
	/// <exception cref="ArgumentNullException">Thrown when set to <see langword="null"/>.</exception>
	public JsonRpcTargetOptions MarshaledTargetOptions
	{
		get => field ??= JsonRpcTargetOptions.Default;
		init => field = Requires.NotNull(value);
	}

	/// <summary>Gets or initializes the logger for request, connection, and transport diagnostics.</summary>
	/// <value>Defaults to <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance"/>.</value>
	/// <remarks>Setting this property also configures the underlying channel to use the same logger.</remarks>
	public ILogger Logger
	{
		get => this.channel.GetLogger();
		init => this.channel.SetLogger(value);
	}

	/// <summary>
	/// Gets or initializes the <see cref="Microsoft.VisualStudio.Threading.JoinableTaskFactory"/> to participate in to mitigate deadlocks with the main thread.
	/// </summary>
	/// <value>Defaults to <see langword="null"/>.</value>
	/// <remarks>
	/// <para>
	/// When set, outbound requests carry a token that identifies the caller's <see cref="JoinableTask"/> (if any),
	/// and inbound requests that carry such a token are dispatched within a <see cref="JoinableTask"/> that is joined to it.
	/// This allows a remote party to call back into this process and reach the main thread while the original caller blocks it
	/// waiting on the outbound request.
	/// </para>
	/// <para>
	/// The token is exchanged as the top-level <c>joinableTaskToken</c> JSON-RPC envelope property, compatible with StreamJsonRpc.
	/// Initialize this property before calling <see cref="Start"/>.
	/// </para>
	/// </remarks>
	public JoinableTaskFactory? JoinableTaskFactory
	{
		get => field;
		init => field = value;
	}

	/// <summary>
	/// Gets or initializes the <see cref="JoinableTaskTokenTracker"/> used to forward <see cref="JoinableTask"/> tokens
	/// from inbound requests to outbound requests when <see cref="JoinableTaskFactory"/> is <see langword="null"/>.
	/// </summary>
	/// <value>Defaults to an instance shared with all other <see cref="JsonRpc"/> instances that do not set this property.</value>
	/// <remarks>
	/// <para>This property is ignored when <see cref="JoinableTaskFactory"/> is set.</para>
	/// <para>
	/// Set this only in advanced scenarios where one process has many <see cref="JsonRpc"/> instances connected to different
	/// remote parties and correlating tokens across them is undesirable.
	/// Initialize this property before calling <see cref="Start"/>.
	/// </para>
	/// </remarks>
	public JoinableTaskTokenTracker JoinableTaskTracker
	{
		get => field ??= JoinableTaskTokenTracker.Default;
		init => field = Requires.NotNull(value);
	}

	/// <summary>
	/// Gets or initializes the <see cref="System.Threading.SynchronizationContext"/> that schedules the start of each inbound RPC method invocation.
	/// </summary>
	/// <value>
	/// Defaults to a private <see cref="NonConcurrentSynchronizationContext"/> instance (configured as non-sticky)
	/// that guarantees inbound method invocations <em>begin</em> in the order the remote party sent the requests.
	/// </value>
	/// <remarks>
	/// <para>
	/// With the default value, inbound requests are dispatched on the thread pool, one at a time, in the order they arrive.
	/// Because the default context is non-sticky, it does not become <see cref="System.Threading.SynchronizationContext.Current"/>
	/// while a method runs. As soon as a method yields at its first <see langword="await"/> (or returns), the next queued method
	/// may begin. Long-running methods therefore execute and complete concurrently with each other and in any order;
	/// only the order in which they <em>start</em> is guaranteed to match the order the client sent them.
	/// </para>
	/// <para>
	/// Set this property to <see langword="null"/> to remove the ordering guarantee entirely. Each inbound invocation is then
	/// queued to the thread pool independently, so invocations may begin in any order and with full concurrency.
	/// This offers the highest throughput and is appropriate when the order in which methods start does not matter.
	/// </para>
	/// <para>
	/// Alternatively, initialize this property with your own <see cref="System.Threading.SynchronizationContext"/> (for example, one that
	/// marshals to an application's main thread) to have every inbound method invocation begin execution on that context.
	/// A context that runs callbacks one at a time preserves the ordering guarantee described above; one that runs them
	/// concurrently does not. As with the default, a method's continuations after its first <see langword="await"/> are
	/// subject to normal <see langword="await"/> semantics and only return to this context if the context applies itself
	/// as <see cref="System.Threading.SynchronizationContext.Current"/> and the method does not use
	/// <see cref="Task.ConfigureAwait(bool)"/> with <see langword="false"/>.
	/// </para>
	/// <para>
	/// Work that precedes the invocation (such as request parsing and cancellation bookkeeping) always runs on the reader
	/// loop in message order and is unaffected by this property.
	/// </para>
	/// <para>
	/// Inbound <c>$/cancelRequest</c> notifications are exempt from this property and always begin on the thread pool.
	/// Because their purpose is to interrupt work that is already running, queueing them behind that work would prevent
	/// a handler that occupies the dispatcher without yielding from ever being canceled.
	/// </para>
	/// </remarks>
	public SynchronizationContext? SynchronizationContext { get; init; } = new NonConcurrentSynchronizationContext(sticky: false);

	JsonRpcSerializer IJsonRpcClient.Serializer => this.userDataSerializer;

	JsonRpcSerializer IArgumentsBuilderContext.Serializer => this.userDataSerializer;

	MarshaledObjectManager IArgumentsBuilderContext.MarshaledObjects => this.marshaledObjects;

	ProgressManager IArgumentsBuilderContext.Progress => this.progress;

	OutOfBandStreamManager IArgumentsBuilderContext.OutOfBandStreams => this.outOfBandStreams;

	AsyncEnumerableManager IArgumentsBuilderContext.AsyncEnumerables => this.asyncEnumerables;

	public JsonRpcState State =>
		this.Completion.IsFaulted ? JsonRpcState.Faulted :
		this.IsDisposed ? JsonRpcState.Disposed :
		this.readerTask is not null ? JsonRpcState.Running :
		JsonRpcState.NotStarted;

	public Task Completion => this.completionSource.Task;

	public bool IsDisposed => this.disposalSource.IsCancellationRequested;

	internal CancellationToken DisposalToken => this.disposalSource.Token;

	/// <summary>Gets the channel used by this connection.</summary>
	internal JsonRpcPipeChannel Channel => this.channel;

	internal JsonRpcSerializer UserDataSerializer => this.userDataSerializer;

	internal MarshaledObjectManager MarshaledObjects => this.marshaledObjects;

	internal ProgressManager Progress => this.progress;

	internal OutOfBandStreamManager OutOfBandStreams => this.outOfBandStreams;

	/// <summary>Gets the manager that tracks <see cref="IAsyncEnumerable{T}"/> generators for this connection.</summary>
	internal AsyncEnumerableManager AsyncEnumerables => this.asyncEnumerables;

	/// <summary>Gets the <see cref="System.Threading.SynchronizationContext"/> that dispatch should use, falling back to a thread pool context that imposes no ordering when <see cref="SynchronizationContext"/> is <see langword="null"/>.</summary>
	private SynchronizationContext DispatchSynchronizationContext => this.SynchronizationContext ?? UnorderedDispatchSynchronizationContext;

	/// <inheritdoc/>
	public JsonRpcArgumentsBuilder CreateArguments(bool named, int count, CancellationToken cancellationToken = default) => new(this, named, count, cancellationToken);

#if NET
	/// <summary>
	/// Registers a local object's public instance methods as JSON-RPC targets, invoked when incoming requests match their JSON-RPC method names.
	/// </summary>
	/// <typeparam name="T">The statically shaped type describing which members of <paramref name="target"/> to register.</typeparam>
	/// <param name="target">The object whose methods should be invoked in response to matching incoming requests and notifications.</param>
	/// <param name="options">Options controlling method name resolution for this target. When <see langword="null"/>, default options are used.</param>
	/// <remarks>Register all initial targets before calling <see cref="Start"/> so the listener is ready to dispatch every method as soon as it begins reading messages.</remarks>
	public void AddRpcTarget<T>(T target, JsonRpcTargetOptions? options = null)
		where T : IShapeable<T> => this.AddRpcTarget(target, T.GetTypeShape(), options);
#endif

	/// <summary>
	/// Registers a local object's public instance methods as JSON-RPC targets, invoked when incoming requests match their JSON-RPC method names.
	/// </summary>
	/// <typeparam name="T">The type describing which members of <paramref name="target"/> to register.</typeparam>
	/// <param name="target">The object whose methods should be invoked in response to matching incoming requests and notifications.</param>
	/// <param name="shape">The type shape describing <paramref name="target"/>'s methods.</param>
	/// <param name="options">Options controlling method name resolution for this target. When <see langword="null"/>, default options are used.</param>
	/// <remarks>Register all initial targets before calling <see cref="Start"/> so the listener is ready to dispatch every method as soon as it begins reading messages.</remarks>
	public void AddRpcTarget<T>(T target, ITypeShape<T> shape, JsonRpcTargetOptions? options = null)
	{
		Requires.NotNull(shape);

		var registration = (TargetRegistration)shape.Accept(RpcTargetVisitor.Instance, options ?? JsonRpcTargetOptions.Default)!;
		lock (this.targetRegistrationSync)
		{
			if (this.disposed)
			{
				throw new ObjectDisposedException(nameof(JsonRpc));
			}

			foreach (string name in registration.MethodInvokers.Keys)
			{
				if (this.handlers.ContainsKey(name))
				{
					throw new InvalidOperationException($"The JSON-RPC method name '{name}' is already registered.");
				}
			}

			foreach ((string name, MethodInvoker invoker) in registration.MethodInvokers)
			{
				this.handlers[name] = (target, invoker);
			}

			foreach (IEventTargetRegistration eventRegistration in registration.Events)
			{
				this.eventSubscriptions.Add(eventRegistration.Subscribe(target, this));
			}
		}
	}

	/// <summary>
	/// Creates a one-shot builder for sending multiple JSON-RPC requests and notifications as one protocol payload.
	/// </summary>
	/// <returns>A batch builder associated with this JSON-RPC connection.</returns>
	public JsonRpcBatch CreateBatch() => new(this);

	/// <summary>
	/// Attaches a generated client proxy for an RPC contract interface to this JSON-RPC connection.
	/// </summary>
	/// <typeparam name="T">The RPC contract interface to proxy.</typeparam>
	/// <param name="options">Options controlling argument encoding for this proxy.</param>
	/// <returns>A generated proxy instance that implements <typeparamref name="T"/>.</returns>
	public T Attach<T>(JsonRpcProxyOptions? options = null) => AttachCore<T>(this, options);

	/// <summary>
	/// Attaches a generated client proxy for an RPC contract interface to this JSON-RPC connection.
	/// </summary>
	/// <param name="interfaceType">The RPC contract interface to proxy.</param>
	/// <param name="options">Options controlling argument encoding for this proxy.</param>
	/// <returns>A generated proxy instance that implements <paramref name="interfaceType"/>.</returns>
	/// <remarks>
	/// The generated proxy factory is looked up once per interface and cached.
	/// When the interface is known at compile time, <see cref="Attach{T}(JsonRpcProxyOptions?)"/> is slightly faster
	/// because it avoids the dictionary lookup.
	/// </remarks>
	public object Attach(Type interfaceType, JsonRpcProxyOptions? options = null) => AttachCore(this, interfaceType, options);

#if NET
	public ValueTask RequestAsync<TArg>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg> => this.RequestAsync(method, arguments, TArg.GetTypeShape(), cancellationToken);

	public ValueTask<TResult> RequestAsync<TArg, TResult>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg>
		where TResult : IShapeable<TResult>
		=> this.RequestAsync(method, arguments, TArg.GetTypeShape(), TResult.GetTypeShape(), cancellationToken);

	public ValueTask<TResult> RequestAsync<TArg, TResult, TResultProvider>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg>
		where TResultProvider : IShapeable<TResult>
		=> this.RequestAsync(method, arguments, TArg.GetTypeShape(), TResultProvider.GetTypeShape(), cancellationToken);

	public ValueTask NotifyAsync<TArg>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg> => this.NotifyAsync(method, arguments, TArg.GetTypeShape(), cancellationToken);
#endif

	public ValueTask<TResult> RequestAsync<TArg, TResult>(string method, in TArg arguments, ITypeShape<TArg> argShape, ITypeShape<TResult> resultShape, CancellationToken cancellationToken)
	{
		using RpcCallState.Lease callStateLease = new(RpcCallState.Rent());
		RpcCallState callState = callStateLease.State;
		using MarshaledObjectManager.HandleScope marshaledObjectsScope = this.marshaledObjects.TrackMarshaledObjects(callState);
		using ProgressManager.RegistrationScope progressScope = this.progress.TrackRegistrations(callState);
		using OutOfBandStreamManager.OutboundScope outOfBandStreamScope = this.outOfBandStreams.TrackOutboundRequest(callState);
		using AsyncEnumerableManager.OutboundScope asyncEnumerableScope = this.asyncEnumerables.TrackOutboundMessage(callState);
		JsonRpcValue serializedArguments = this.userDataSerializer.Serialize(arguments, argShape, callState, cancellationToken);
		JsonRpcRequest request = new()
		{
			Id = this.GetNextRequestId(),
			Method = method,
			Arguments = serializedArguments.WithMarshaledHandles(marshaledObjectsScope.Commit()).WithProgressRegistrations(progressScope.Commit()).WithOutOfBandChannels(outOfBandStreamScope.Commit()).WithAsyncEnumerableTokens(asyncEnumerableScope.Commit()),
		};

		ValueTask<JsonRpcResponse> responseTask = this.RequestAsync(request, cancellationToken);
		return this.AwaitTypedResponseAsync<TResult>(request, resultShape, responseTask, cancellationToken);
	}

	public ValueTask RequestAsync<TArg>(string method, in TArg arguments, ITypeShape<TArg> argShape, CancellationToken cancellationToken)
	{
		using RpcCallState.Lease callStateLease = new(RpcCallState.Rent());
		RpcCallState callState = callStateLease.State;
		using MarshaledObjectManager.HandleScope marshaledObjectsScope = this.marshaledObjects.TrackMarshaledObjects(callState);
		using ProgressManager.RegistrationScope progressScope = this.progress.TrackRegistrations(callState);
		using OutOfBandStreamManager.OutboundScope outOfBandStreamScope = this.outOfBandStreams.TrackOutboundRequest(callState);
		using AsyncEnumerableManager.OutboundScope asyncEnumerableScope = this.asyncEnumerables.TrackOutboundMessage(callState);
		JsonRpcValue serializedArguments = this.userDataSerializer.Serialize(arguments, argShape, callState, cancellationToken);
		JsonRpcRequest request = new()
		{
			Id = this.GetNextRequestId(),
			Method = method,
			Arguments = serializedArguments.WithMarshaledHandles(marshaledObjectsScope.Commit()).WithProgressRegistrations(progressScope.Commit()).WithOutOfBandChannels(outOfBandStreamScope.Commit()).WithAsyncEnumerableTokens(asyncEnumerableScope.Commit()),
		};

		ValueTask<JsonRpcResponse> responseTask = this.RequestAsync(request, cancellationToken);
		return this.AwaitVoidResponseAsync(request, responseTask);
	}

	public ValueTask NotifyAsync<TArg>(string method, in TArg arguments, ITypeShape<TArg> argShape, CancellationToken cancellationToken)
	{
		RpcCallState callState = new();
		MarshaledObjectManager.HandleScope marshaledObjectsScope = this.marshaledObjects.TrackMarshaledObjects(callState);
		using ProgressManager.RegistrationScope progressScope = this.progress.TrackRegistrations(callState);
		using AsyncEnumerableManager.OutboundScope asyncEnumerableScope = this.asyncEnumerables.TrackOutboundMessage(callState);
		try
		{
			JsonRpcValue serializedArguments = this.userDataSerializer.Serialize(arguments, argShape, callState, cancellationToken);
			if (marshaledObjectsScope.HasMarshaledObjects)
			{
				throw new InvalidOperationException("Marshaled objects cannot be sent in notifications because the sender cannot know whether the receiver accepted them.");
			}

			this.progress.EnsureNoProgressRegistrations(serializedArguments.WithProgressRegistrations(progressScope.Commit()));
			this.asyncEnumerables.EnsureNoAsyncEnumerables(serializedArguments.WithAsyncEnumerableTokens(asyncEnumerableScope.Commit()));

			JsonRpcRequest request = new()
			{
				Id = null,
				Method = method,
				Arguments = serializedArguments.WithMarshaledHandles(marshaledObjectsScope.Commit()).WithProgressRegistrations(progressScope.Commit()),
			};

			return this.NotifyAsync(request, marshaledObjectsScope, cancellationToken);
		}
		catch
		{
			marshaledObjectsScope.Dispose();
			throw;
		}
	}

	/// <inheritdoc/>
	public ValueTask RequestAsync(string method, JsonRpcValue arguments, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = this.GetNextRequestId(),
			Method = method,
			Arguments = arguments,
		};

		return this.AwaitVoidResponseAsync(request, this.RequestAsync(request, cancellationToken));
	}

	/// <inheritdoc/>
	public ValueTask<TResult> RequestAsync<TResult>(string method, JsonRpcValue arguments, ITypeShape<TResult> resultShape, CancellationToken cancellationToken)
	{
		Requires.NotNull(resultShape);

		JsonRpcRequest request = new()
		{
			Id = this.GetNextRequestId(),
			Method = method,
			Arguments = arguments,
		};

		return this.AwaitTypedResponseAsync(request, resultShape, this.RequestAsync(request, cancellationToken), cancellationToken);
	}

	/// <inheritdoc/>
	public ValueTask NotifyAsync(string method, JsonRpcValue arguments, CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			this.marshaledObjects.EnsureNoMarshaledObjects(arguments);
			this.progress.EnsureNoProgressRegistrations(arguments);
			this.asyncEnumerables.EnsureNoAsyncEnumerables(arguments);

			JsonRpcRequest request = new()
			{
				Id = null,
				Method = method,
				Arguments = arguments,
			};

			return this.AwaitPostedNotificationAsync(this.PostMessageAsync(request, cancellationToken), request);
		}
		catch
		{
			this.marshaledObjects.ReleaseLocalObjects(arguments);
			arguments.ReleaseIfSingleUse();
			throw;
		}
	}

	/// <summary>Starts listening for and dispatching incoming messages.</summary>
	/// <remarks>Call this after registering initial targets with <see cref="AddRpcTarget{T}(T, ITypeShape{T}, JsonRpcTargetOptions?)"/> to avoid rejecting incoming requests or dropping notifications for which no RPC target has yet been registered.</remarks>
	public void Start()
	{
		this.channel.Start();
		this.readerTask = this.ReadAsync(this.channel.Reader);
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		// Unsubscribe from target object events first so that none can be raised (and attempt to notify) during the rest of disposal.
		List<IDisposable> subscriptions;
		lock (this.targetRegistrationSync)
		{
			if (this.disposed)
			{
				return;
			}

			this.disposed = true;
			subscriptions = new List<IDisposable>(this.eventSubscriptions);
			this.eventSubscriptions.Clear();
		}

		foreach (IDisposable subscription in subscriptions)
		{
			subscription.Dispose();
		}

		this.StopReading();
		lock (this.connectionSync)
		{
			this.completionSource.TrySetCanceled();
			this.channel.Writer.TryComplete();
			foreach ((RequestId id, TaskCompletionSource<JsonRpcResponse> pending) in this.pendingOutboundRequests)
			{
				this.progress.UnregisterOutboundRequest(id);
				pending.TrySetException(new ObjectDisposedException(nameof(JsonRpc)));
			}

			this.pendingOutboundRequests.Clear();
		}

		this.marshaledObjects.DisposeAll();
		this.outOfBandStreams.Dispose();
		this.asyncEnumerables.Dispose();
	}

	/// <summary>Revokes every active marshaled relationship for an object owned by this connection.</summary>
	/// <param name="target">The previously marshaled target object.</param>
	/// <returns>The number of handles revoked.</returns>
	/// <remarks>
	/// This operation is object-wide: every handle issued for <paramref name="target"/> is revoked, while handles
	/// for other objects are unaffected. Revocation does not dispose <paramref name="target"/>; its owner remains
	/// responsible for its lifetime. Calls already dispatched remotely may complete.
	/// </remarks>
	public int RevokeMarshaledObject(object target)
	{
		Requires.NotNull(target);
		return this.marshaledObjects.Revoke(target);
	}

	/// <summary>
	/// Creates a generated client proxy for an RPC contract interface, using a factory that is discovered once per interface.
	/// </summary>
	/// <typeparam name="T">The RPC contract interface to proxy.</typeparam>
	/// <param name="client">The client the proxy sends requests through.</param>
	/// <param name="options">Options controlling the proxy's behavior.</param>
	/// <returns>The new proxy.</returns>
	internal static T AttachCore<T>(IJsonRpcClient client, JsonRpcProxyOptions? options)
	{
		Requires.NotNull(client);

		// When no factory is cached, defer to the non-generic path to throw the appropriate exception.
		return ProxyFactoryCache<T>.Registration is { } registration
			? CastProxy<T>(registration.CreateProxy(client, options ?? JsonRpcProxyOptions.Default))
			: (T)AttachCore(client, typeof(T), options);
	}

	/// <summary>
	/// Creates a generated client proxy for an RPC contract interface.
	/// </summary>
	/// <param name="client">The client the proxy sends requests through.</param>
	/// <param name="interfaceType">The RPC contract interface to proxy.</param>
	/// <param name="options">Options controlling the proxy's behavior.</param>
	/// <returns>The new proxy.</returns>
	internal static object AttachCore(IJsonRpcClient client, Type interfaceType, JsonRpcProxyOptions? options)
	{
		Requires.NotNull(client);
		Requires.NotNull(interfaceType);
		Requires.Argument(interfaceType.IsInterface, nameof(interfaceType), "The requested proxy type must be an interface.");

		ProxyFactoryRegistration registration = GetCachedProxyFactory(interfaceType)
			?? throw new NotSupportedException($"No generated JSON-RPC proxy was found for interface '{interfaceType.FullName}'. Add GenerateJsonRpcProxyAttribute to the interface or request an annotated composite interface.");
		object proxy = registration.CreateProxy(client, options ?? JsonRpcProxyOptions.Default);
		if (!interfaceType.IsInstanceOfType(proxy))
		{
			throw CreateProxyMismatchException(proxy, interfaceType);
		}

		return proxy;
	}

	/// <summary>
	/// Casts a newly created proxy to the interface it was expected to implement.
	/// </summary>
	/// <typeparam name="T">The RPC contract interface the proxy should implement.</typeparam>
	/// <param name="proxy">The proxy.</param>
	/// <returns>The proxy, typed as <typeparamref name="T"/>.</returns>
	/// <exception cref="InvalidOperationException">Thrown when <paramref name="proxy"/> does not implement <typeparamref name="T"/>.</exception>
	internal static T CastProxy<T>(object proxy) => proxy is T typed ? typed : throw CreateProxyMismatchException(proxy, typeof(T));

	/// <summary>Creates a proxy identified by legacy metadata emitted by an earlier source generator.</summary>
	/// <param name="proxyType">The generated proxy type.</param>
	/// <param name="client">The client the proxy sends requests through.</param>
	/// <param name="options">Options controlling the proxy's behavior.</param>
	/// <returns>The generated proxy instance.</returns>
	internal static object CreateLegacyProxy(
#if NET
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
#endif
		Type proxyType,
		IJsonRpcClient client,
		JsonRpcProxyOptions options)
	{
		ConstructorInfo? constructor = proxyType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, types: [typeof(IJsonRpcClient), typeof(JsonRpcProxyOptions)], modifiers: null);
		if (constructor is null)
		{
			throw new InvalidOperationException($"The generated proxy type '{proxyType.FullName}' does not have a constructor that accepts an IJsonRpcClient and JsonRpcProxyOptions instance.");
		}

		return constructor.Invoke([client, options]);
	}

	internal RequestId GetNextRequestId()
	{
		int id = Interlocked.Increment(ref this.nextRequestId);
		if (id <= 0)
		{
			throw new InvalidOperationException("The JSON-RPC request ID space is exhausted.");
		}

		return id;
	}

	internal void PostMarshaledNotification(string method, JsonRpcValue arguments) => this.PostMessage(new JsonRpcRequest { Method = method, Arguments = arguments });

	internal JsonRpcValue MarshalReleaseArguments(long handle, bool ownedBySender = false)
	{
		if (this.channel.Encoding == JsonRpcEncoding.Json)
		{
			using Sequence<byte> buffer = new();
			using Utf8JsonWriter writer = new(buffer);
			writer.WriteStartObject();
			writer.WriteNumber("handle", handle);
			writer.WriteBoolean("ownedBySender", ownedBySender);
			writer.WriteEndObject();
			writer.Flush();
			return JsonRpcValue.FromJson(buffer.AsReadOnlySequence);
		}

		using Sequence<byte> msgpackBuffer = new();
		MessagePackWriter msgpackWriter = new(msgpackBuffer);
		msgpackWriter.WriteMapHeader(2);
		msgpackWriter.Write("handle");
		msgpackWriter.Write(handle);
		msgpackWriter.Write("ownedBySender");
		msgpackWriter.Write(ownedBySender);
		msgpackWriter.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)msgpackBuffer.AsReadOnlySequence);
	}

	internal void LogApplicationError(Exception exception) => this.Logger.LogWarning(exception, "JSON-RPC request processing failed.");

	/// <summary>Stamps an outbound request with the ambient <see cref="JoinableTask"/> token, if any.</summary>
	/// <param name="request">A request that expects a response.</param>
	internal void ApplyJoinableTaskToken(JsonRpcRequest request)
	{
		string? token = this.JoinableTaskFactory is { } jtf ? jtf.Context.Capture() : this.JoinableTaskTracker.Token;
		if (token is not null)
		{
			request.JoinableTaskToken = token;
		}
	}

	internal bool TryRegisterOutboundRequest(JsonRpcRequest request, TaskCompletionSource<JsonRpcResponse> responseTcs)
	{
		Requires.Argument(request.Id.HasValue, nameof(request), "Request must have an ID for tracking the response.");
		lock (this.connectionSync)
		{
			if (this.Completion.IsCompleted || this.IsDisposed)
			{
				throw new InvalidOperationException("The JSON-RPC connection is closed.");
			}

			if (this.pendingOutboundRequests.ContainsKey(request.Id.Value))
			{
				return false;
			}

			this.pendingOutboundRequests.Add(request.Id.Value, responseTcs);
			return true;
		}
	}

	internal bool TryUnregisterOutboundRequest(RequestId id)
	{
		lock (this.connectionSync)
		{
			return this.pendingOutboundRequests.Remove(id);
		}
	}

	internal void CancelOutboundRequest(JsonRpcRequest request)
	{
		this.PostMessage(this.CreateCancellationNotification(request, CancellationToken.None));
	}

	internal JsonRpcRequest CreateCancellationNotification(JsonRpcRequest request, CancellationToken cancellationToken)
	{
		Requires.Argument(request.Id.HasValue, nameof(request), "Request must have an ID for cancellation.");
		return new JsonRpcRequest
		{
			Method = SpecialCancelMethodName,
			Arguments = this.channel.Serializer.SerializeCancellation(request.Id.Value, cancellationToken),
		};
	}

	internal ValueTask PostMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
	{
		this.channel.Serializer.ValidateMessage(message);
		return this.channel.Writer.WriteAsync(message, cancellationToken);
	}

	internal void PostMessage(JsonRpcMessage message) => this.FaultOnFailure(this.PostMessageAsync(message).AsTask());

	internal ValueTask<JsonRpcResponse> AwaitResponseAsync(JsonRpcRequest request, TaskCompletionSource<JsonRpcResponse> responseTcs, CancellationToken cancellationToken)
	{
		return HelperAsync();

		async ValueTask<JsonRpcResponse> HelperAsync()
		{
			using (cancellationToken.Register(this.cancelOutboundRequestDelegate, request))
			{
#pragma warning disable VSTHRD003 // Awaiting a TaskCompletionSource that represents the remote response.
				JsonRpcResponse response = await responseTcs.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
				return response;
			}
		}
	}

	internal async ValueTask AwaitVoidResponseAsync(JsonRpcRequest request, ValueTask<JsonRpcResponse> responseTask)
	{
		try
		{
			JsonRpcResponse response = await responseTask.ConfigureAwait(false);
			switch (response)
			{
				case JsonRpcResult result:
					result.Result.Release();
					return;
				case JsonRpcError error:
					this.marshaledObjects.ReleaseLocalObjects(request.Arguments);
					throw new JsonRpcException(error.Error);
				default:
					throw new InvalidOperationException("Received an unknown response type.");
			}
		}
		finally
		{
			this.marshaledObjects.ReleaseCallScopedObjects(request.Arguments);
		}
	}

	internal async ValueTask<TResult> AwaitTypedResponseAsync<TResult>(JsonRpcRequest request, ITypeShape<TResult> resultShape, ValueTask<JsonRpcResponse> responseTask, CancellationToken cancellationToken)
	{
		CallScopedLifetime? argumentLifetime = request.Arguments.MarshaledHandles is { HasCallScopedObjects: true }
			? this.CreateArgumentLifetime(request.Arguments)
			: null;
		try
		{
			JsonRpcResponse response = await responseTask.ConfigureAwait(false);
			switch (response)
			{
				case JsonRpcResult result:
					{
						using RpcCallState.Lease callStateLease = new(RpcCallState.Rent());
						RpcCallState callState = callStateLease.State;
						using OutOfBandStreamManager.InboundScope outOfBandStreamScope = this.outOfBandStreams.TrackInboundRequest(hasResponse: true, callState);
						using AsyncEnumerableManager.InboundScope asyncEnumerableScope = this.asyncEnumerables.TrackInboundRequest(hasResponse: true, callState);
						TResult returnValue;
						try
						{
							returnValue = this.userDataSerializer.Deserialize(result.Result, resultShape, callState, cancellationToken)!;
						}
						finally
						{
							// The result has been fully materialized (or failed to be), and this is its only consumer.
							result.Result.Release();
						}

						outOfBandStreamScope.Complete(successful: true);
						asyncEnumerableScope.RetainCallScopedArguments(argumentLifetime);
						return returnValue;
					}

				case JsonRpcError error:
					this.marshaledObjects.ReleaseLocalObjects(request.Arguments);
					throw new JsonRpcException(error.Error);
				default:
					throw new InvalidOperationException("Received an unknown response type.");
			}
		}
		finally
		{
			argumentLifetime?.Dispose();
		}
	}

	/// <summary>Sends a request and returns the raw encoded result without deserializing it.</summary>
	/// <param name="method">The remote method name.</param>
	/// <param name="arguments">The pre-encoded arguments.</param>
	/// <param name="cancellationToken">A token to cancel the request.</param>
	/// <returns>The raw encoded result.</returns>
	internal async ValueTask<JsonRpcValue> RequestRawAsync(string method, JsonRpcValue arguments, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = this.GetNextRequestId(),
			Method = method,
			Arguments = arguments,
		};

		JsonRpcResponse response = await this.RequestAsync(request, cancellationToken).ConfigureAwait(false);
		return response switch
		{
			JsonRpcResult result => result.Result,
			JsonRpcError error => throw new JsonRpcException(error.Error),
			_ => throw new InvalidOperationException("Received an unknown response type."),
		};
	}

	/// <summary>Creates a lifetime that releases the call-scoped objects marshaled into a request's arguments.</summary>
	/// <param name="arguments">The request's arguments.</param>
	/// <returns>The lifetime.</returns>
	/// <remarks>This is a separate method so that callers only allocate the lambda's closure when they need it.</remarks>
	internal CallScopedLifetime CreateArgumentLifetime(JsonRpcValue arguments) => new(() => this.marshaledObjects.ReleaseCallScopedObjects(arguments));

	internal JsonRpcValue SerializeMarshaledResult<T>(T value, ITypeShape<T> shape, RpcCallState? inboundCallState, CancellationToken cancellationToken)
	{
		using RpcCallState.Lease callStateLease = new(RpcCallState.Rent());
		RpcCallState callState = callStateLease.State;
		using MarshaledObjectManager.HandleScope marshaledObjectsScope = this.marshaledObjects.TrackMarshaledObjects(callState, allowCallScopedLifetime: false);
		using OutOfBandStreamManager.OutboundScope outOfBandStreamScope = this.outOfBandStreams.TrackOutboundRequest(callState);
		using AsyncEnumerableManager.OutboundScope asyncEnumerableScope = this.asyncEnumerables.TrackOutboundMessage(callState, inboundCallState?.InboundCall?.Lifetime);
		JsonRpcValue serialized = this.userDataSerializer.Serialize(value, shape, callState, cancellationToken);
		OutOfBandStreamManager.ChannelSet outOfBandChannels = outOfBandStreamScope.Commit();
		this.outOfBandStreams.TrackActiveChannels(outOfBandChannels);
		return serialized.WithMarshaledHandles(marshaledObjectsScope.Commit()).WithOutOfBandChannels(outOfBandChannels).WithAsyncEnumerableTokens(asyncEnumerableScope.Commit());
	}

	private static void ReleaseReceivedArguments(JsonRpcRequest request)
	{
		request.SplitArguments.Return();
		request.SplitArguments = default;
		request.Arguments.Release();
	}

	/// <summary>Invokes a request handler as a child of the caller's joinable task.</summary>
	/// <param name="jtf">The joinable task factory.</param>
	/// <param name="invoker">The request handler.</param>
	/// <param name="dispatchRequest">The request to dispatch.</param>
	/// <param name="parentToken">The caller's joinable task token.</param>
	/// <returns>The handler's response.</returns>
	/// <remarks>This is a separate method so that only requests that carry a token allocate the lambda's closure.</remarks>
	private static JoinableTask<DispatchResponse> RunUnderJoinableTaskAsync(JoinableTaskFactory jtf, MethodInvoker invoker, DispatchRequest dispatchRequest, string parentToken)
		=> jtf.RunAsync(() => invoker(dispatchRequest).AsTask(), parentToken, JoinableTaskCreationOptions.None);

	/// <summary>
	/// Finds the source-generated factory for an RPC contract interface's proxy.
	/// </summary>
	/// <param name="interfaceType">The RPC contract interface.</param>
	/// <returns>The factory, or <see langword="null"/> if <paramref name="interfaceType"/> is not an interface with a generated proxy.</returns>
	private static ProxyFactoryRegistration? FindProxyFactory(Type interfaceType)
	{
		if (!interfaceType.IsInterface)
		{
			return null;
		}

		if (interfaceType.GetCustomAttribute<JsonRpcProxyFactoryAttribute>(inherit: false) is { } factory)
		{
			return new(factory, legacyProxyType: null);
		}

#pragma warning disable CS0618 // Support proxy metadata emitted by previous versions of the source generator.
		return interfaceType.GetCustomAttribute<JsonRpcProxyImplementationAttribute>(inherit: false) is { } legacyFactory
			? new(factory: null, legacyFactory.ProxyType)
			: null;
#pragma warning restore CS0618
	}

	/// <summary>
	/// Gets the generated factory or legacy proxy type for an RPC contract interface, searching only on first request.
	/// </summary>
	/// <param name="interfaceType">The RPC contract interface.</param>
	/// <returns>The proxy registration, or <see langword="null"/> if <paramref name="interfaceType"/> has no generated proxy.</returns>
	private static ProxyFactoryRegistration? GetCachedProxyFactory(Type interfaceType)
	{
		ConcurrentDictionary<Type, ProxyFactoryRegistration?> cache = Volatile.Read(ref proxyFactoriesByType)
			?? Interlocked.CompareExchange(ref proxyFactoriesByType, new(), null)
			?? proxyFactoriesByType!;
		return cache.GetOrAdd(interfaceType, FindProxyFactory);
	}

	private static InvalidOperationException CreateProxyMismatchException(object? proxy, Type interfaceType)
		=> new($"The generated proxy factory returned {(proxy is null ? "null" : $"an instance of '{proxy.GetType().FullName}'")} which does not implement requested interface '{interfaceType.FullName}'.");

	/// <summary>Gets the <see cref="System.Threading.SynchronizationContext"/> to dispatch a particular request on.</summary>
	/// <param name="request">The inbound request.</param>
	/// <returns>The context to begin the invocation on.</returns>
	/// <remarks>
	/// <para>
	/// <c>$/cancelRequest</c> notifications always go to the thread pool rather than to <see cref="SynchronizationContext"/>.
	/// Their entire purpose is to interrupt work that is already running, so queueing them behind that work would be
	/// self-defeating: a handler that occupies the ordered dispatcher without yielding could never be canceled.
	/// </para>
	/// <para>
	/// They are not run on the reader loop itself because cancellation invokes arbitrary user callbacks,
	/// which must not be given the opportunity to stall message processing.
	/// </para>
	/// </remarks>
	private SynchronizationContext GetDispatchSynchronizationContext(JsonRpcRequest request)
		=> request.Method == SpecialCancelMethodName ? UnorderedDispatchSynchronizationContext : this.DispatchSynchronizationContext;

	private ValueTask<JsonRpcResponse?> DispatchAsync(JsonRpcRequest request)
	{
		if (request.Id is null && (this.progress.TryHandleNotification(request) || this.marshaledObjects.TryHandleNotification(request)))
		{
			ReleaseReceivedArguments(request);
			return new ValueTask<JsonRpcResponse?>((JsonRpcResponse?)null);
		}

		(object? Target, MethodInvoker Invoker) handler;
		if (this.asyncEnumerables.TryGetMethodInvoker(request, out MethodInvoker enumerableInvoker))
		{
			handler = (null, enumerableInvoker);
		}
		else if (this.marshaledObjects.TryGetMethodInvoker(request, out object? marshaledTarget, out MethodInvoker? marshaledInvoker))
		{
			handler = (marshaledTarget, marshaledInvoker);
		}
		else if (!this.handlers.TryGetValue(request.Method, out handler))
		{
			ReleaseReceivedArguments(request);
			bool missingMarshaledObject = this.marshaledObjects.IsMissingHandleInvocation(request, out long missingHandle);
			return new ValueTask<JsonRpcResponse?>(request.Id is RequestId missingId
				? new JsonRpcError
				{
					Id = missingId,
					Error = new()
					{
						Code = missingMarshaledObject ? JsonRpcErrorCode.NoMarshaledObjectFound : JsonRpcErrorCode.MethodNotFound,
						Message = missingMarshaledObject ? $"No marshaled object with handle {missingHandle} exists." : $"The method {request.Method} is not supported.",
					},
				}
				: null);
		}

		try
		{
			PendingInboundRequest tracker;

			if (request.Id is RequestId id)
			{
				tracker = new()
				{
					CancellationTokenSource = new(),
				};

				bool added;
				lock (this.pendingInboundRequests)
				{
					added = !this.pendingInboundRequests.ContainsKey(id);
					if (added)
					{
						this.pendingInboundRequests.Add(id, tracker);
					}
				}

				if (!added)
				{
					tracker.Dispose();
					throw new ProtocolViolationException($"A request with ID {id} is already pending.");
				}
			}
			else
			{
				tracker = default;
			}

			RpcCallState callState = new();
			DispatchRequest dispatchRequest = new()
			{
				JsonRpc = this,
				Request = request,
				TargetInstance = handler.Target,
				CallState = callState,
				CancellationToken = tracker.CancellationTokenSource?.Token ?? default,
			};

			return this.DispatchCoreAsync(request, handler.Invoker, dispatchRequest);
		}
		catch (Exception ex)
		{
			ReleaseReceivedArguments(request);
			this.Fault(ex);
			return new ValueTask<JsonRpcResponse?>(Task.FromException<JsonRpcResponse?>(ex));
		}
	}

	// Pooled because this is only ever awaited once, by ProcessRequestAsync or a batch.
#if NET
	[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
	private async ValueTask<JsonRpcResponse?> DispatchCoreAsync(JsonRpcRequest request, MethodInvoker invoker, DispatchRequest dispatchRequest)
	{
		try
		{
			using MarshaledObjectManager.InboundCallScope inboundCallScope = this.marshaledObjects.TrackInboundCall(request.Id.HasValue, dispatchRequest.CallState);
			using ProgressManager.InboundScope progressScope = this.progress.TrackInboundCall(request.Id.HasValue, dispatchRequest.CallState);
			using OutOfBandStreamManager.InboundScope outOfBandStreamScope = this.outOfBandStreams.TrackInboundRequest(request.Id.HasValue, dispatchRequest.CallState);
			using AsyncEnumerableManager.InboundScope asyncEnumerableScope = this.asyncEnumerables.TrackInboundRequest(request.Id.HasValue, dispatchRequest.CallState);

			// Changes to the ambient tracker made here are scoped to this async method's execution context.
			string? parentToken = request.JoinableTaskToken;
			JoinableTaskFactory? jtf = this.JoinableTaskFactory;
			if (jtf is null)
			{
				this.JoinableTaskTracker.Token = parentToken;
			}

			// IMPORTANT: This must remain the first await in this method, with no other await between it and
			// invoking the handler. It is what guarantees that handlers *begin* executing in the order the
			// remote party sent the requests (per the SynchronizationContext property).
			// Awaiting a SynchronizationContext does not allocate a delegate or closure of its own: the awaiter
			// posts the state machine's existing continuation using a cached, static SendOrPostCallback.
			await this.GetDispatchSynchronizationContext(request);

			DispatchResponse response = jtf is not null && parentToken is not null
				? await RunUnderJoinableTaskAsync(jtf, invoker, dispatchRequest, parentToken)
				: await invoker(dispatchRequest).ConfigureAwait(false);
			await progressScope.CompleteAsync().ConfigureAwait(false);
			Assumes.True(request.Id is null == response.Response is null, "A response is expected iff the request included an ID.");
			inboundCallScope.Complete(response.Response is not JsonRpcError);
			outOfBandStreamScope.Complete(response.Response is not JsonRpcError);
			return response.Response;
		}
		finally
		{
			ReleaseReceivedArguments(request);
			if (request.Id is RequestId id)
			{
				PendingInboundRequest tracker;
				bool removed;
				lock (this.pendingInboundRequests)
				{
					removed = this.pendingInboundRequests.TryGetValue(id, out tracker) && this.pendingInboundRequests.Remove(id);
				}

				if (removed)
				{
					tracker.Dispose();
				}
			}
		}
	}

	private void FaultOnFailure(Task task) => task.ContinueWith(static (t, s) => ((JsonRpc)s!).Fault(t.Exception!), this, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default).Forget();

	private void ProcessResponse(JsonRpcResponse response)
	{
		ProtocolViolationException? unmatchedResponseException = null;
		bool transferredToCaller = false;
		lock (this.connectionSync)
		{
			if (!this.Completion.IsCompleted && this.pendingOutboundRequests.TryGetValue(response.Id, out TaskCompletionSource<JsonRpcResponse>? tcs))
			{
				this.pendingOutboundRequests.Remove(response.Id);
				this.progress.UnregisterOutboundRequest(response.Id);
				this.outOfBandStreams.CompleteOutboundRequest(response.Id, successful: response is JsonRpcResult);
				this.asyncEnumerables.CompleteOutboundRequest(response.Id);
				transferredToCaller = tcs.TrySetResult(response);
			}
			else if (!this.Completion.IsCompleted)
			{
				unmatchedResponseException = new ProtocolViolationException($"Received a response with ID {response.Id} that does not match any pending requests.");
			}
		}

		if (!transferredToCaller && response is JsonRpcResult discardedResult)
		{
			discardedResult.Result.Release();
		}

		if (unmatchedResponseException is not null)
		{
			this.Fault(unmatchedResponseException);
		}
	}

	private void ProcessIncomingMessage(JsonRpcMessage message)
	{
		switch (message)
		{
			case JsonRpcRequest request:
				this.ProcessRequestAsync(request).Forget();
				break;
			case JsonRpcResponse response:
				this.ProcessResponse(response);
				break;
			case JsonRpcMessageBatch batch:
				this.ProcessBatch(batch.Messages);
				break;
			case JsonRpcInvalidMessage invalid:
				this.Fault(new ProtocolViolationException(invalid.Message));
				break;
		}
	}

	private void ProcessBatch(System.Collections.Immutable.ImmutableArray<JsonRpcMessage> messages)
	{
		if (messages is [])
		{
			this.Fault(new ProtocolViolationException("A JSON-RPC batch must not be empty."));
			return;
		}

		foreach (JsonRpcMessage message in messages)
		{
			if (message is JsonRpcMessageBatch)
			{
				this.Fault(new ProtocolViolationException("A JSON-RPC batch entry must be a message object."));
				return;
			}
		}

		if (messages.Any(static m => m is JsonRpcInvalidMessage))
		{
			this.Fault(new ProtocolViolationException("A JSON-RPC batch contains an invalid entry."));
			return;
		}

		List<Task<JsonRpcResponse?>> requestTasks = [];

		foreach (JsonRpcMessage message in messages)
		{
			switch (message)
			{
				case JsonRpcRequest request:
					requestTasks.Add(this.DispatchAsync(request).AsTask());
					break;
				case JsonRpcResponse response:
					this.ProcessResponse(response);
					break;
			}
		}

		if (requestTasks.Count > 0)
		{
			this.FaultOnFailure(this.SendBatchResponsesAsync(requestTasks));
		}
	}

	private async Task SendBatchResponsesAsync(List<Task<JsonRpcResponse?>> requestTasks)
	{
		JsonRpcResponse?[] dispatchedResponses = requestTasks.Count > 0 ? await Task.WhenAll(requestTasks).ConfigureAwait(false) : [];
		List<JsonRpcMessage> responses = [];
		foreach (JsonRpcResponse? response in dispatchedResponses)
		{
			if (response is not null)
			{
				responses.Add(response);
			}
		}

		if (responses.Count > 0)
		{
			await this.PostMessageAsync(new JsonRpcMessageBatch([.. responses])).ConfigureAwait(false);
		}
	}

	/// <summary>Dispatches a request and sends its response, faulting the connection if either fails.</summary>
	/// <param name="request">The request to process.</param>
	/// <returns>A task that never faults.</returns>
	private async Task ProcessRequestAsync(JsonRpcRequest request)
	{
		try
		{
			JsonRpcResponse? response = await this.DispatchAsync(request).ConfigureAwait(false);
			if (response is not null)
			{
				await this.PostMessageAsync(response).ConfigureAwait(false);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Canceled work never faulted the connection, and faults are wrapped for parity with those observed from other tasks.
			this.Fault(new AggregateException(ex));
		}
	}

	// Pooled because this is only ever awaited once, by the typed response helpers.
#if NET
	[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
	private async ValueTask<JsonRpcResponse> RequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
	{
		TaskCompletionSource<JsonRpcResponse>? responseTcs = null;
		bool posted = false;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requires.Argument(request.Id.HasValue, nameof(request), "Request must have an ID for tracking the response.");
			Verify.Operation(this.State == JsonRpcState.Running, $"This instance is not listening for messages. Current state is {this.State}.");

			responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Verify.Operation(this.TryRegisterOutboundRequest(request, responseTcs), "A request with this ID is already pending.");
			this.progress.RegisterOutboundRequest(request);
			this.outOfBandStreams.RegisterOutboundRequest(request);
			this.asyncEnumerables.RegisterOutboundRequest(request);
			this.ApplyJoinableTaskToken(request);
			await this.PostMessageAsync(request, cancellationToken).ConfigureAwait(false);
			posted = true;

			using (cancellationToken.Register(this.cancelOutboundRequestDelegate, request))
			{
#pragma warning disable VSTHRD003 // Awaiting a TaskCompletionSource that represents the remote response.
				return await responseTcs.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
		}
		catch (Exception ex) when (!posted)
		{
			request.Arguments.ReleaseIfSingleUse();
			if (request.Id.HasValue)
			{
				this.TryUnregisterOutboundRequest(request.Id.Value);
				this.progress.UnregisterOutboundRequest(request);
				this.outOfBandStreams.CompleteOutboundRequest(request.Id.Value, successful: false);
				this.asyncEnumerables.CompleteOutboundRequest(request.Id.Value);
			}

			this.marshaledObjects.ReleaseLocalObjects(request.Arguments);
			this.asyncEnumerables.ReleaseGenerators(request.Arguments);
			responseTcs?.TrySetException(ex);
			throw;
		}
	}

	private async ValueTask NotifyAsync(JsonRpcRequest request, MarshaledObjectManager.HandleScope marshaledObjectsScope, CancellationToken cancellationToken)
	{
		using (marshaledObjectsScope)
		{
			try
			{
				await this.PostMessageAsync(request, cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				this.marshaledObjects.ReleaseLocalObjects(request.Arguments);
				request.Arguments.ReleaseIfSingleUse();
				throw;
			}
		}
	}

	private async ValueTask AwaitPostedNotificationAsync(ValueTask postTask, JsonRpcRequest request)
	{
		try
		{
			await postTask.ConfigureAwait(false);
		}
		catch
		{
			this.marshaledObjects.ReleaseLocalObjects(request.Arguments);
			request.Arguments.ReleaseIfSingleUse();
			throw;
		}
	}

	private void CancelOutboundRequest(object? state)
	{
		JsonRpcRequest request = (JsonRpcRequest)state!;
		this.CancelOutboundRequest(request);
	}

	private async Task ReadAsync(ChannelReader<JsonRpcMessage> inbound)
	{
		try
		{
			// Waiting without a token lets the channel reuse its cached wait operation; StopReading completes the channel instead.
			while (await inbound.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
			{
				this.DisposalToken.ThrowIfCancellationRequested();
				while (inbound.TryRead(out JsonRpcMessage? message))
				{
					this.ProcessIncomingMessage(message);
					if (this.Completion.IsFaulted)
					{
						return;
					}
				}
			}

			this.Fault(new EndOfStreamException("The JSON-RPC connection closed."));
		}
		catch (OperationCanceledException) when (this.IsDisposed)
		{
		}
		catch (Exception ex)
		{
			this.Fault(ex);
		}
	}

	private void Fault(Exception exception)
	{
		lock (this.connectionSync)
		{
			if (!this.completionSource.TrySetException(exception))
			{
				return;
			}

			foreach ((RequestId id, TaskCompletionSource<JsonRpcResponse> pending) in this.pendingOutboundRequests)
			{
				this.progress.UnregisterOutboundRequest(id);
				pending.TrySetException(exception);
			}

			this.pendingOutboundRequests.Clear();

			this.channel.Writer.TryComplete(exception);
			this.Logger.LogError(exception, "JSON-RPC connection terminated: {Reason}", exception.Message);
		}

		try
		{
			this.marshaledObjects.DisposeAll();
			this.outOfBandStreams.Dispose();
			this.asyncEnumerables.Dispose();
		}
		catch (Exception ex)
		{
			this.Logger.LogError(ex, "One or more marshaled objects failed to dispose while faulting the JSON-RPC connection.");
		}

		this.StopReading();
	}

	/// <summary>Cancels <see cref="DisposalToken"/> and wakes the read loop, which waits without a token.</summary>
	private void StopReading()
	{
		this.disposalSource.Cancel();
		this.channel.AbortInbound(this.disposalSource.Token);
	}

	[GenerateShape]
	internal partial struct CancelRequestParams
	{
		public CancelRequestParams(RequestId id) => this.Id = id;

		[Key(0)]
		[PropertyShape(IsRequired = true)]
		public RequestId Id { get; init; }
	}

	private struct PendingInboundRequest : IDisposable
	{
		internal required CancellationTokenSource? CancellationTokenSource { get; init; }

		public void Dispose()
		{
			this.CancellationTokenSource?.Dispose();
		}
	}

	[GenerateShape(Kind = TypeShapeKind.None, IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class SpecialMethodsTarget(JsonRpc owner)
	{
		[MethodShape(Name = SpecialCancelMethodName)]
		public void CancelRequest(RequestId id)
		{
			PendingInboundRequest tracker;
			bool found;
			lock (owner.pendingInboundRequests)
			{
				found = owner.pendingInboundRequests.TryGetValue(id, out tracker);
			}

			// Cancel outside the lock, since cancellation runs arbitrary callbacks.
			if (found)
			{
				tracker.CancellationTokenSource?.Cancel();
			}
		}
	}

	internal sealed class ProxyFactoryRegistration
	{
		/// <summary>Initializes a new instance of the <see cref="ProxyFactoryRegistration"/> class.</summary>
		/// <param name="factory">The source-generated factory, if available.</param>
		/// <param name="legacyProxyType">The proxy type from legacy metadata, if available.</param>
		internal ProxyFactoryRegistration(
			JsonRpcProxyFactoryAttribute? factory,
#if NET
			[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
#endif
			Type? legacyProxyType)
		{
			this.Factory = factory;
			this.LegacyProxyType = legacyProxyType;
		}

		/// <summary>Gets the source-generated factory, if the proxy was emitted by the current generator.</summary>
		internal JsonRpcProxyFactoryAttribute? Factory { get; }

		/// <summary>Gets the legacy proxy type, if the proxy was emitted by an earlier generator.</summary>
#if NET
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
#endif
		internal Type? LegacyProxyType { get; }

		/// <summary>Creates a proxy using either its generated factory or legacy constructor metadata.</summary>
		/// <param name="client">The client the proxy sends requests through.</param>
		/// <param name="options">Options controlling the proxy's behavior.</param>
		/// <returns>The generated proxy instance.</returns>
		internal object CreateProxy(IJsonRpcClient client, JsonRpcProxyOptions options)
			=> this.Factory is not null
				? this.Factory.CreateProxy(client, options)
				: CreateLegacyProxy(this.LegacyProxyType!, client, options);
	}

	/// <summary>
	/// Caches the source-generated proxy factory for an RPC contract interface.
	/// </summary>
	/// <typeparam name="T">The RPC contract interface.</typeparam>
	/// <remarks>
	/// The runtime initializes this type's field once per <typeparamref name="T"/>, thread-safely,
	/// so attaching a proxy needs only a field read instead of a reflection lookup.
	/// The initializer never throws, so a failed lookup isn't cached as a permanent <see cref="TypeInitializationException"/>.
	/// </remarks>
	private static class ProxyFactoryCache<T>
	{
		/// <summary>The factory registration, or <see langword="null"/> if <typeparamref name="T"/> has no generated proxy.</summary>
		internal static readonly ProxyFactoryRegistration? Registration = FindProxyFactory(typeof(T));
	}
}
