// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

namespace Nerdbank.JsonRpc.Tests;

[GenerateShape]
public partial class OpenRpcRecursivePayload
{
	public required string Name { get; init; }

	public OpenRpcRecursivePayload? Next { get; init; }
}
