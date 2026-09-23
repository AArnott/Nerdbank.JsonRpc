// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Represents one JSON-RPC protocol payload containing multiple messages.
/// </summary>
/// <param name="messages">The immutable messages included in the batch payload.</param>
public sealed class JsonRpcMessageBatch(ImmutableArray<JsonRpcMessage> messages) : JsonRpcMessage
{
	/// <summary>
	/// Gets the immutable messages included in the batch payload.
	/// </summary>
	public ImmutableArray<JsonRpcMessage> Messages { get; } = !messages.IsDefault ? messages : throw new ArgumentException("The batch message collection must be initialized.", nameof(messages));
}
