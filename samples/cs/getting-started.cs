// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Nerdbank.JsonRpc;
using Nerdbank.Streams;
using PolyType;

namespace GettingStarted;

#pragma warning disable SA1649 // The sample file name matches its documentation topic rather than its type.

#region rpc-contract
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}
#endregion

public static class Example
{
    #region create-connection
    public static JsonRpc CreateConnection(Stream stream, ILogger logger)
    {
        IDuplexPipe pipe = stream.UsePipe();
        var channel = new JsonRpcMessagePackChannel(pipe);
        return new JsonRpc(channel) { Logger = logger };
    }
    #endregion

    #region shared-memory-connection
    public static async Task<int> CallOverSharedMemoryAsync(CancellationToken cancellationToken)
    {
        string name = Guid.NewGuid().ToString("N");
        Task<SharedMemoryDuplexPipe> listener = SharedMemoryDuplexPipe.ListenAsync(name, cancellationToken: cancellationToken);
        using SharedMemoryDuplexPipe clientPipe = await SharedMemoryDuplexPipe.ConnectAsync(name, cancellationToken: cancellationToken);
        using SharedMemoryDuplexPipe serverPipe = await listener;

        using JsonRpc server = new(new JsonRpcMessagePackChannel(serverPipe));
        server.AddRpcTarget<ICalculator>(new Calculator());
        server.Start();

        using JsonRpc client = new(new JsonRpcMessagePackChannel(clientPipe));
        client.Start();
        return await client.Attach<ICalculator>().AddAsync(1, 2, cancellationToken);
    }
    #endregion

    public static async Task ExposeCalculatorAsync(Stream stream, ILogger logger)
    {
        #region expose-target
        using JsonRpc rpc = CreateConnection(stream, logger);
        rpc.AddRpcTarget<ICalculator>(new Calculator());
        rpc.Start();

        await rpc.Completion;
        #endregion
    }

    public static async Task<int> CallCalculatorAsync(Stream stream, ILogger logger, CancellationToken cancellationToken)
    {
        #region attach-proxy
        using JsonRpc rpc = CreateConnection(stream, logger);
        rpc.Start();

        ICalculator calculator = rpc.Attach<ICalculator>();
        int sum = await calculator.AddAsync(1, 2, cancellationToken);
        #endregion

        return sum;
    }
}

#region rpc-target
public sealed class Calculator : ICalculator
{
    public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);
}
#endregion
