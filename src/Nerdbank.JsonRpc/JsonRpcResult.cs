// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

[GenerateShape]
public partial class JsonRpcResult : JsonRpcResponse
{
	[PropertyShape(Ignore = true)]
	public JsonRpcValue Result { get; init; }
}
