// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

internal struct DispatchRequest
{
	internal JsonRpcSerializer UserDataSerializer => this.JsonRpc.UserDataSerializer;

	internal required JsonRpc JsonRpc { get; init; }

	internal required object? TargetInstance { get; init; }

	internal required JsonRpcRequest Request { get; init; }

	internal required RpcCallState CallState { get; init; }

	internal required CancellationToken CancellationToken { get; init; }

	internal bool IsNotification => this.Request.Id is null;

	// The following helpers exist so that exception filters don't copy structs
	// (loading a struct-typed field or calling a struct-returning getter inside a filter crashes the NetWasm 0.5.0 compiler).

	/// <summary>Gets a value indicating whether cancellation has been requested for this dispatch.</summary>
	/// <returns><see langword="true"/> if cancellation was requested.</returns>
	internal readonly bool IsCancellationRequested() => this.CancellationToken.IsCancellationRequested;

	/// <summary>Tests whether this dispatch was canceled and is a request (rather than a notification).</summary>
	/// <param name="id">Receives the request ID.</param>
	/// <returns><see langword="true"/> if cancellation was requested and the request has an ID.</returns>
	internal readonly bool IsCanceledRequest(out RequestId id)
	{
		if (this.CancellationToken.IsCancellationRequested && this.Request.Id is RequestId requestId)
		{
			id = requestId;
			return true;
		}

		id = default;
		return false;
	}
}
