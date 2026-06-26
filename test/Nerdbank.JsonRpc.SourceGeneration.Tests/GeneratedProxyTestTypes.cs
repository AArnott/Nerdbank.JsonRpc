// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICalculator
{
	ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}

internal sealed class Calculator : ICalculator
{
	public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);
}
