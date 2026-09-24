// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

public class JsonRpcErrorDetails
{
	[PropertyShape(Name = "code")]
	public required long Code { get; init; }

	[PropertyShape(Name = "message")]
	public required string Message { get; init; }

	[PropertyShape(Ignore = true)]
	public JsonRpcValue? Data { get; init; }
}
