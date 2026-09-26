// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[RpcMarshalable(CallScopedLifetime = true)]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICallScopedCounter
{
	Task<int> IncrementAsync(CancellationToken cancellationToken);
}
