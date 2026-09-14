// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

public partial class JsonRpc : IDisposableObservable
{
	private const string SpecialCancelMethodName = "$/cancelRequest";
	private const string ProgressMethodName = "$/progress";

	private readonly ConcurrentDictionary<RequestId, PendingInboundRequest> pendingInboundRequests = [];
	private readonly TaskCompletionSource<bool> completionSource = new();
	private readonly CancellationTokenSource disposalSource = new();
	private readonly ConcurrentDictionary<string, (object? Target, MethodInvoker Invoker)> handlers = new();
	private readonly ConcurrentDictionary<RequestId, TaskCompletionSource<JsonRpcResponse>> pendingOutboundRequests = new();
	private readonly Action<object?> cancelOutboundRequestDelegate;
	private readonly Channel<JsonRpcMessage> channel;
	private readonly ProgressTracker progressTracker;
	private Task? readerTask;
	private int nextRequestId;

	public JsonRpc(Channel<JsonRpcMessage> channel)
	{
		// Store a delegate we can reuse to avoid allocations.
		this.cancelOutboundRequestDelegate = this.CancelOutboundRequest;

		this.AddRpcTarget(new SpecialMethodsTarget(this));
		this.progressTracker = new(this);
		this.channel = channel;
	}

	/// <summary>
	/// Gets the default serializer used for JSON-RPC messages.
	/// </summary>
	public static MessagePackSerializer DefaultSerializer { get; } = new MessagePackSerializer
	{
		InternStrings = true,
	};

	/// <summary>
	/// Gets the serializer used for JSON-RPC messages by this instance.
	/// </summary>
	public MessagePackSerializer Serializer { get; init; } = DefaultSerializer;

	public JsonRpcState State =>
		this.IsDisposed ? JsonRpcState.Disposed :
		this.Completion.IsFaulted ? JsonRpcState.Faulted :
		this.readerTask is not null ? JsonRpcState.Running :
		JsonRpcState.NotStarted;

	public Task Completion => this.completionSource.Task;

	public bool IsDisposed => this.disposalSource.IsCancellationRequested;

	internal CancellationToken DisposalToken => this.disposalSource.Token;

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
	/// Attaches a generated client proxy for an RPC contract interface to this JSON-RPC connection.
	/// </summary>
	/// <typeparam name="T">The RPC contract interface to proxy.</typeparam>
	/// <param name="options">Options reserved for future proxy attachment behavior.</param>
	/// <returns>A generated proxy instance that implements <typeparamref name="T"/>.</returns>
	public T Attach<T>(JsonRpcProxyOptions? options = null) => (T)this.Attach(typeof(T), options);

	/// <summary>
	/// Attaches a generated client proxy for an RPC contract interface to this JSON-RPC connection.
	/// </summary>
	/// <param name="interfaceType">The RPC contract interface to proxy.</param>
	/// <param name="options">Options reserved for future proxy attachment behavior.</param>
	/// <returns>A generated proxy instance that implements <paramref name="interfaceType"/>.</returns>
	public object Attach(Type interfaceType, JsonRpcProxyOptions? options = null)
	{
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

		ConstructorInfo? constructor = proxyType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, types: [typeof(JsonRpc)], modifiers: null);
		if (constructor is null)
		{
			throw new InvalidOperationException($"The generated proxy type '{proxyType.FullName}' does not have a constructor that accepts a JsonRpc instance.");
		}

		return constructor.Invoke([this]);
	}

#if NET
	public ValueTask RequestAsync<TArg>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg> => this.RequestAsync(method, arguments, TArg.GetTypeShape(), cancellationToken);

	public ValueTask<TResult> RequestAsync<TArg, TResult>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg>
		where TResult : IShapeable<TResult>
	{
		return this.RequestAsync(method, arguments, TArg.GetTypeShape(), TResult.GetTypeShape(), cancellationToken);
	}

	public ValueTask<TResult> RequestAsync<TArg, TResult, TResultProvider>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg>
		where TResultProvider : IShapeable<TResult>
	{
		return this.RequestAsync(method, arguments, TArg.GetTypeShape(), TResultProvider.GetTypeShape(), cancellationToken);
	}

	public void Notify<TArg>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg> => this.Notify(method, arguments, TArg.GetTypeShape(), cancellationToken);
#endif

	public ValueTask<TResult> RequestAsync<TArg, TResult>(string method, in TArg arguments, ITypeShape<TArg> argShape, ITypeShape<TResult> resultShape, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = this.GetNextRequestId(),
			Method = method,
			Arguments = (RawMessagePack)this.Serializer.Serialize(arguments, argShape, cancellationToken),
		};

		return HelperAsync();
		async ValueTask<TResult> HelperAsync()
		{
			JsonRpcResponse response = await this.RequestAsync(request, progressTokens: null, cancellationToken).ConfigureAwait(false);
			switch (response)
			{
				case JsonRpcResult result:
					TResult returnValue = this.Serializer.Deserialize(result.Result, resultShape, cancellationToken)!;
					return returnValue;
				case JsonRpcError error:
					throw new JsonRpcException(error.Error);
				default:
					throw new InvalidOperationException("Received an unknown response type.");
			}
		}
	}

	public ValueTask RequestAsync<TArg>(string method, in TArg arguments, ITypeShape<TArg> argShape, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = this.GetNextRequestId(),
			Method = method,
			Arguments = (RawMessagePack)this.Serializer.Serialize(arguments, argShape, cancellationToken),
		};

		return HelperAsync();
		async ValueTask HelperAsync()
		{
			JsonRpcResponse response = await this.RequestAsync(request, progressTokens: null, cancellationToken).ConfigureAwait(false);
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
	}

	public void Notify<TArg>(string method, in TArg arguments, ITypeShape<TArg> argShape, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = null,
			Method = method,
			Arguments = (RawMessagePack)this.Serializer.Serialize(arguments, argShape, cancellationToken),
		};

		this.PostMessage(request);
	}

	/// <summary>
	/// Registers a progress callback and returns the token to send as its RPC argument.
	/// </summary>
	/// <typeparam name="T">The type of progress values.</typeparam>
	/// <param name="progress">The callback to receive progress values.</param>
	/// <param name="valueShape">The type shape for <typeparamref name="T"/>.</param>
	/// <returns>The token to serialize in place of <paramref name="progress"/>.</returns>
	public long RegisterProgress<T>(IProgress<T> progress, ITypeShape<T> valueShape) => this.progressTracker.Register(progress, valueShape);

	/// <summary>
	/// Sends a notification with arguments that have already been serialized to MessagePack.
	/// </summary>
	/// <param name="method">The name of the remote method to invoke.</param>
	/// <param name="arguments">The pre-serialized arguments payload.</param>
	/// <param name="cancellationToken">A token whose cancellation is observed before the notification is posted.</param>
	public void Notify(string method, RawMessagePack arguments, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		JsonRpcRequest request = new()
		{
			Id = null,
			Method = method,
			Arguments = arguments,
		};

		this.PostMessage(request);
	}

	/// <summary>
	/// Sends a request with arguments that have already been serialized to MessagePack.
	/// </summary>
	/// <param name="method">The name of the remote method to invoke.</param>
	/// <param name="arguments">The pre-serialized arguments payload.</param>
	/// <param name="cancellationToken">A token whose cancellation should be propagated to the remote endpoint.</param>
	/// <returns>A task that completes when the remote endpoint sends its response.</returns>
	public ValueTask RequestAsync(string method, RawMessagePack arguments, CancellationToken cancellationToken)
		=> this.RequestAsync(method, arguments, progressTokens: null, cancellationToken);

	/// <summary>
	/// Sends a request with pre-serialized arguments and progress tokens to associate with its response.
	/// </summary>
	/// <param name="method">The name of the remote method to invoke.</param>
	/// <param name="arguments">The pre-serialized arguments payload.</param>
	/// <param name="progressTokens">Progress tokens serialized into <paramref name="arguments"/>.</param>
	/// <param name="cancellationToken">A token whose cancellation should be propagated to the remote endpoint.</param>
	/// <returns>A task that completes when the remote endpoint sends its response.</returns>
	public ValueTask RequestAsync(string method, RawMessagePack arguments, IReadOnlyList<long>? progressTokens, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = this.GetNextRequestId(),
			Method = method,
			Arguments = arguments,
		};

		return HelperAsync();
		async ValueTask HelperAsync()
		{
			JsonRpcResponse response = await this.RequestAsync(request, progressTokens, cancellationToken).ConfigureAwait(false);
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
	}

	/// <summary>
	/// Sends a request with arguments that have already been serialized to MessagePack.
	/// </summary>
	/// <typeparam name="TResult">The expected result type.</typeparam>
	/// <param name="method">The name of the remote method to invoke.</param>
	/// <param name="arguments">The pre-serialized arguments payload.</param>
	/// <param name="resultShape">The type shape describing <typeparamref name="TResult"/>.</param>
	/// <param name="cancellationToken">A token whose cancellation should be propagated to the remote endpoint.</param>
	/// <returns>A task that completes with the result returned by the remote endpoint.</returns>
	public ValueTask<TResult> RequestAsync<TResult>(string method, RawMessagePack arguments, ITypeShape<TResult> resultShape, CancellationToken cancellationToken)
		=> this.RequestAsync(method, arguments, resultShape, progressTokens: null, cancellationToken);

	/// <summary>
	/// Sends a request with pre-serialized arguments and progress tokens to associate with its response.
	/// </summary>
	/// <typeparam name="TResult">The expected result type.</typeparam>
	/// <param name="method">The name of the remote method to invoke.</param>
	/// <param name="arguments">The pre-serialized arguments payload.</param>
	/// <param name="resultShape">The type shape describing <typeparamref name="TResult"/>.</param>
	/// <param name="progressTokens">Progress tokens serialized into <paramref name="arguments"/>.</param>
	/// <param name="cancellationToken">A token whose cancellation should be propagated to the remote endpoint.</param>
	/// <returns>A task that completes with the result returned by the remote endpoint.</returns>
	public ValueTask<TResult> RequestAsync<TResult>(string method, RawMessagePack arguments, ITypeShape<TResult> resultShape, IReadOnlyList<long>? progressTokens, CancellationToken cancellationToken)
	{
		Requires.NotNull(resultShape);

		JsonRpcRequest request = new()
		{
			Id = this.GetNextRequestId(),
			Method = method,
			Arguments = arguments,
		};

		return HelperAsync();
		async ValueTask<TResult> HelperAsync()
		{
			JsonRpcResponse response = await this.RequestAsync(request, progressTokens, cancellationToken).ConfigureAwait(false);
			switch (response)
			{
				case JsonRpcResult result:
					TResult returnValue = this.Serializer.Deserialize(result.Result, resultShape, cancellationToken)!;
					return returnValue;
				case JsonRpcError error:
					throw new JsonRpcException(error.Error);
				default:
					throw new InvalidOperationException("Received an unknown response type.");
			}
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
	}

	private long GetNextRequestId() => Interlocked.Increment(ref this.nextRequestId);

	private void Dispatch(JsonRpcRequest request)
	{
		if (!this.handlers.TryGetValue(request.Method, out (object? Target, MethodInvoker Invoker) handler))
		{
			if (request.Id is RequestId missingId)
			{
				// Report method not found.
				this.PostMessage(new JsonRpcError { Id = missingId, Error = new() { Code = -32601, Message = $"The method {request.Method} is not supported." } });
				return;
			}
		}

		// Dispatch to the handler.
		try
		{
			PendingInboundRequest tracker;

			if (request.Id is RequestId id)
			{
				tracker = new()
				{
					CancellationTokenSource = new(),
				};

				Assumes.True(this.pendingInboundRequests.TryAdd(id, tracker));
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

			Helper();
#pragma warning disable VSTHRD100 // Avoid async void methods (we catch and report everything).
			async void Helper()
#pragma warning restore VSTHRD100 // Avoid async void methods
			{
				try
				{
					DispatchResponse response = await handler.Invoker(dispatchRequest).ConfigureAwait(false);
					Assumes.True(request.Id is null == response.Response is null, "A response is expected iff the request included an ID.");

					if (response.Response is not null)
					{
						this.PostMessage(response.Response);
					}
				}
				catch (Exception ex)
				{
					this.Fault(ex);
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
		}
	}

	private void FaultOnFailure(Task task) => task.ContinueWith(static (t, s) => ((JsonRpc)s!).Fault(t.Exception!), this, this.DisposalToken, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default).Forget();

	private void ProcessResponse(JsonRpcResponse response)
	{
		this.progressTracker.CompleteRequest(response.Id);
		if (this.pendingOutboundRequests.TryRemove(response.Id, out TaskCompletionSource<JsonRpcResponse>? tcs))
		{
			tcs.SetResult(response);
		}
		else
		{
			this.Fault(new InvalidOperationException($"Received a response with ID {response.Id} that does not match any pending requests."));
		}
	}

	private void ProcessIncomingMessage(JsonRpcMessage message)
	{
		switch (message)
		{
			case JsonRpcRequest request:
				if (request.Method == ProgressMethodName)
				{
					this.ProcessProgressNotification(request);
				}
				else
				{
					this.Dispatch(request);
				}

				break;
			case JsonRpcResponse response:
				this.ProcessResponse(response);
				break;
		}
	}

	private async ValueTask<JsonRpcResponse> RequestAsync(JsonRpcRequest request, IReadOnlyList<long>? progressTokens, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Requires.Argument(request.Id.HasValue, nameof(request), "Request must have an ID for tracking the response.");
		Verify.Operation(this.State == JsonRpcState.Running, $"This instance is not listening for messages. Current state is {this.State}.");

		TaskCompletionSource<JsonRpcResponse> responseTcs = new();
		Assumes.True(this.pendingOutboundRequests.TryAdd(request.Id.Value, responseTcs));
		this.progressTracker.AssociateWithRequest(request.Id.Value, progressTokens);
		this.PostMessage(request);

		using (cancellationToken.Register(this.cancelOutboundRequestDelegate, request))
		{
			JsonRpcResponse response = await responseTcs.Task.ConfigureAwait(false);
			return response;
		}
	}

	private void ProcessProgressNotification(JsonRpcRequest request)
	{
		MessagePackReader reader = new(request.Arguments);
		switch (reader.NextMessagePackType)
		{
			case MessagePackType.Array when reader.ReadArrayHeader() == 2:
				this.progressTracker.Report(reader.ReadInt64(), reader.ReadRaw(this.Serializer.StartingContext));
				break;
			case MessagePackType.Map when reader.ReadMapHeader() == 2 && reader.ReadString() == "token":
				long token = reader.ReadInt64();
				if (reader.ReadString() == "value")
				{
					this.progressTracker.Report(token, reader.ReadRaw(this.Serializer.StartingContext));
				}

				break;
		}
	}

	private void CancelOutboundRequest(object? state)
	{
		JsonRpcRequest request = (JsonRpcRequest)state!;
		this.Notify(SpecialCancelMethodName, new CancelRequestParams(request.Id!.Value), CancellationToken.None);
	}

	private async Task ReadAsync(ChannelReader<JsonRpcMessage> inbound)
	{
		while (!inbound.Completion.IsCompleted)
		{
			JsonRpcMessage message = await inbound.ReadAsync(this.DisposalToken).ConfigureAwait(false);
			this.ProcessIncomingMessage(message);
		}
	}

	private void PostMessage(JsonRpcMessage message)
	{
		if (this.channel.Writer.TryWrite(message))
		{
			return;
		}

		this.Fault(new InvalidOperationException("Unable to write message to outbound channel."));
	}

	private void Fault(Exception exception)
	{
		this.completionSource.TrySetException(exception);
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

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
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
