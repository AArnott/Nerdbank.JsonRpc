// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IPositionalCalculator
{
	ValueTask<int> SubtractAsync(int a, int b, CancellationToken cancellationToken);

	ValueTask<int> EchoKeywordAsync(int @event, CancellationToken cancellationToken);
}

internal sealed class PositionalCalculator : IPositionalCalculator
{
	public ValueTask<int> SubtractAsync(int a, int b, CancellationToken cancellationToken) => new(a - b);

	public ValueTask<int> EchoKeywordAsync(int @event, CancellationToken cancellationToken) => new(@event);
}
