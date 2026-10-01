// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using Nerdbank.JsonRpc;
using PolyType;

namespace SharedMemory;

#pragma warning disable SA1649 // The sample file name matches its documentation topic rather than its type.

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IWorker
{
    ValueTask<int> ProcessAsync(byte[] payload, CancellationToken cancellationToken);
}

public static class Example
{
    public static async Task<JsonRpc> ServeAsync(string channelName, ILogger logger, CancellationToken cancellationToken)
    {
        #region listen
        SharedMemoryDuplexPipe pipe = await SharedMemoryDuplexPipe.ListenAsync(channelName, cancellationToken: cancellationToken);
        JsonRpc rpc = new(new JsonRpcMessagePackChannel(pipe, logger));
        rpc.AddRpcTarget<IWorker>(new Worker());
        rpc.Start();
        #endregion

        return rpc;
    }

    public static async Task<int> ConnectAsync(string channelName, ILogger logger, CancellationToken cancellationToken)
    {
        #region connect
        SharedMemoryDuplexPipe pipe = await SharedMemoryDuplexPipe.ConnectAsync(channelName, cancellationToken: cancellationToken);
        using JsonRpc rpc = new(new JsonRpcMessagePackChannel(pipe, logger));
        rpc.Start();

        IWorker worker = rpc.Attach<IWorker>();
        int result = await worker.ProcessAsync(new byte[256 * 1024], cancellationToken);
        #endregion

        return result;
    }

    public static async Task<(SharedMemoryDuplexPipe Client, SharedMemoryDuplexPipe Server)> CreatePairAsync(CancellationToken cancellationToken)
    {
        #region options
        SharedMemoryPipeOptions options = new()
        {
            // Large enough that typical messages are written in place rather than copied.
            Capacity = 4 * 1024 * 1024,
        };
        (SharedMemoryDuplexPipe client, SharedMemoryDuplexPipe server) = await SharedMemoryDuplexPipe.CreatePairAsync(options, cancellationToken);
        #endregion

        return (client, server);
    }
}

internal sealed class Worker : IWorker
{
    public ValueTask<int> ProcessAsync(byte[] payload, CancellationToken cancellationToken) => new(payload.Length);
}
