// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.Threading;
using Nerdbank.JsonRpc;
using Nerdbank.JsonRpc.BrokeredServices;
using Nerdbank.Streams;
using PolyType;
using IAsyncDisposable = System.IAsyncDisposable;

namespace BrokeredServices;

#region brokered-contract
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IGreeter
{
    event Action<string> Greeted;

    ValueTask<string> GreetAsync(string person, CancellationToken cancellationToken = default);
}

#endregion

public static class BrokeredServiceExamples
{
    #region brokered-descriptor
    public static IJsonRpcDescriptor<IGreeter> Greeter { get; } =
        JsonRpcMessagePackDescriptor.Create<IGreeter>(new("sample.greeter", new Version(1, 0)));
    #endregion

    public static async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        #region brokered-registration
        using BrokeredServiceContainer container = new();
        container.Register(new(Greeter.Moniker));
        using IDisposable registration = container.Proffer(Greeter, () => new Greeter());
        IServiceBroker broker = container.GetServiceBroker();
        #endregion

        #region brokered-acquisition
        IGreeter greeter = await broker.GetProxyAsync(Greeter, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The greeting service is unavailable.");
        await using IAsyncDisposable lifetime = (IAsyncDisposable)greeter;
        #endregion

        return await greeter.GreetAsync("Ada", cancellationToken);
    }

    public static async Task<string> CachedAsync(IServiceBroker broker, CancellationToken cancellationToken)
    {
        #region brokered-cache
        using ServiceBrokerClient client = new(broker);
        using ServiceBrokerClient.Rental<IGreeter> rental = await client.GetProxyAsync(Greeter, cancellationToken: cancellationToken);
        IGreeter greeter = rental.Proxy ?? throw new InvalidOperationException("The greeting service is unavailable.");
        return await greeter.GreetAsync("Ada", cancellationToken);
        #endregion
    }

    public static async Task<string> ResilientAsync(IServiceBroker broker, CancellationToken cancellationToken)
    {
        #region brokered-resilience
        await using ResilientServiceProxy<IGreeter> owner =
            await broker.GetResilientProxyAsync(Greeter, cancellationToken: cancellationToken);
        return await owner.Proxy.GreetAsync("Ada", cancellationToken);
        #endregion
    }

    public static async Task ServeBrokerAsync(IServiceBroker broker, string endpoint, CancellationToken cancellationToken)
    {
        #region brokered-ipc-server
        await using IpcRelayServiceBroker relay = new(broker);
        JsonRpcDescriptor<IRemoteServiceBroker> control = FrameworkServices.RemoteServiceBroker;
        await using IIpcServer listener = IpcServer.Create(
            async (stream, token) =>
            {
                using JsonRpc rpc = control.CreateConnection(stream.UsePipe(cancellationToken: token));
                control.AddRpcTarget(rpc, relay, []);
                rpc.Start();
                await rpc.Completion.WithCancellation(token);
            },
            new() { Name = endpoint });
        await listener.Completion.WithCancellation(cancellationToken);
        #endregion
    }

    public static async Task<string> CallRemoteAsync(string endpoint, CancellationToken cancellationToken)
    {
        #region brokered-ipc-client
        using Stream stream = await IpcClient.ConnectAsync(endpoint, cancellationToken);
        using RemoteServiceBroker broker = await RemoteServiceBroker.ConnectAsync(stream.UsePipe(cancellationToken: cancellationToken), cancellationToken: cancellationToken);
        IGreeter greeter = await broker.GetProxyAsync(Greeter, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The greeting service is unavailable.");
        await using IAsyncDisposable lifetime = (IAsyncDisposable)greeter;
        return await greeter.GreetAsync("Ada", cancellationToken);
        #endregion
    }

    internal static ValueTask LoadGreeterAsync(ServiceMoniker moniker, IBrokeredServiceContainer container)
    {
        #region brokered-lazy-proffer
        // A host can load its implementation assembly here before proffering the factory.
        container.Proffer(Greeter, () => new Greeter());
        return default;
        #endregion
    }
}

internal static class BrokeredServiceCatalog
{
    internal static void RegisterOnDemand(IBrokeredServiceContainer container)
    {
        #region brokered-lazy-registration
        // A host's loader can locate implementation code without referencing contract types here.
        container.Register(new(
            new("sample.greeter", new Version(1, 0)),
            BrokeredServiceExamples.LoadGreeterAsync,
            ServiceAudience.Local | ServiceAudience.PublicSdk));
        #endregion
    }

    internal static IServiceBroker GetPublicRelayBroker(IBrokeredServiceContainer container)
    {
        #region brokered-audience
        // Only public SDK services that support same-machine RPC are exposed by this view.
        return container.GetServiceBroker(ServiceAudience.Local | ServiceAudience.PublicSdk);
        #endregion
    }
}

#region brokered-implementation
internal sealed class Greeter : IGreeter
{
    public event Action<string>? Greeted;

    public ValueTask<string> GreetAsync(string person, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string greeting = $"Hello, {person}!";
        this.Greeted?.Invoke(greeting);
        return new(greeting);
    }
}
#endregion
