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

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IExtendedCalculator : ICalculator
{
    ValueTask ResetAsync(CancellationToken cancellationToken);
}

#region proxy-interface-groups
[GenerateJsonRpcProxy]
[JsonRpcProxyInterfaceGroup]
[JsonRpcProxyInterfaceGroup(typeof(IResettableCalculator))]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IGroupedCalculator : ICalculator
{
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IResettableCalculator
{
    ValueTask ResetAsync(CancellationToken cancellationToken);
}
#endregion

public static class ProxyOptions
{
    public static void Run(JsonRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);

        #region proxy-options
        ICalculator positionalClient = rpc.Attach<ICalculator>();
        #endregion

        #region named-arguments
        ICalculator namedClient = rpc.Attach<ICalculator>(new JsonRpcProxyOptions { UseNamedArguments = true });
        #endregion

        #region enabled-interfaces
        IExtendedCalculator client = rpc.Attach<IExtendedCalculator>(new JsonRpcProxyOptions
        {
            EnabledInterfaces = [typeof(ICalculator)],
        });
        ICalculator? calculator = client.As<ICalculator>(); // Enabled.
        IExtendedCalculator? extended = client.As<IExtendedCalculator>(); // Null, despite the CLR interface.
        bool canReset = client.Is(typeof(IExtendedCalculator)); // False.
        #endregion
    }
}
