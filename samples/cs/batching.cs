// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

namespace Batching;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}

public static class Example
{
    public static async Task RunAsync(JsonRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);

        #region sending-batch
        JsonRpcBatch batch = rpc.CreateBatch();
        ICalculator batchedClient = batch.Attach<ICalculator>();

        ValueTask<int> first = batchedClient.AddAsync(1, 2, CancellationToken.None);
        ValueTask<int> second = batchedClient.AddAsync(3, 4, CancellationToken.None);

        await batch.SendAsync(CancellationToken.None);

        int firstSum = await first;
        int secondSum = await second;
        #endregion
    }
}
