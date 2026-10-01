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
        (SharedMemoryDuplexPipe clientPipe, SharedMemoryDuplexPipe serverPipe) = await ConnectSharedMemoryEndpointsAsync(name, cancellationToken);
        using (clientPipe)
        using (serverPipe)
        {
            using JsonRpc server = new(new JsonRpcMessagePackChannel(serverPipe));
            server.AddRpcTarget<ICalculator>(new Calculator());
            server.Start();

            using JsonRpc client = new(new JsonRpcMessagePackChannel(clientPipe));
            client.Start();
            return await client.Attach<ICalculator>().AddAsync(1, 2, cancellationToken);
        }
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

    private static async Task<(SharedMemoryDuplexPipe Client, SharedMemoryDuplexPipe Server)> ConnectSharedMemoryEndpointsAsync(string name, CancellationToken cancellationToken)
    {
        using CancellationTokenSource setupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<SharedMemoryDuplexPipe> listener = SharedMemoryDuplexPipe.ListenAsync(name, cancellationToken: setupCancellation.Token);
        SharedMemoryDuplexPipe client;
        try
        {
            client = await SharedMemoryDuplexPipe.ConnectAsync(name, cancellationToken: setupCancellation.Token);
        }
        catch (Exception connectionException)
        {
            await setupCancellation.CancelAsync();
            try
            {
                (await listener).Dispose();
            }
            catch (OperationCanceledException) when (setupCancellation.IsCancellationRequested)
            {
            }
            catch (Exception listenerException)
            {
                throw new AggregateException("Shared-memory connection setup and listener cleanup both failed.", connectionException, listenerException);
            }

            throw;
        }

        try
        {
            return (client, await listener);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

#region rpc-target
public sealed class Calculator : ICalculator
{
    public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);
}
#endregion
