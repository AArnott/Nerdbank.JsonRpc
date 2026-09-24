// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Nerdbank.JsonRpc;
using PolyType;
using static GettingStarted.CompilationSupport;
using IBatchedCalculator = GettingStarted.GeneratedClientProxy.ICalculator;

namespace GettingStarted.ServerSetup
{
    #region server-setup
    [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface ICalculator
    {
        ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
    }

    public sealed class Calculator : ICalculator
    {
        public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);
    }
    #endregion
}

namespace GettingStarted.GeneratedClientProxy
{
    #region generated-client-proxy
    [GenerateJsonRpcProxy]
    [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface ICalculator
    {
        ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
    }
    #endregion
}

namespace GettingStarted.JsonEncoding
{
    public static class Example
    {
        public static JsonRpc Start(IDuplexPipe pipe, ILogger logger)
        {
            #region json-encoding
            var configured = new Nerdbank.Json.JsonSerializer();
            var channel = new JsonRpcJsonChannel(pipe, configured, JsonRpcJsonFraming.ContentLength, logger);
            var rpc = new JsonRpc(channel) { Serializer = configured };
            rpc.Start();
            #endregion

            return rpc;
        }
    }
}

namespace GettingStarted.AttachProxy
{
    [GenerateJsonRpcProxy]
    [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface ICalculator
    {
        ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
    }

    public static class Example
    {
        public static async Task RunAsync()
        {
            #region attach-proxy
            JsonRpc rpc = new(channel);
            rpc.Start();

            ICalculator client = rpc.Attach<ICalculator>();
            int sum = await client.AddAsync(1, 2, CancellationToken.None);
            #endregion
        }
    }
}

namespace GettingStarted.ProxyOptions
{
    [GenerateJsonRpcProxy]
    [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface ICalculator
    {
        ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
    }

    public static class Example
    {
        public static void Run()
        {
            #region proxy-options
            ICalculator client = rpc.Attach<ICalculator>(new JsonRpcProxyOptions());
            #endregion
        }
    }
}

namespace GettingStarted.NamedArguments
{
    #region named-arguments
    [GenerateJsonRpcProxy(UseNamedArguments = true)]
    [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface ICalculator
    {
        ValueTask<int> SubtractAsync(int a, int b, CancellationToken cancellationToken);
    }
    #endregion
}

namespace GettingStarted.SendingBatch
{
    public static class Example
    {
        public static async Task RunAsync()
        {
            #region sending-batch
            JsonRpcBatch batch = rpc.CreateBatch();
            IBatchedCalculator batchedClient = batch.Attach<IBatchedCalculator>();

            ValueTask<int> first = batchedClient.AddAsync(1, 2, CancellationToken.None);
            ValueTask<int> second = batchedClient.AddAsync(3, 4, CancellationToken.None);

            await batch.SendAsync(CancellationToken.None);

            int firstSum = await first;
            int secondSum = await second;
            #endregion
        }
    }
}

namespace GettingStarted
{
    internal static class CompilationSupport
    {
        internal static readonly Channel<JsonRpcMessage> channel = Channel.CreateUnbounded<JsonRpcMessage>();
        internal static JsonRpc rpc = null!;
    }
}
