// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

[MessagePackConverter(typeof(JsonRpcMessageConverter))]
[GenerateShape]
public abstract partial class JsonRpcMessage
{
	[PropertyShape(Name = "id")]
	public RequestId? Id { get; init; }

	[PropertyShape(IsRequired = true, Name = "jsonrpc")]
	public string Version { get; init; } = "2.0";
}
