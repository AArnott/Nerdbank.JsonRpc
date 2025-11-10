// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

[GenerateShape]
public partial class JsonRpcError : JsonRpcResponse
{
	[PropertyShape(Name = "code")]
	public required long Code { get; init; }

	[PropertyShape(Name = "message")]
	public required string Message { get; init; }

	[PropertyShape(Name = "data")]
	public MessagePackValue? Data { get; init; }
}
