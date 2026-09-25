// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1649 // File name should match first type name

namespace Nerdbank.JsonRpc;

internal delegate ValueTask<DispatchResponse> MethodInvoker(DispatchRequest request);

/// <summary>Creates a delegate that forwards a raised CLR event to the given <see cref="JsonRpc"/> connection as a notification with the given method name.</summary>
internal delegate Delegate CreateEventHandlerDelegate(JsonRpc jsonRpc, string eventName);

/// <summary>Reads one event handler argument from the given argument state and adds it to the JSON-RPC notification arguments being built.</summary>
internal delegate void EventArgumentWriter<TArgumentState>(ref TArgumentState state, ref JsonRpcArgumentsBuilder builder);
