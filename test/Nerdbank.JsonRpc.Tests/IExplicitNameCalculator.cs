// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

/// <summary>A JSON-RPC test contract with an explicit <see cref="MethodShapeAttribute.Name"/> that must bypass method name transforms.</summary>
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IExplicitNameCalculator
{
	[MethodShape(Name = "custom.add")]
	ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}
