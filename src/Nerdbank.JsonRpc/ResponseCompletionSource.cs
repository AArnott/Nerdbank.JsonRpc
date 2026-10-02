// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks.Sources;

namespace Nerdbank.JsonRpc;

/// <summary>A reusable, single-consumer rendezvous for a direct outbound request's response.</summary>
/// <remarks>
/// The owning request retains this source until its internal await and cancellation registration have ended.
/// Completion, removal from the pending-request table, and recycling are serialized by the connection's lock.
/// </remarks>
internal sealed class ResponseCompletionSource : IValueTaskSource<JsonRpcResponse>
{
	private ManualResetValueTaskSourceCore<JsonRpcResponse> completion;
	private bool resultConsumed;

	/// <summary>Initializes a new instance of the <see cref="ResponseCompletionSource"/> class.</summary>
	internal ResponseCompletionSource()
	{
		this.completion.RunContinuationsAsynchronously = true;
	}

	/// <summary>Gets or sets the next source in the owning connection's bounded free list.</summary>
	internal ResponseCompletionSource? Next { get; set; }

	/// <summary>Gets the response awaitable, which is consumed once by the owning request.</summary>
	internal ValueTask<JsonRpcResponse> Task => new(this, this.completion.Version);

	/// <inheritdoc/>
	JsonRpcResponse IValueTaskSource<JsonRpcResponse>.GetResult(short token)
	{
		JsonRpcResponse response = this.completion.GetResult(token);
		this.resultConsumed = true;
		return response;
	}

	/// <inheritdoc/>
	ValueTaskSourceStatus IValueTaskSource<JsonRpcResponse>.GetStatus(short token) => this.completion.GetStatus(token);

	/// <inheritdoc/>
	void IValueTaskSource<JsonRpcResponse>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
		=> this.completion.OnCompleted(continuation, state, token, flags);

	/// <summary>Completes the response while holding the owning connection's lock.</summary>
	/// <param name="response">The response whose ownership is transferred to the consumer.</param>
	internal void SetResult(JsonRpcResponse response) => this.completion.SetResult(response);

	/// <summary>Faults the response while holding the owning connection's lock.</summary>
	/// <param name="exception">The failure.</param>
	internal void SetException(Exception exception) => this.completion.SetException(exception);

	/// <summary>Clears a source after its consumer and all producers have relinquished it.</summary>
	/// <remarks>Must be called under the connection's lock, after removing the pending request.</remarks>
	internal void Reset()
	{
		// A write can fail after a response arrived but before the request awaited it.
		if (!this.resultConsumed && this.completion.GetStatus(this.completion.Version) == ValueTaskSourceStatus.Succeeded
			&& this.completion.GetResult(this.completion.Version) is JsonRpcResult result)
		{
			result.Result.Release();
		}

		this.completion.Reset();
		this.resultConsumed = false;
	}
}
