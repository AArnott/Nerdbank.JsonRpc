// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

public class JsonRpc : IDisposableObservable
{
	private readonly TaskCompletionSource<bool> completionSource = new();
	private readonly CancellationTokenSource disposalSource = new();
	private readonly ConcurrentDictionary<string, (object? Target, MethodInvoker Invoker)> handlers = new();
	private ChannelWriter<JsonRpcMessage> outboundWriter;
	private Task? readerTask;

	public JsonRpc()
	{
		Channel<JsonRpcMessage> outbound = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true });
		this.outboundWriter = outbound.Writer;
		this.OutboundMessages = outbound.Reader;
	}

	public ChannelReader<JsonRpcMessage> OutboundMessages { get; }

	public MessagePackSerializer Serializer { get; init; } = new MessagePackSerializer();

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
				this.PostMessage(new JsonRpcError { Id = missingId, Code = -32601, Message = $"The method {request.Method} is not supported." });
				return;
			}
		}

		// Dispatch to the handler.
		try
		{
			DispatchRequest dispatchRequest = new()
			{
				JsonRpc = this,
				Request = request,
				TargetInstance = handler.Target,
				CancellationToken = default, // TODO
			};

			Helper();
#pragma warning disable VSTHRD100 // Avoid async void methods (we catch and report everything).
			async void Helper()
#pragma warning restore VSTHRD100 // Avoid async void methods
			{
				try
				{
					DispatchResponse response = await handler.Invoker(dispatchRequest);
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
}
