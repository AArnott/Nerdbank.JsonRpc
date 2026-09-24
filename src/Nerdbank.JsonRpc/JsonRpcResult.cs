// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

[GenerateShape]
public partial class JsonRpcResult : JsonRpcResponse
{
	[PropertyShape(Ignore = true)]
	public JsonRpcValue Result { get; init; }

	/// <summary>Gets the MessagePack representation used by the MessagePack envelope converter.</summary>
	[PropertyShape(IsRequired = true, Name = "result")]
	public RawMessagePack MessagePackResult
	{
		get => this.Result.AsMessagePack();
		init => this.Result = JsonRpcValue.FromMessagePack(value);
	}
}
