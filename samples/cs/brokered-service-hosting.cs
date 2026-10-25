// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nerdbank.JsonRpc;
using Nerdbank.JsonRpc.BrokeredServices;
using PolyType;

namespace BrokeredServices;

#region brokered-optional-contract
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
[JsonRpcProxyInterfaceGroup]
[JsonRpcProxyInterfaceGroup(typeof(IResettableGreeter))]
[JsonRpcProxyInterfaceGroup(typeof(IGreeterStatus))]
[JsonRpcProxyInterfaceGroup(typeof(IResettableGreeter), typeof(IGreeterStatus))]
public partial interface IOptionalGreeter
{
    ValueTask<string> GreetAsync(string person, CancellationToken cancellationToken = default);
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IResettableGreeter
{
    ValueTask ResetAsync(CancellationToken cancellationToken = default);
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IGreeterStatus
{
    ValueTask<string> GetStatusAsync(CancellationToken cancellationToken = default);
}
#endregion

#region brokered-optional-descriptor
public static class OptionalGreeterServices
{
    public static ServiceInterface<IResettableGreeter> Reset { get; } = ServiceInterface.Create<IResettableGreeter>("sample.reset.v1");

    public static ServiceInterface<IGreeterStatus> Status { get; } = ServiceInterface.Create<IGreeterStatus>("sample.status.v1");

    public static IJsonRpcDescriptor<IOptionalGreeter> Greeter { get; } =
        JsonRpcMessagePackDescriptor.Create<IOptionalGreeter>(new("sample.optional-greeter", new Version(1, 0))) with
        {
            AdditionalInterfaces = [Reset, Status],
        };
}
#endregion

internal static class BrokeredServiceHostingExamples
{
    #region brokered-diagnostics
    internal static BrokeredServiceContainer CreateLoggedContainer(ILoggerFactory loggerFactory)
        => new() { LoggerFactory = loggerFactory };

    internal static ServiceRpcContext CreateLoggedConnectionContext(ILoggerFactory loggerFactory)
        => new()
        {
            Logger = loggerFactory.CreateLogger<JsonRpc>(),
            LoggerFactory = loggerFactory,
        };
    #endregion

    internal static async Task<string> RegisterFromManifestAsync(CancellationToken cancellationToken)
    {
        #region brokered-serialized-manifest
        const string manifest = """
            {
              "name": "sample.optional-greeter",
              "version": "1.0",
              "additionalInterfaceIds": ["sample.reset.v1"]
            }
            """;
        #endregion

        using BrokeredServiceContainer container = new();
        IDisposable? proffer = null;
        RegisterManifest(container, manifest, (_, host) =>
        {
            proffer = host.Proffer(OptionalGreeterServices.Greeter, () => new ResettableGreeter());
            return default;
        });
        try
        {
            return await UseOptionalInterfacesAsync(container.GetServiceBroker(), cancellationToken);
        }
        finally
        {
            proffer?.Dispose();
        }
    }

    #region brokered-serialized-registration
    internal static void RegisterManifest(IBrokeredServiceContainer container, string manifest, BrokeredServiceLoader loadImplementation)
    {
        using JsonDocument document = JsonDocument.Parse(manifest);
        JsonElement service = document.RootElement;
        string name = service.GetProperty("name").GetString()
            ?? throw new JsonException("A service name is required.");
        string version = service.GetProperty("version").GetString()
            ?? throw new JsonException("A service version is required.");
        string[] capabilityIds = service.GetProperty("additionalInterfaceIds").EnumerateArray()
            .Select(id => id.GetString() ?? throw new JsonException("A capability identifier cannot be null."))
            .ToArray();
        container.Register(new(
            new(name, Version.Parse(version)),
            loadImplementation,
            additionalInterfaceIds: capabilityIds));
    }
    #endregion

    internal static async Task<string> LiveRegistrationAsync(CancellationToken cancellationToken)
    {
        #region brokered-live-registration
        using BrokeredServiceContainer container = new();
        IServiceBroker broker = container.GetServiceBroker();
        container.Register(new(BrokeredServiceExamples.Greeter.Moniker));
        using IDisposable proffer = container.Proffer(BrokeredServiceExamples.Greeter, () => new Greeter());
        IGreeter proxy = await broker.GetProxyAsync(BrokeredServiceExamples.Greeter, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The greeting service is unavailable.");
        await using IAsyncDisposable lifetime = (IAsyncDisposable)proxy;
        container.Unregister(BrokeredServiceExamples.Greeter.Moniker);

        // New acquisitions are unavailable, but existing clients retain their activations.
        return await proxy.GreetAsync("Ada", cancellationToken);
        #endregion
    }

    internal static async Task<string> LoadOnFirstRequestAsync(CancellationToken cancellationToken)
    {
        #region brokered-lazy-client
        using OnDemandGreeterHost host = new();
        IGreeter proxy = await host.Broker.GetProxyAsync(
            BrokeredServiceExamples.Greeter, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The greeting service is unavailable.");
        await using IAsyncDisposable lifetime = (IAsyncDisposable)proxy;
        return await proxy.GreetAsync("Ada", cancellationToken);
        #endregion
    }

    internal static async Task<string> UseOptionalInterfacesAsync(IServiceBroker broker, CancellationToken cancellationToken)
    {
        #region brokered-optional-client
        IOptionalGreeter proxy = await broker.GetProxyAsync(
            OptionalGreeterServices.Greeter, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The greeting service is unavailable.");
        await using IAsyncDisposable lifetime = (IAsyncDisposable)proxy;
        if (proxy.As<IResettableGreeter>() is { } reset)
        {
            await reset.ResetAsync(cancellationToken);
        }

        IGreeterStatus? status = proxy.As<IGreeterStatus>();
        string description = status is null ? "Status is not supported." : await status.GetStatusAsync(cancellationToken);
        return $"{await proxy.GreetAsync("Ada", cancellationToken)} {description}";
        #endregion
    }

    internal static async Task<bool> QueryResetSupportAsync(IServiceBroker broker, CancellationToken cancellationToken)
    {
        #region brokered-optional-metadata
        IReadOnlyList<string>? capabilities = await broker.GetCapabilitiesAsync(
            OptionalGreeterServices.Greeter.Moniker, cancellationToken);
        return capabilities?.Contains(OptionalGreeterServices.Reset.Id) is true;
        #endregion
    }

    internal static IDisposable ProfferOptionalGreeter(IBrokeredServiceContainer container)
    {
        #region brokered-optional-offering
        container.Register(new(OptionalGreeterServices.Greeter.Moniker, additionalInterfaceIds: [OptionalGreeterServices.Reset.Id]));
        return container.Proffer(OptionalGreeterServices.Greeter, () => new ResettableGreeter());
        #endregion
    }

    internal static JsonRpcDescriptor<IGreeter> CreateMultiplexedDescriptor()
    {
        #region brokered-multiplexing
        return JsonRpcMessagePackDescriptor.Create<IGreeter>(new("sample.streaming-greeter", new Version(1, 0))) with
        {
            MultiplexingStreamOptions = new() { ProtocolMajorVersion = 3 },
        };
        #endregion
    }
}

#region brokered-lazy-host
internal sealed class OnDemandGreeterHost : IDisposable
{
    private readonly BrokeredServiceContainer container = new();
    private IDisposable? offering;

    internal OnDemandGreeterHost()
    {
        this.container.Register(new(
            new("sample.greeter", new Version(1, 0)),
            this.LoadAsync));
    }

    internal IServiceBroker Broker => this.container.GetServiceBroker();

    public void Dispose()
    {
        this.offering?.Dispose();
        this.container.Dispose();
    }

    private ValueTask LoadAsync(ServiceMoniker moniker, IBrokeredServiceContainer host)
    {
        // Load an implementation module here if it is in a separate assembly.
        this.offering = host.Proffer(BrokeredServiceExamples.Greeter, () => new Greeter());
        return default;
    }
}
#endregion

#region brokered-optional-implementation
internal sealed class ResettableGreeter : IOptionalGreeter, IResettableGreeter
{
    private int greetings;

    public ValueTask<string> GreetAsync(string person, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new($"Hello, {person}! Greeting #{Interlocked.Increment(ref this.greetings)}.");
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref this.greetings, 0);
        return default;
    }
}
#endregion
