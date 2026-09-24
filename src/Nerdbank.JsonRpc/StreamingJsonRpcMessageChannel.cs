// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

namespace Nerdbank.JsonRpc;

/// <summary>Provides the original name for the MessagePack JSON-RPC pipe channel.</summary>
/// <remarks>Use <see cref="JsonRpcMessagePackChannel"/> for new code.</remarks>
[Obsolete("Use JsonRpcMessagePackChannel to make the MessagePack encoding explicit.")]
public class StreamingJsonRpcMessageChannel : JsonRpcMessagePackChannel
{
	/// <summary>Initializes a new instance of the <see cref="StreamingJsonRpcMessageChannel"/> class.</summary>
	/// <param name="pipe">The connected duplex pipe.</param>
	/// <param name="logger">The transport logger.</param>
	/// <param name="inboundCapacity">The inbound queue limit, or null for an unbounded queue.</param>
	/// <param name="outboundCapacity">The outbound queue limit, or null for an unbounded queue.</param>
	public StreamingJsonRpcMessageChannel(IDuplexPipe pipe, ILogger logger, int? inboundCapacity = 100, int? outboundCapacity = null)
		: base(pipe, logger, inboundCapacity, outboundCapacity)
	{
	}
}
