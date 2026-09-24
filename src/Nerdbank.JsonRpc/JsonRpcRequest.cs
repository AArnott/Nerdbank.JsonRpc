// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

[GenerateShape]
public partial class JsonRpcRequest : JsonRpcMessage
{
	[PropertyShape(Name = "method")]
	public required string Method { get; init; }

	[PropertyShape(Ignore = true)]
	public JsonRpcValue Arguments { get; init; }

	/// <summary>Gets the MessagePack representation used by the MessagePack envelope converter.</summary>
	[PropertyShape(Name = "params")]
	public RawMessagePack MessagePackArguments
	{
		get => this.Arguments.HasValue ? this.Arguments.AsMessagePack() : MsgPackValues.EmptyMap;
		init => this.Arguments = JsonRpcValue.FromMessagePack(value);
	}
}
