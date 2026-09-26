// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IRemoteCounterService
{
	Task<IRemoteCounter> GetCounterAsync(CancellationToken cancellationToken);

	Task<bool> IsSameCounterAsync(IRemoteCounter counter, CancellationToken cancellationToken);
}
