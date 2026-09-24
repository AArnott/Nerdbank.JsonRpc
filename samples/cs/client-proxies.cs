// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

namespace ClientProxies;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}

#region named-arguments
[GenerateJsonRpcProxy(UseNamedArguments = true)]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface INamedCalculator
{
    ValueTask<int> SubtractAsync(int a, int b, CancellationToken cancellationToken);
}
#endregion

public static class ProxyOptions
{
    public static void Run(JsonRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);

        #region proxy-options
        ICalculator client = rpc.Attach<ICalculator>(new JsonRpcProxyOptions());
        #endregion
    }
}
