// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using PolyType;

namespace Nerdbank.JsonRpc.Tests;

[GenerateShape(Kind = TypeShapeKind.None, IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial class OpenRpcTarget
{
	[Description("Adds two values.")]
	public int Add(int left, int right = 0) => left + right;

	public string EchoId(RequestId id) => id.ToString();

	public OpenRpcPayload EchoPayload(OpenRpcPayload payload) => payload;

	public JsonRpcResult EchoRequest(JsonRpcRequest request) => new() { Id = request.Id ?? throw new ArgumentException("An ID is required.", nameof(request)) };
}
