// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
[JsonRpcProxyInterfaceGroup]
[JsonRpcProxyInterfaceGroup(typeof(IAotMultiply))]
internal partial interface IAotCalculator
{
	event EventHandler<int> Changed;

#pragma warning disable SA1313 // Legal parameter names deliberately collide with generated implementation locals.
	ValueTask<int> AddAsync(int __lease, int __current, CancellationToken __enumerationCancellation);

	ValueTask<IDisposable?> ObserveAsync(IObserver<int> __target, int __subscription, CancellationToken __cancellationToken);
#pragma warning restore SA1313
}
