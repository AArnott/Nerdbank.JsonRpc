// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IPositionalCalculator
{
	ValueTask<int> SubtractAsync(int a, int b, CancellationToken cancellationToken);
}

internal sealed class PositionalCalculator : IPositionalCalculator
{
	public ValueTask<int> SubtractAsync(int a, int b, CancellationToken cancellationToken) => new(a - b);
}
