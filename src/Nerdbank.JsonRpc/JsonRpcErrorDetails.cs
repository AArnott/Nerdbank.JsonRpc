// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

public class JsonRpcErrorDetails
{
	[PropertyShape(Name = "code")]
	public required long Code { get; init; }

	[PropertyShape(Name = "message")]
	public required string Message { get; init; }

	[PropertyShape(Ignore = true)]
	public JsonRpcValue? Data { get; init; }

	/// <summary>Gets the optional MessagePack representation used by the MessagePack envelope converter.</summary>
	[PropertyShape(Name = "data")]
	public RawMessagePack? MessagePackData
	{
		get => this.Data is JsonRpcValue data ? data.AsMessagePack() : null;
		init => this.Data = value.HasValue ? JsonRpcValue.FromMessagePack(value.Value) : null;
	}
}
