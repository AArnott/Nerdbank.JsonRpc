// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

namespace Batching;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICalculator
{
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}

internal static class Example
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

    public static async Task RunDynamicAsync(JsonRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);

        #region dynamic-batch-results
        using JsonRpcBatch batch = rpc.CreateBatch();
        ICalculator batchedClient = batch.Attach<ICalculator>();

        Task<int>[] results =
        [
            batchedClient.AddAsync(1, 2, CancellationToken.None).AsTask(),
            batchedClient.AddAsync(3, 4, CancellationToken.None).AsTask(),
        ];

        await batch.SendAsync(CancellationToken.None);
        int[] sums = await Task.WhenAll(results);
        #endregion
    }
}
