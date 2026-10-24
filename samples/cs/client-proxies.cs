// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

namespace ClientProxies;

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IExtendedCalculator : ICalculator
{
    ValueTask ResetAsync(CancellationToken cancellationToken);
}

#region proxy-interface-groups
[JsonRpcContract]
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
        Requires.NotNull(rpc);

        #region proxy-options
        ICalculator positionalClient = rpc.Attach<ICalculator>();
        #endregion

        #region named-arguments
        ICalculator namedClient = rpc.Attach<ICalculator>(new JsonRpcProxyOptions { UseNamedArguments = true });
        #endregion

        #region enabled-interfaces
        IGroupedCalculator client = rpc.Attach<IGroupedCalculator>(new JsonRpcProxyOptions
        {
            EnabledInterfaces = [],
        });
        IGroupedCalculator? service = client.As<IGroupedCalculator>(); // Always enabled.
        ICalculator? calculator = client.As<ICalculator>(); // Inherited interfaces are also enabled.
        IResettableCalculator? resettable = client.As<IResettableCalculator>(); // Null.
        bool canReset = client.Is(typeof(IResettableCalculator)); // False.
        #endregion
    }
}
