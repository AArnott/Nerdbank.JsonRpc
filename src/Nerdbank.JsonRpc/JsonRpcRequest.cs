// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

[GenerateShape]
public partial class JsonRpcRequest : JsonRpcMessage
{
	[PropertyShape(Name = "method")]
	public required string Method { get; init; }

	[PropertyShape(Ignore = true)]
	public JsonRpcValue Arguments { get; init; }
}
