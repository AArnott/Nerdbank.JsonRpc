// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using ClientProxies;
using Nerdbank.JsonRpc;
using PolyType;

namespace Discovery;

#region discovery-contract
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IDiscovery
{
    ValueTask<string[]> GetCapabilitiesAsync(CancellationToken cancellationToken);
}

public static class CapabilityNames
{
    public const string Reset = "calculator.reset.v1";
}
#endregion

public static class Example
{
    #region discovery-server
    public static void StartServer(JsonRpc rpc, IGroupedCalculator calculator)
    {
        Requires.NotNull(rpc);
        Requires.NotNull(calculator);

        List<string> capabilities = [];
        rpc.AddRpcTarget<IGroupedCalculator>(calculator);
        if (calculator is IResettableCalculator resettable)
        {
            rpc.AddRpcTarget<IResettableCalculator>(resettable);
            capabilities.Add(CapabilityNames.Reset);
        }

        rpc.AddRpcTarget<IDiscovery>(new DiscoveryService(capabilities.ToArray()));
        rpc.Start();
    }
    #endregion

    #region discovery-client
    public static async Task<IGroupedCalculator> ConnectAsync(JsonRpc rpc, CancellationToken cancellationToken)
    {
        Requires.NotNull(rpc);

        IDiscovery discovery = rpc.Attach<IDiscovery>();
        rpc.Start();
        string[] capabilities = await discovery.GetCapabilitiesAsync(cancellationToken);

        List<Type> enabledInterfaces = [];
        if (capabilities.Contains(CapabilityNames.Reset, StringComparer.Ordinal))
        {
            enabledInterfaces.Add(typeof(IResettableCalculator));
        }

        IGroupedCalculator calculator = rpc.Attach<IGroupedCalculator>(new JsonRpcProxyOptions
        {
            EnabledInterfaces = enabledInterfaces,
        });
        if (calculator.As<IResettableCalculator>() is { } resettable)
        {
            await resettable.ResetAsync(cancellationToken);
        }

        return calculator;
    }
    #endregion

    #region discovery-target
    private sealed class DiscoveryService(string[] capabilities) : IDiscovery
    {
        public ValueTask<string[]> GetCapabilitiesAsync(CancellationToken cancellationToken) => new(capabilities);
    }
    #endregion
}
