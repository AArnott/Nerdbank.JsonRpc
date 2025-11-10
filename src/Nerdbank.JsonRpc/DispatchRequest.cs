// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

internal struct DispatchRequest
{
	internal MessagePackSerializer UserDataSerializer => this.JsonRpc.Serializer;

	internal required JsonRpc JsonRpc { get; init; }

	internal required object? TargetInstance { get; init; }

	internal required JsonRpcRequest Request { get; init; }

	internal required CancellationToken CancellationToken { get; init; }

	internal bool IsNotification => this.Request.Id is null;
}
