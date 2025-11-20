// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

public partial class JsonRpc : IDisposableObservable
{
	private readonly ConcurrentDictionary<RequestId, PendingInboundRequest> pendingInboundRequests = [];
	private readonly TaskCompletionSource<bool> completionSource = new();
	private readonly CancellationTokenSource disposalSource = new();
	private readonly ConcurrentDictionary<string, (object? Target, MethodInvoker Invoker)> handlers = new();
	private readonly ConcurrentDictionary<RequestId, TaskCompletionSource<JsonRpcResponse>> pendingOutboundRequests = new();
	private ChannelWriter<JsonRpcMessage> outboundWriter;
	private Task? readerTask;
	private int nextRequestId;

	public JsonRpc()
	{
		Channel<JsonRpcMessage> outbound = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true });
		this.outboundWriter = outbound.Writer;
		this.OutboundMessages = outbound.Reader;

		this.AddRpcTarget(new SpecialMethodsTarget(this));
	}

	public ChannelReader<JsonRpcMessage> OutboundMessages { get; }

	public MessagePackSerializer Serializer { get; init; } = new MessagePackSerializer
	{
		InternStrings = true,
	};

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

#if NET
	public ValueTask RequestAsync<TArg>(string method, TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg> => this.RequestAsync(method, arguments, TArg.GetTypeShape(), cancellationToken);

	public ValueTask<TResult> RequestAsync<TArg, TResult>(string method, TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg>
		where TResult : IShapeable<TResult>
	{
		return this.RequestAsync(method, arguments, TArg.GetTypeShape(), TResult.GetTypeShape(), cancellationToken);
	}

	public ValueTask<TResult> RequestAsync<TArg, TResult, TResultProvider>(string method, TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg>
		where TResultProvider : IShapeable<TResult>
	{
		return this.RequestAsync(method, arguments, TArg.GetTypeShape(), TResultProvider.GetTypeShape(), cancellationToken);
	}

	public void Notify<TArg>(string method, TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg> => this.Notify(method, arguments, TArg.GetTypeShape(), cancellationToken);
#endif

	public async ValueTask<TResult> RequestAsync<TArg, TResult>(string method, TArg arguments, ITypeShape<TArg> argShape, ITypeShape<TResult> resultShape, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = this.nextRequestId++,
			Method = method,
			Arguments = (RawMessagePack)this.Serializer.Serialize(arguments, argShape, cancellationToken),
		};

		JsonRpcResponse response = await this.RequestAsync(request, cancellationToken).ConfigureAwait(false);
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

	public async ValueTask RequestAsync<TArg>(string method, TArg arguments, ITypeShape<TArg> argShape, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = this.nextRequestId++,
			Method = method,
			Arguments = (RawMessagePack)this.Serializer.Serialize(arguments, argShape, cancellationToken),
		};

		JsonRpcResponse response = await this.RequestAsync(request, cancellationToken).ConfigureAwait(false);
		switch (response)
		{
			case JsonRpcResult result:
				return;
			case JsonRpcError error:
				throw new JsonRpcException(error.Error);
			default:
				throw new InvalidOperationException("Received an unknown response type.");
		}
	}

	public void Notify<TArg>(string method, TArg arguments, ITypeShape<TArg> argShape, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = null,
			Method = method,
			Arguments = (RawMessagePack)this.Serializer.Serialize(arguments, argShape, cancellationToken),
		};

		this.PostMessage(request);
	}

	public void Start(ChannelReader<JsonRpcMessage> inboundMessages)
	{
		Requires.NotNull(inboundMessages);
		this.readerTask = this.ReadAsync(inboundMessages);
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		this.disposalSource.Cancel();
	}

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
				this.Dispatch(request);
				break;
			case JsonRpcResponse response:
				this.ProcessResponse(response);
				break;
		}
	}

	private async ValueTask<JsonRpcResponse> RequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Requires.Argument(request.Id.HasValue, nameof(request), "Request must have an ID for tracking the response.");
		Verify.Operation(this.State == JsonRpcState.Running, $"This instance is not listening for messages. Current state is {this.State}.");

		TaskCompletionSource<JsonRpcResponse> responseTcs = new();
		Assumes.True(this.pendingOutboundRequests.TryAdd(request.Id.Value, responseTcs));
		this.PostMessage(request);

		// TODO: Handle cancellation of the request.

		JsonRpcResponse response = await responseTcs.Task.ConfigureAwait(false);
		return response;
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
		if (this.outboundWriter.TryWrite(message))
		{
			return;
		}

		this.Fault(new InvalidOperationException("Unable to write message to outbound channel."));
	}

	private void Fault(Exception exception)
	{
		this.completionSource.TrySetException(exception);
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
		[MethodShape(Name = "$/cancelRequest")]
		public void CancelRequest(RequestId id)
		{
			if (owner.pendingInboundRequests.TryGetValue(id, out PendingInboundRequest tracker))
			{
				tracker.CancellationTokenSource?.Cancel();
			}
		}
	}
}
