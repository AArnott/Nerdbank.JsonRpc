// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

internal struct DispatchRequest
{
	internal JsonRpcSerializer UserDataSerializer => this.JsonRpc.UserDataSerializer;

	internal required JsonRpc JsonRpc { get; init; }

	internal required object? TargetInstance { get; init; }

	internal required JsonRpcRequest Request { get; init; }

	internal required CancellationToken CancellationToken { get; init; }

	internal bool IsNotification => this.Request.Id is null;
}
