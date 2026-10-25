// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Exposes the transport client of a source-generated RPC proxy without reflection.</summary>
/// <remarks>The client remains owned according to the proxy's lifetime options.</remarks>
public interface IJsonRpcServiceProxy
{
	/// <summary>Gets the RPC client used by the generated proxy.</summary>
	IJsonRpcClient RpcClient { get; }
}
