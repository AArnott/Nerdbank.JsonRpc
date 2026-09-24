// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

/// <summary>A cancellation-aware JSON-RPC test contract.</summary>
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICancellableTarget
{
	ValueTask<int> WaitAsync(CancellationToken cancellationToken);
}
