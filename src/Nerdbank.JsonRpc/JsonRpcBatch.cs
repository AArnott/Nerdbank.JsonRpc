// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Builds and sends one JSON-RPC batch payload.
/// </summary>
/// <remarks>
/// A batch is one-shot. Requests and notifications added before <see cref="SendAsync"/> are transmitted in one protocol payload.
/// All requests are sent together, and their returned tasks complete after the peer returns the batch response.
/// </remarks>
public sealed class JsonRpcBatch : JsonRpcClient, IDisposable
{
	private readonly JsonRpc owner;
	private readonly List<Entry> entries = [];
	private readonly object syncObject = new();
	private bool sent;
	private bool payloadQueued;
	private bool disposed;

	internal JsonRpcBatch(JsonRpc owner)
	{
		this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
	}

	/// <inheritdoc/>
	public override JsonRpcSerializer Serializer => this.owner.Channel.Serializer;
	/// <inheritdoc/>
	public override JsonRpcValue MarshalDisposable(IDisposable value) => this.owner.MarshalDisposable(value);

	/// <inheritdoc/>
	public override async ValueTask<IDisposable> RequestDisposableAsync(string method, JsonRpcValue arguments, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new() { Id = this.owner.GetNextRequestId(), Method = method, Arguments = arguments };
		JsonRpcResponse response = await this.AddRequestAsync(request, cancellationToken).ConfigureAwait(false);
		return response is JsonRpcResult result ? this.owner.UnmarshalDisposable(result.Result) : throw new JsonRpcException(((JsonRpcError)response).Error);
	}

	/// <inheritdoc/>
	public override JsonRpcArgumentsBuilder CreateArguments(bool named, int count, CancellationToken cancellationToken = default) => new(this.Serializer, named, count, cancellationToken, this.owner.MarshalDisposable);

	/// <summary>
	/// Attaches a generated client proxy for an RPC contract interface to this batch.
	/// </summary>
	/// <typeparam name="T">The RPC contract interface to proxy.</typeparam>
	/// <param name="options">Options controlling argument encoding for this proxy.</param>
	/// <returns>A generated proxy instance that implements <typeparamref name="T"/>.</returns>
	public T Attach<T>(JsonRpcProxyOptions? options = null) => (T)this.Attach(typeof(T), options);

	/// <summary>
	/// Attaches a generated client proxy for an RPC contract interface to this batch.
	/// </summary>
	/// <param name="interfaceType">The RPC contract interface to proxy.</param>
	/// <param name="options">Options controlling argument encoding for this proxy.</param>
	/// <returns>A generated proxy instance that implements <paramref name="interfaceType"/>.</returns>
	public object Attach(Type interfaceType, JsonRpcProxyOptions? options = null) => JsonRpc.AttachCore(this, interfaceType, options);

#if NET
	/// <summary>
	/// Adds a request without a result to this batch.
	/// </summary>
	/// <typeparam name="TArg">The argument type.</typeparam>
	/// <param name="method">The remote method name.</param>
	/// <param name="arguments">The request arguments.</param>
	/// <param name="cancellationToken">A token whose cancellation controls whether this request is sent.</param>
	/// <returns>A task that completes when the batch response is received.</returns>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	public ValueTask RequestAsync<TArg>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg> => this.RequestAsync(method, arguments, TArg.GetTypeShape(), cancellationToken);

	/// <summary>
	/// Adds a request with a typed result to this batch.
	/// </summary>
	/// <typeparam name="TArg">The argument type.</typeparam>
	/// <typeparam name="TResult">The result type.</typeparam>
	/// <param name="method">The remote method name.</param>
	/// <param name="arguments">The request arguments.</param>
	/// <param name="cancellationToken">A token whose cancellation controls whether this request is sent.</param>
	/// <returns>A task that completes with the result when the batch response is received.</returns>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	public ValueTask<TResult> RequestAsync<TArg, TResult>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg>
		where TResult : IShapeable<TResult>
	{
		return this.RequestAsync(method, arguments, TArg.GetTypeShape(), TResult.GetTypeShape(), cancellationToken);
	}

	/// <summary>
	/// Adds a request with a typed result to this batch.
	/// </summary>
	/// <typeparam name="TArg">The argument type.</typeparam>
	/// <typeparam name="TResult">The result type.</typeparam>
	/// <typeparam name="TResultProvider">The result shape provider type.</typeparam>
	/// <param name="method">The remote method name.</param>
	/// <param name="arguments">The request arguments.</param>
	/// <param name="cancellationToken">A token whose cancellation controls whether this request is sent.</param>
	/// <returns>A task that completes with the result when the batch response is received.</returns>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	public ValueTask<TResult> RequestAsync<TArg, TResult, TResultProvider>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg>
		where TResultProvider : IShapeable<TResult>
	{
		return this.RequestAsync(method, arguments, TArg.GetTypeShape(), TResultProvider.GetTypeShape(), cancellationToken);
	}

	/// <summary>
	/// Adds a notification to this batch.
	/// </summary>
	/// <typeparam name="TArg">The argument type.</typeparam>
	/// <param name="method">The remote method name.</param>
	/// <param name="arguments">The notification arguments.</param>
	/// <param name="cancellationToken">A token whose cancellation controls whether this notification is sent.</param>
	/// <returns>A task that completes when the notification has been added to the batch.</returns>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	public ValueTask NotifyAsync<TArg>(string method, in TArg arguments, CancellationToken cancellationToken)
		where TArg : IShapeable<TArg> => this.NotifyAsync(method, arguments, TArg.GetTypeShape(), cancellationToken);
#endif

	/// <summary>
	/// Adds a request with a typed result to this batch.
	/// </summary>
	/// <typeparam name="TArg">The argument type.</typeparam>
	/// <typeparam name="TResult">The result type.</typeparam>
	/// <param name="method">The remote method name.</param>
	/// <param name="arguments">The request arguments.</param>
	/// <param name="argShape">The argument type shape.</param>
	/// <param name="resultShape">The result type shape.</param>
	/// <param name="cancellationToken">A token whose cancellation controls whether this request is sent.</param>
	/// <returns>A task that completes with the result when the batch response is received.</returns>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	public ValueTask<TResult> RequestAsync<TArg, TResult>(string method, in TArg arguments, ITypeShape<TArg> argShape, ITypeShape<TResult> resultShape, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = this.owner.GetNextRequestId(),
			Method = method,
			Arguments = this.owner.Channel.Serializer.Serialize(arguments, argShape, cancellationToken),
		};

		return this.owner.AwaitTypedResponseAsync(request, resultShape, this.AddRequestAsync(request, cancellationToken), cancellationToken);
	}

	/// <summary>
	/// Adds a request without a result to this batch.
	/// </summary>
	/// <typeparam name="TArg">The argument type.</typeparam>
	/// <param name="method">The remote method name.</param>
	/// <param name="arguments">The request arguments.</param>
	/// <param name="argShape">The argument type shape.</param>
	/// <param name="cancellationToken">A token whose cancellation controls whether this request is sent.</param>
	/// <returns>A task that completes when the batch response is received.</returns>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	public ValueTask RequestAsync<TArg>(string method, in TArg arguments, ITypeShape<TArg> argShape, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = this.owner.GetNextRequestId(),
			Method = method,
			Arguments = this.owner.Channel.Serializer.Serialize(arguments, argShape, cancellationToken),
		};

		return this.owner.AwaitVoidResponseAsync(this.AddRequestAsync(request, cancellationToken));
	}

	/// <summary>
	/// Adds a notification to this batch.
	/// </summary>
	/// <typeparam name="TArg">The argument type.</typeparam>
	/// <param name="method">The remote method name.</param>
	/// <param name="arguments">The notification arguments.</param>
	/// <param name="argShape">The argument type shape.</param>
	/// <param name="cancellationToken">A token whose cancellation controls whether this notification is sent.</param>
	/// <returns>A task that completes when the notification has been added to the batch.</returns>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	public ValueTask NotifyAsync<TArg>(string method, in TArg arguments, ITypeShape<TArg> argShape, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = null,
			Method = method,
			Arguments = this.owner.Channel.Serializer.Serialize(arguments, argShape, cancellationToken),
		};

		this.AddNotification(request, cancellationToken);
		return default;
	}

	/// <inheritdoc/>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	public override ValueTask RequestAsync(string method, JsonRpcValue arguments, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = this.owner.GetNextRequestId(),
			Method = method,
			Arguments = arguments,
		};

		return this.owner.AwaitVoidResponseAsync(this.AddRequestAsync(request, cancellationToken));
	}

	/// <inheritdoc/>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	public override ValueTask<TResult> RequestAsync<TResult>(string method, JsonRpcValue arguments, ITypeShape<TResult> resultShape, CancellationToken cancellationToken)
	{
		Requires.NotNull(resultShape);

		JsonRpcRequest request = new()
		{
			Id = this.owner.GetNextRequestId(),
			Method = method,
			Arguments = arguments,
		};

		return this.owner.AwaitTypedResponseAsync(request, resultShape, this.AddRequestAsync(request, cancellationToken), cancellationToken);
	}

	/// <inheritdoc/>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	public override ValueTask NotifyAsync(string method, JsonRpcValue arguments, CancellationToken cancellationToken)
	{
		JsonRpcRequest request = new()
		{
			Id = null,
			Method = method,
			Arguments = arguments,
		};

		this.AddNotification(request, cancellationToken);
		return default;
	}

	/// <summary>
	/// Seals this batch and queues all non-canceled entries as one JSON-RPC protocol payload.
	/// </summary>
	/// <param name="cancellationToken">
	/// A token whose cancellation is observed until the batch has been enqueued for transmission.
	/// After the batch has been enqueued, cancel individual requests by canceling the token supplied to each request,
	/// or call <see cref="CancelAllAsync"/> to enqueue one batch of cancellation notifications for every pending request in this batch.
	/// </param>
	/// <returns>A task that completes when the payload has been accepted by the outbound channel.</returns>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	public async ValueTask SendAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		List<Entry> snapshot = [];
		List<JsonRpcMessage> messages = [];
		try
		{
			lock (this.syncObject)
			{
				this.ThrowIfDisposedOrSent();
				this.sent = true;
				snapshot = [.. this.entries];
			}

			Verify.Operation(this.owner.State == JsonRpcState.Running, $"This instance is not listening for messages. Current state is {this.owner.State}.");
			foreach (Entry entry in snapshot)
			{
				if (!entry.TryMarkSent())
				{
					entry.CancelUnsent();
					continue;
				}

				messages.Add(entry.Request);
				if (entry.ResponseCompletionSource is not null)
				{
					Assumes.True(entry.Request.Id.HasValue);
					if (!this.owner.TryRegisterOutboundRequest(entry.Request, entry.ResponseCompletionSource))
					{
						throw new InvalidOperationException($"A request with ID {entry.Request.Id.Value} is already pending.");
					}
				}
			}

			if (messages is [])
			{
				throw new InvalidOperationException("A JSON-RPC batch must contain at least one non-canceled entry.");
			}

			await this.owner.PostMessageAsync(new JsonRpcMessageBatch([.. messages]), cancellationToken).ConfigureAwait(false);

			List<Entry> deferredCancellationEntries;
			lock (this.syncObject)
			{
				this.payloadQueued = true;
				deferredCancellationEntries = this.GetCancellationEntries(snapshot, markCanceled: false);
			}

			if (deferredCancellationEntries.Count > 0)
			{
				_ = this.SendCancellationBatchAsync(deferredCancellationEntries, throwOnFailure: false).AsTask();
			}

			foreach (Entry entry in snapshot)
			{
				if (entry.ResponseCompletionSource is null)
				{
					entry.Dispose();
				}
			}
		}
		catch (Exception ex)
		{
			foreach (Entry entry in snapshot)
			{
				if (entry.ResponseCompletionSource is not null)
				{
					if (entry.Request.Id.HasValue)
					{
						this.owner.TryUnregisterOutboundRequest(entry.Request.Id.Value);
					}

					entry.ResponseCompletionSource.TrySetException(ex);
				}
				else
				{
					entry.Dispose();
				}
			}

			throw;
		}

		return;
	}

	/// <summary>
	/// Queues cancellation notifications for all pending requests sent by this batch.
	/// </summary>
	/// <returns>A task that completes when cancellation notifications have been accepted by the outbound channel.</returns>
	/// <exception cref="ObjectDisposedException">Thrown if this batch has been disposed.</exception>
	/// <remarks>
	/// If this batch has not been sent yet, pending request tasks are canceled locally and those requests will be omitted from <see cref="SendAsync"/>.
	/// After this batch has been sent, this method sends one JSON-RPC batch containing a <c>$/cancelRequest</c> notification for each request that has not already completed or been canceled.
	/// </remarks>
	public async ValueTask CancelAllAsync()
	{
		List<Entry> snapshot;
		bool alreadySent;
		bool payloadQueued;
		lock (this.syncObject)
		{
			if (this.disposed)
			{
				throw new ObjectDisposedException(nameof(JsonRpcBatch));
			}

			alreadySent = this.sent;
			payloadQueued = this.payloadQueued;
			snapshot = [.. this.entries];
			if (!alreadySent)
			{
				foreach (Entry entry in snapshot)
				{
					if (entry.ResponseCompletionSource is not null)
					{
						entry.CancelUnsent();
					}
				}

				return;
			}

			if (!payloadQueued)
			{
				foreach (Entry entry in snapshot)
				{
					entry.MarkCanceledDuringSubmission();
				}

				return;
			}
		}

		List<Entry> cancellationEntries = this.GetCancellationEntries(snapshot, markCanceled: true);
		if (cancellationEntries.Count == 0)
		{
			return;
		}

		await this.SendCancellationBatchAsync(cancellationEntries, throwOnFailure: true).ConfigureAwait(false);

		return;
	}

	/// <summary>
	/// Releases this batch builder.
	/// </summary>
	/// <remarks>
	/// Disposing an unsent batch cancels pending request tasks and prevents subsequent additions, sending, or cancellation.
	/// Disposing a batch after it has been sent does not cancel requests that are already in flight.
	/// </remarks>
	public void Dispose()
	{
		List<Entry> snapshot;
		lock (this.syncObject)
		{
			if (this.disposed)
			{
				return;
			}

			this.disposed = true;
			if (this.sent)
			{
				return;
			}

			snapshot = [.. this.entries];
		}

		foreach (Entry entry in snapshot)
		{
			entry.CancelUnsent();
		}
	}

	private ValueTask<JsonRpcResponse> AddRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
	{
		TaskCompletionSource<JsonRpcResponse> responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Entry entry = new(this, request, responseTcs, cancellationToken);
		try
		{
			this.AddEntry(entry);
		}
		catch
		{
			entry.Dispose();
			throw;
		}

		return this.AwaitBatchResponseAsync(responseTcs, entry);
	}

	private async ValueTask<JsonRpcResponse> AwaitBatchResponseAsync(TaskCompletionSource<JsonRpcResponse> responseTcs, Entry entry)
	{
		try
		{
#pragma warning disable VSTHRD003 // Awaiting a TaskCompletionSource completed by inbound JSON-RPC responses.
			JsonRpcResponse response = await responseTcs.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			return response;
		}
		finally
		{
			entry.Dispose();
		}
	}

	private void AddNotification(JsonRpcRequest request, CancellationToken cancellationToken)
	{
		Entry entry = new(this, request, responseCompletionSource: null, cancellationToken);
		try
		{
			this.AddEntry(entry);
		}
		catch
		{
			entry.Dispose();
			throw;
		}
	}

	private void AddEntry(Entry entry)
	{
		this.owner.Channel.Serializer.ValidateMessage(entry.Request);
		lock (this.syncObject)
		{
			this.ThrowIfDisposedOrSent();
			this.entries.Add(entry);
		}
	}

	private void ThrowIfDisposedOrSent()
	{
		if (this.disposed)
		{
			throw new ObjectDisposedException(nameof(JsonRpcBatch));
		}

		Verify.Operation(!this.sent, "This JSON-RPC batch has already been sent.");
	}

	private List<Entry> GetCancellationEntries(List<Entry> entries, bool markCanceled)
	{
		List<Entry> cancellationEntries = [];
		foreach (Entry entry in entries)
		{
			if (entry.TryMarkCancelingSentRequest(markCanceled))
			{
				cancellationEntries.Add(entry);
			}
		}

		return cancellationEntries;
	}

	private async ValueTask SendCancellationBatchAsync(List<Entry> cancellationEntries, bool throwOnFailure)
	{
		List<JsonRpcMessage> cancellationRequests = [];
		foreach (Entry entry in cancellationEntries)
		{
			cancellationRequests.Add(this.owner.CreateCancellationNotification(entry.Request, CancellationToken.None));
		}

		try
		{
			await this.owner.PostMessageAsync(new JsonRpcMessageBatch([.. cancellationRequests])).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			foreach (Entry entry in cancellationEntries)
			{
				entry.Fault(ex);
			}

			if (throwOnFailure)
			{
				throw;
			}
		}
	}

	private void OnEntryCanceled(Entry entry)
	{
		if (entry.MarkCanceledBeforeSend())
		{
			return;
		}

		lock (this.syncObject)
		{
			if (!this.payloadQueued || !entry.TryMarkCancelingSentRequest(markCanceled: false))
			{
				return;
			}
		}

		_ = this.SendCancellationNotificationAsync(entry).AsTask();
	}

	private async ValueTask SendCancellationNotificationAsync(Entry entry)
	{
		try
		{
			await this.owner.PostMessageAsync(this.owner.CreateCancellationNotification(entry.Request, CancellationToken.None)).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			entry.Fault(ex);
		}
	}

	private sealed class Entry
	{
		private readonly JsonRpcBatch owner;
		private readonly CancellationTokenRegistration cancellationRegistration;
		private readonly object syncObject = new();
		private bool sent;
		private bool canceled;
		private bool cancellationSubmitted;

		internal Entry(
			JsonRpcBatch owner,
			JsonRpcRequest request,
			TaskCompletionSource<JsonRpcResponse>? responseCompletionSource,
			CancellationToken cancellationToken)
		{
			this.owner = owner;
			this.Request = request;
			this.ResponseCompletionSource = responseCompletionSource;
			if (cancellationToken.IsCancellationRequested)
			{
				this.canceled = true;
				this.ResponseCompletionSource?.TrySetCanceled(CancellationToken.None);
			}
			else
			{
				this.cancellationRegistration = cancellationToken.Register(
					static state =>
					{
						Entry entry = (Entry)state!;
						entry.owner.OnEntryCanceled(entry);
					},
					this);
			}
		}

		internal JsonRpcRequest Request { get; }

		internal TaskCompletionSource<JsonRpcResponse>? ResponseCompletionSource { get; }

		internal void CancelUnsent()
		{
			lock (this.syncObject)
			{
				this.canceled = true;
			}

			this.ResponseCompletionSource?.TrySetCanceled(CancellationToken.None);
			this.Dispose();
		}

		internal bool TryMarkSent()
		{
			lock (this.syncObject)
			{
				if (this.canceled)
				{
					return false;
				}

				this.sent = true;
				return true;
			}
		}

		internal bool MarkCanceledBeforeSend()
		{
			lock (this.syncObject)
			{
				if (this.canceled)
				{
					return true;
				}

				if (this.sent)
				{
					this.canceled = true;
					return false;
				}

				this.canceled = true;
			}

			this.ResponseCompletionSource?.TrySetCanceled(CancellationToken.None);
			return true;
		}

		internal void MarkCanceledDuringSubmission()
		{
			lock (this.syncObject)
			{
				if (this.ResponseCompletionSource is not null && !this.ResponseCompletionSource.Task.IsCompleted)
				{
					this.canceled = true;
				}
			}
		}

		internal bool TryMarkCancelingSentRequest(bool markCanceled)
		{
			lock (this.syncObject)
			{
				if (this.ResponseCompletionSource is null || !this.sent || this.ResponseCompletionSource.Task.IsCompleted || this.cancellationSubmitted)
				{
					return false;
				}

				if (!this.canceled)
				{
					if (!markCanceled)
					{
						return false;
					}

					this.canceled = true;
				}

				this.cancellationSubmitted = true;
				return true;
			}
		}

		internal void Fault(Exception ex)
		{
			if (this.Request.Id.HasValue)
			{
				this.owner.owner.TryUnregisterOutboundRequest(this.Request.Id.Value);
			}

			this.ResponseCompletionSource?.TrySetException(ex);
			this.Dispose();
		}

		internal void Dispose()
		{
			this.cancellationRegistration.Dispose();
		}
	}
}
