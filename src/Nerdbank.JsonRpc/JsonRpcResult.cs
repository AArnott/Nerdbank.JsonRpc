// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

[GenerateShape]
public partial class JsonRpcResult : JsonRpcResponse
{
	[PropertyShape(Name = "result")]
	public required RawMessagePack Result { get; init; } = MsgPackValues.EmptyMap;
}
