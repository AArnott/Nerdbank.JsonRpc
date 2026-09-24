// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

namespace GettingStarted;

#region generated-client-proxy
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}
#endregion

#region server-setup
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IServerCalculator
{
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}

public sealed class Calculator : IServerCalculator
{
    public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);
}
#endregion

public sealed class Example
{
    public async Task RunAsync(JsonRpcPipeChannel channel)
    {
        #region attach-proxy
        JsonRpc rpc = new(channel);
        rpc.Start();

        ICalculator client = rpc.Attach<ICalculator>();
        int sum = await client.AddAsync(1, 2, CancellationToken.None);
        #endregion
    }
}
