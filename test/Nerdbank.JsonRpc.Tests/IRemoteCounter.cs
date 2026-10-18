// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[RpcMarshalable]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IRemoteCounter : IDisposable
{
	Task<int> IncrementAsync(CancellationToken cancellationToken);
}
