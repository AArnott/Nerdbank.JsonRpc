// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateJsonRpcProxy(UseNamedArguments = true)]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface INamedCalculator
{
	ValueTask<int> SubtractAsync(int a, int b, CancellationToken cancellationToken);
}

internal sealed class NamedCalculator : INamedCalculator
{
	public ValueTask<int> SubtractAsync(int a, int b, CancellationToken cancellationToken) => new(a - b);
}
