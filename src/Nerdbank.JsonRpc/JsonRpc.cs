// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

[TypeShape(Kind = TypeShapeKind.None)]
public partial class JsonRpc : IDisposableObservable, IJsonRpcClient, IArgumentsBuilderContext
{
	internal const string SpecialCancelMethodName = "$/cancelRequest";

	private readonly ConcurrentDictionary<RequestId, PendingInboundRequest> pendingInboundRequests = [];
	private readonly MarshaledObjectManager marshaledObjects;
	private readonly TaskCompletionSource<bool> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly object connectionSync = new();
	private readonly CancellationTokenSource disposalSource = new();
	private readonly ConcurrentDictionary<string, (object? Target, MethodInvoker Invoker)> handlers = new();
	private readonly ConcurrentDictionary<RequestId, TaskCompletionSource<JsonRpcResponse>> pendingOutboundRequests = new();
	private readonly Action<object?> cancelOutboundRequestDelegate;
	private readonly JsonRpcPipeChannel channel;
	private readonly JsonRpcSerializer userDataSerializer;
	private ILogger logger = NullLogger.Instance;
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
		this.userDataSerializer = serializer.WithMarshaledObjectManager(this.marshaledObjects);

		// Store a delegate we can reuse to avoid allocations.
		this.cancelOutboundRequestDelegate = this.CancelOutboundRequest;

		this.AddRpcTarget(new SpecialMethodsTarget(this));
	}

	/// <summary>Gets the logger for request and connection failures. Defaults to <see cref="NullLogger.Instance"/>.</summary>
	public ILogger Logger
	{
		get => this.logger;
		init => this.logger = value ?? throw new ArgumentNullException(nameof(value));
	}

	/// <summary>
	/// Gets or sets the <see cref="Microsoft.VisualStudio.Threading.JoinableTaskFactory"/> to participate in to mitigate deadlocks with the main thread.
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
	/// This property may only be set before <see cref="Start"/> is called.
	/// </para>
	/// </remarks>
	/// <exception cref="InvalidOperationException">Thrown when setting this property after <see cref="Start"/> has been called.</exception>
	public JoinableTaskFactory? JoinableTaskFactory
	{
		get => field;
		set
		{
			this.ThrowIfStarted();
			field = value;
		}
	}

	/// <summary>
	/// Gets or sets the <see cref="JoinableTaskTokenTracker"/> used to forward <see cref="JoinableTask"/> tokens
	/// from inbound requests to outbound requests when <see cref="JoinableTaskFactory"/> is <see langword="null"/>.
	/// </summary>
	/// <value>Defaults to an instance shared with all other <see cref="JsonRpc"/> instances that do not set this property.</value>
	/// <remarks>
	/// <para>This property is ignored when <see cref="JoinableTaskFactory"/> is set.</para>
	/// <para>
	/// Set this only in advanced scenarios where one process has many <see cref="JsonRpc"/> instances connected to different
	/// remote parties and correlating tokens across them is undesirable.
	/// This property may only be set before <see cref="Start"/> is called.
	/// </para>
	/// </remarks>
	/// <exception cref="InvalidOperationException">Thrown when setting this property after <see cref="Start"/> has been called.</exception>
	public JoinableTaskTokenTracker JoinableTaskTracker
	{
		get => field ??= JoinableTaskTokenTracker.Default;
		set
		{
			Requires.NotNull(value);
			this.ThrowIfStarted();
			field = value;
		}
	}

	JsonRpcSerializer IJsonRpcClient.Serializer => this.userDataSerializer;

	JsonRpcSerializer IArgumentsBuilderContext.Serializer => this.userDataSerializer;

	MarshaledObjectManager IArgumentsBuilderContext.MarshaledObjects => this.marshaledObjects;

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

	/// <inheritdoc/>
	public JsonRpcArgumentsBuilder CreateArguments(bool named, int count, CancellationToken cancellationToken = default) => new(this, named, count, cancellationToken);

#if NET
	public void AddRpcTarget<T>(T target)
		where T : IShapeable<T> => this.AddRpcTarget(target, T.GetTypeShape());
#endif

	public void AddRpcTarget<T>(T target, ITypeShape<T> shape)
	{
		Requires.NotNull(shape);

		var invokers = (Dictionary<string, MethodInvoker>)shape.Accept(RpcTargetVisitor.Instance)!;
		foreach ((string name, MethodInvoker invoker) in invokers)
		{
			this.handlers.TryAdd(name, (target, invoker));
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
	public T Attach<T>(JsonRpcProxyOptions? options = null) => (T)this.Attach(typeof(T), options);

	/// <summary>
	/// Attaches a generated client proxy for an RPC contract interface to this JSON-RPC connection.
	/// </summary>
	/// <param name="interfaceType">The RPC contract interface to proxy.</param>
	/// <param name="options">Options controlling argument encoding for this proxy.</param>
	/// <returns>A generated proxy instance that implements <paramref name="interfaceType"/>.</returns>
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
		using MarshaledObjectManager.HandleScope marshaledObjectsScope = this.marshaledObjects.TrackMarshaledObjects();
		JsonRpcValue serializedArguments = this.userDataSerializer.Serialize(arguments, argShape, cancellationToken);
		JsonRpcRequest request = new()
		{
			Id = this.GetNextRequestId(),
			Method = method,
			Arguments = serializedArguments.WithMarshaledHandles(marshaledObjectsScope.Commit()),
		};

		ValueTask<JsonRpcResponse> responseTask = this.RequestAsync(request, cancellationToken);
		return this.AwaitTypedResponseAsync<TResult>(request, resultShape, responseTask, cancellationToken);
	}

	public ValueTask RequestAsync<TArg>(string method, in TArg arguments, ITypeShape<TArg> argShape, CancellationToken cancellationToken)
	{
		using MarshaledObjectManager.HandleScope marshaledObjectsScope = this.marshaledObjects.TrackMarshaledObjects();
		JsonRpcValue serializedArguments = this.userDataSerializer.Serialize(arguments, argShape, cancellationToken);
		JsonRpcRequest request = new()
		{
			Id = this.GetNextRequestId(),
			Method = method,
			Arguments = serializedArguments.WithMarshaledHandles(marshaledObjectsScope.Commit()),
		};

		ValueTask<JsonRpcResponse> responseTask = this.RequestAsync(request, cancellationToken);
		return this.AwaitVoidResponseAsync(responseTask);
	}

	public ValueTask NotifyAsync<TArg>(string method, in TArg arguments, ITypeShape<TArg> argShape, CancellationToken cancellationToken)
	{
		MarshaledObjectManager.HandleScope marshaledObjectsScope = this.marshaledObjects.TrackMarshaledObjects();
		try
		{
			JsonRpcValue serializedArguments = this.userDataSerializer.Serialize(arguments, argShape, cancellationToken);
			JsonRpcRequest request = new()
			{
				Id = null,
				Method = method,
				Arguments = serializedArguments.WithMarshaledHandles(marshaledObjectsScope.Commit()),
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

		return this.AwaitVoidResponseAsync(this.RequestAsync(request, cancellationToken));
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
		cancellationToken.ThrowIfCancellationRequested();

		JsonRpcRequest request = new()
		{
			Id = null,
			Method = method,
			Arguments = arguments,
		};

		try
		{
			return this.AwaitPostedNotificationAsync(this.PostMessageAsync(request, cancellationToken), request);
		}
		catch
		{
			this.marshaledObjects.ReleaseLocalObjects(request.Arguments);
			throw;
		}
	}

	public void Start()
	{
		this.readerTask = this.ReadAsync(this.channel.Reader);
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		this.disposalSource.Cancel();
		lock (this.connectionSync)
		{
			this.completionSource.TrySetCanceled();
			this.channel.Writer.TryComplete();
			foreach ((RequestId id, TaskCompletionSource<JsonRpcResponse> pending) in this.pendingOutboundRequests)
			{
				if (this.pendingOutboundRequests.TryRemove(id, out _))
				{
					pending.TrySetException(new ObjectDisposedException(nameof(JsonRpc)));
				}
			}
		}

		this.marshaledObjects.DisposeAll();
	}

	internal static object AttachCore(IJsonRpcClient client, Type interfaceType, JsonRpcProxyOptions? options = null)
	{
		Requires.NotNull(client);
		Requires.NotNull(interfaceType);
		Requires.Argument(interfaceType.IsInterface, nameof(interfaceType), "The requested proxy type must be an interface.");

		JsonRpcProxyImplementationAttribute? implementation = interfaceType.GetCustomAttribute<JsonRpcProxyImplementationAttribute>();
		if (implementation is null)
		{
			throw new NotSupportedException($"No generated JSON-RPC proxy was found for interface '{interfaceType.FullName}'. Add GenerateJsonRpcProxyAttribute to the interface or request an annotated composite interface.");
		}

		Type proxyType = implementation.ProxyType;
		if (!interfaceType.IsAssignableFrom(proxyType))
		{
			throw new InvalidOperationException($"The generated proxy type '{proxyType.FullName}' does not implement requested interface '{interfaceType.FullName}'.");
		}

		ConstructorInfo? constructor = proxyType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, types: [typeof(IJsonRpcClient), typeof(JsonRpcProxyOptions)], modifiers: null);
		if (constructor is null)
		{
			throw new InvalidOperationException($"The generated proxy type '{proxyType.FullName}' does not have a constructor that accepts an IJsonRpcClient and JsonRpcProxyOptions instance.");
		}

		return constructor.Invoke([client, options ?? new JsonRpcProxyOptions()]);
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

	internal JsonRpcValue MarshalReleaseArguments(long handle)
	{
		if (this.channel.Encoding == JsonRpcEncoding.Json)
		{
			using Sequence<byte> buffer = new();
			using Utf8JsonWriter writer = new(buffer);
			writer.WriteStartArray();
			writer.WriteNumberValue(handle);
			writer.WriteBooleanValue(false);
			writer.WriteEndArray();
			writer.Flush();
			return JsonRpcValue.FromJson(buffer.AsReadOnlySequence);
		}

		using Sequence<byte> msgpackBuffer = new();
		MessagePackWriter msgpackWriter = new(msgpackBuffer);
		msgpackWriter.WriteArrayHeader(2);
		msgpackWriter.Write(handle);
		msgpackWriter.Write(false);
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

			return this.pendingOutboundRequests.TryAdd(request.Id.Value, responseTcs);
		}
	}

	internal bool TryUnregisterOutboundRequest(RequestId id) => this.pendingOutboundRequests.TryRemove(id, out _);

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

	internal async ValueTask AwaitVoidResponseAsync(ValueTask<JsonRpcResponse> responseTask)
	{
		JsonRpcResponse response = await responseTask.ConfigureAwait(false);
		switch (response)
		{
			case JsonRpcResult:
				return;
			case JsonRpcError error:
				throw new JsonRpcException(error.Error);
			default:
				throw new InvalidOperationException("Received an unknown response type.");
		}
	}

	internal async ValueTask<TResult> AwaitTypedResponseAsync<TResult>(JsonRpcRequest request, ITypeShape<TResult> resultShape, ValueTask<JsonRpcResponse> responseTask, CancellationToken cancellationToken)
	{
		JsonRpcResponse response = await responseTask.ConfigureAwait(false);
		switch (response)
		{
			case JsonRpcResult result:
				TResult returnValue = this.userDataSerializer.Deserialize(result.Result, resultShape, cancellationToken)!;
				return returnValue;
			case JsonRpcError error:
				throw new JsonRpcException(error.Error);
			default:
				throw new InvalidOperationException("Received an unknown response type.");
		}
	}

	private Task<JsonRpcResponse?> DispatchAsync(JsonRpcRequest request)
	{
		if (request.Id is null && this.marshaledObjects.TryHandleNotification(request))
		{
			return Task.FromResult<JsonRpcResponse?>(null);
		}

		if (!this.handlers.TryGetValue(request.Method, out (object? Target, MethodInvoker Invoker) handler))
		{
			return Task.FromResult<JsonRpcResponse?>(request.Id is RequestId missingId
				? new JsonRpcError { Id = missingId, Error = new() { Code = JsonRpcErrorCode.MethodNotFound, Message = $"The method {request.Method} is not supported." } }
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

				if (!this.pendingInboundRequests.TryAdd(id, tracker))
				{
					tracker.Dispose();
					throw new ProtocolViolationException($"A request with ID {id} is already pending.");
				}
			}
			else
			{
				tracker = default;
			}

			DispatchRequest dispatchRequest = new()
			{
				JsonRpc = this,
				Request = request,
				TargetInstance = handler.Target,
				CancellationToken = tracker.CancellationTokenSource?.Token ?? default,
			};

			return HelperAsync();

			async Task<JsonRpcResponse?> HelperAsync()
			{
				try
				{
					// Changes to the ambient tracker made here are scoped to this async method's execution context.
					string? parentToken = request.JoinableTaskToken;
					JoinableTaskFactory? jtf = this.JoinableTaskFactory;
					if (jtf is null)
					{
						this.JoinableTaskTracker.Token = parentToken;
					}

					DispatchResponse response = jtf is not null && parentToken is not null
						? await jtf.RunAsync(() => handler.Invoker(dispatchRequest).AsTask(), parentToken, JoinableTaskCreationOptions.None)
						: await handler.Invoker(dispatchRequest).ConfigureAwait(false);
					Assumes.True(request.Id is null == response.Response is null, "A response is expected iff the request included an ID.");
					return response.Response;
				}
				finally
				{
					if (request.Id is RequestId id)
					{
						if (this.pendingInboundRequests.TryRemove(id, out PendingInboundRequest tracker))
						{
							tracker.Dispose();
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			this.Fault(ex);
			return Task.FromException<JsonRpcResponse?>(ex);
		}
	}

	private void FaultOnFailure(Task task) => task.ContinueWith(static (t, s) => ((JsonRpc)s!).Fault(t.Exception!), this, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default).Forget();

	private void ThrowIfStarted() => Verify.Operation(this.readerTask is null, "This property may only be set before Start is called.");

	private void ProcessResponse(JsonRpcResponse response)
	{
		ProtocolViolationException? unmatchedResponseException = null;
		lock (this.connectionSync)
		{
			if (this.Completion.IsCompleted)
			{
				return;
			}

			if (this.pendingOutboundRequests.TryRemove(response.Id, out TaskCompletionSource<JsonRpcResponse>? tcs))
			{
				tcs.TrySetResult(response);
			}
			else
			{
				unmatchedResponseException = new ProtocolViolationException($"Received a response with ID {response.Id} that does not match any pending requests.");
			}
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
				this.FaultOnFailure(this.ProcessRequestAsync(request));
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
					requestTasks.Add(this.DispatchAsync(request));
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

	private async Task ProcessRequestAsync(JsonRpcRequest request)
	{
		JsonRpcResponse? response = await this.DispatchAsync(request).ConfigureAwait(false);
		if (response is not null)
		{
			await this.PostMessageAsync(response).ConfigureAwait(false);
		}
	}

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
			this.ApplyJoinableTaskToken(request);
			await this.PostMessageAsync(request, cancellationToken).ConfigureAwait(false);
			posted = true;

			return await this.AwaitResponseAsync(request, responseTcs, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (!posted)
		{
			if (request.Id.HasValue)
			{
				this.TryUnregisterOutboundRequest(request.Id.Value);
			}

			this.marshaledObjects.ReleaseLocalObjects(request.Arguments);
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
			while (await inbound.WaitToReadAsync(this.DisposalToken).ConfigureAwait(false))
			{
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
				if (this.pendingOutboundRequests.TryRemove(id, out _))
				{
					pending.TrySetException(exception);
				}
			}

			this.channel.Writer.TryComplete(exception);
			this.Logger.LogError(exception, "JSON-RPC connection terminated: {Reason}", exception.Message);
		}

		try
		{
			this.marshaledObjects.DisposeAll();
		}
		catch (Exception ex)
		{
			this.Logger.LogError(ex, "One or more marshaled objects failed to dispose while faulting the JSON-RPC connection.");
		}

		this.disposalSource.Cancel();
	}

	[GenerateShape]
	internal partial struct CancelRequestParams
	{
		public CancelRequestParams(RequestId id) => this.Id = id;

		[Key(0)]
		[PropertyShape(IsRequired = true)]
		public RequestId Id { get; set; }
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
			if (owner.pendingInboundRequests.TryGetValue(id, out PendingInboundRequest tracker))
			{
				tracker.CancellationTokenSource?.Cancel();
			}
		}
	}
}
