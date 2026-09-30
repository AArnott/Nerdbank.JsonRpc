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

/// <summary>Defines arithmetic operations available over JSON-RPC.</summary>
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
    /// <summary>Adds two integers.</summary>
    /// <param name="a">The first integer.</param>
    /// <param name="b">The second integer.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The sum of <paramref name="a"/> and <paramref name="b"/>.</returns>
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}
#endregion

/// <summary>Demonstrates creating and using JSON-RPC connections.</summary>
public static class Example
{
    #region create-connection

    /// <summary>Creates a JSON-RPC connection over a bidirectional stream.</summary>
    /// <param name="stream">The connected stream used to exchange messages with the remote party.</param>
    /// <param name="logger">The logger for transport and protocol events.</param>
    /// <returns>The connection, ready to configure and start.</returns>
    public static JsonRpc CreateConnection(Stream stream, ILogger logger)
    {
        IDuplexPipe pipe = stream.UsePipe();
        var channel = new JsonRpcMessagePackChannel(pipe, logger);
        return new JsonRpc(channel) { Logger = logger };
    }
    #endregion

    /// <summary>Exposes a calculator target until the remote party closes the connection.</summary>
    /// <param name="stream">The connected stream used to exchange messages with the remote party.</param>
    /// <param name="logger">The logger for transport and protocol events.</param>
    /// <returns>A task that completes when the JSON-RPC connection completes.</returns>
    public static async Task ExposeCalculatorAsync(Stream stream, ILogger logger)
    {
        #region expose-target
        using JsonRpc rpc = CreateConnection(stream, logger);
        rpc.AddRpcTarget<ICalculator>(new Calculator());
        rpc.Start();

        await rpc.Completion;
        #endregion
    }

    /// <summary>Invokes the calculator exposed by the remote party.</summary>
    /// <param name="stream">The connected stream used to exchange messages with the remote party.</param>
    /// <param name="logger">The logger for transport and protocol events.</param>
    /// <param name="cancellationToken">A token that cancels the remote request.</param>
    /// <returns>The sum returned by the remote calculator.</returns>
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

/// <summary>Provides arithmetic operations.</summary>
public sealed class Calculator : ICalculator
{
    /// <inheritdoc/>
    public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);
}
#endregion
