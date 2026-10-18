// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Completes either a direct request's reusable source or a batch's retained task.</summary>
/// <remarks>Reusable sources are completed only while holding the owning connection's lock.</remarks>
internal readonly struct PendingResponse
{
	private readonly ResponseCompletionSource? reusableSource;
	private readonly TaskCompletionSource<JsonRpcResponse>? taskSource;

	/// <summary>Initializes a new instance of the <see cref="PendingResponse"/> struct for a direct request.</summary>
	/// <param name="source">The reusable response source.</param>
	internal PendingResponse(ResponseCompletionSource source)
	{
		this.reusableSource = source;
		this.taskSource = null;
	}

	/// <summary>Initializes a new instance of the <see cref="PendingResponse"/> struct for a batch request.</summary>
	/// <param name="source">The retained task completion source.</param>
	internal PendingResponse(TaskCompletionSource<JsonRpcResponse> source)
	{
		this.reusableSource = null;
		this.taskSource = source;
	}

	/// <summary>Transfers a response to its consumer if the request is still pending.</summary>
	/// <param name="response">The received response.</param>
	/// <returns>Whether the consumer accepted ownership of the response.</returns>
	internal bool TrySetResult(JsonRpcResponse response)
	{
		if (this.reusableSource is { } source)
		{
			source.SetResult(response);
			return true;
		}

		return this.taskSource!.TrySetResult(response);
	}

	/// <summary>Faults a pending request.</summary>
	/// <param name="exception">The failure.</param>
	/// <returns>Whether the request was faulted.</returns>
	internal bool TrySetException(Exception exception)
	{
		if (this.reusableSource is { } source)
		{
			source.SetException(exception);
			return true;
		}

		return this.taskSource!.TrySetException(exception);
	}
}
