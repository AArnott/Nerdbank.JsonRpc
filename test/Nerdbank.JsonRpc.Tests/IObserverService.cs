// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IObserverService
{
	Task<IDisposable> SubscribeAsync(IObserver<int> observer, CancellationToken cancellationToken);

	Task<IObserver<int>> GetObserverAsync(CancellationToken cancellationToken);

	Task<bool> IsOwnedObserverAsync(IObserver<int> observer, CancellationToken cancellationToken);

	Task FailAfterReceivingAsync(IObserver<int> observer, CancellationToken cancellationToken);
}
