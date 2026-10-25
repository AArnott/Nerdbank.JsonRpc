// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1201 // Elements should appear in the correct order

using MethodNaming;
using Nerdbank.JsonRpc;
using Nerdbank.JsonRpc.BrokeredServices;
using PolyType;

namespace BrokeredServices
{
    #region brokered-version-descriptors
    public static class VersionedGreeterServices
    {
        public static JsonRpcMessagePackDescriptor<IGreeter> V1 { get; } =
            JsonRpcMessagePackDescriptor.Create<IGreeter>(new("sample.versioned-greeter", new Version(1, 0)));

        public static JsonRpcMessagePackDescriptor<IGreeter> V2 { get; } =
            V1 with { Moniker = new(V1.Moniker.Name, new Version(2, 0)) };
    }
    #endregion

    internal static class BrokeredServiceVersioningExamples
    {
        internal static async Task<string[]> UseSharedImplementationAsync(CancellationToken cancellationToken)
        {
            #region brokered-version-shared-factory
            using BrokeredServiceContainer container = new();
            BrokeredServiceFactory<IGreeter> factory = (moniker, options, dependencies, token) =>
            {
                token.ThrowIfCancellationRequested();
                return new(new VersionAwareGreeter(moniker.Version
                    ?? throw new InvalidOperationException("A contract version is required.")));
            };
            container.Register(new(VersionedGreeterServices.V1.Moniker));
            using IDisposable v1 = container.Proffer(VersionedGreeterServices.V1, factory);
            container.Register(new(VersionedGreeterServices.V2.Moniker));
            using IDisposable v2 = container.Proffer(VersionedGreeterServices.V2, factory);
            #endregion

            return await CallBothVersionsAsync(container.GetServiceBroker(), cancellationToken);
        }

        internal static async Task<string[]> UseDistinctImplementationsAsync(CancellationToken cancellationToken)
        {
            #region brokered-version-distinct-factories
            using BrokeredServiceContainer container = new();
            container.Register(new(VersionedGreeterServices.V1.Moniker));
            container.Register(new(VersionedGreeterServices.V2.Moniker));
            using IDisposable v1 = container.Proffer(VersionedGreeterServices.V1, () => new LegacyGreeter());
            using IDisposable v2 = container.Proffer(VersionedGreeterServices.V2, () => new ModernGreeter());
            #endregion

            return await CallBothVersionsAsync(container.GetServiceBroker(), cancellationToken);
        }

        internal static async Task<string[]> CallBothVersionsAsync(IServiceBroker broker, CancellationToken cancellationToken)
        {
            #region brokered-version-client
            IGreeter legacy = await broker.GetProxyAsync(VersionedGreeterServices.V1, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Version 1 is unavailable.");
            await using IAsyncDisposable legacyLifetime = (IAsyncDisposable)legacy;
            IGreeter modern = await broker.GetProxyAsync(VersionedGreeterServices.V2, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Version 2 is unavailable.");
            await using IAsyncDisposable modernLifetime = (IAsyncDisposable)modern;
            return [
                await legacy.GreetAsync("Ada", cancellationToken),
                await modern.GreetAsync("Ada", cancellationToken),
            ];
            #endregion
        }
    }

    #region brokered-version-shared-implementation
    internal sealed class VersionAwareGreeter(Version requestedVersion) : IGreeter
    {
        public event Action<string>? Greeted;

        public ValueTask<string> GreetAsync(string person, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string greeting = requestedVersion == new Version(1, 0) ? $"Hello, {person}!" : $"Welcome, {person}!";
            this.Greeted?.Invoke(greeting);
            return new(greeting);
        }
    }
    #endregion

    #region brokered-version-distinct-implementations
    internal sealed class LegacyGreeter : IGreeter
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

    internal sealed class ModernGreeter : IGreeter
    {
        public event Action<string>? Greeted;

        public ValueTask<string> GreetAsync(string person, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string greeting = $"Welcome, {person}!";
            this.Greeted?.Invoke(greeting);
            return new(greeting);
        }
    }
    #endregion

    namespace FutureProofServices
    {
        #region FutureProofServices_1_0

        public static partial class MyHostServices
        {
            public static V1_0Descriptors V1_0 { get; } = V1_0Descriptors.Instance;

            public class V1_0Descriptors
            {
                internal static readonly V1_0Descriptors Instance = new();

                protected V1_0Descriptors()
                {
                }

                public virtual IJsonRpcDescriptor<IGreeter> Greeter { get; } =
                    JsonRpcMessagePackDescriptor.Create<IGreeter>(new("greeter", new Version(0, 1)));

                public virtual IJsonRpcDescriptor<ICalculator> Calculator { get; } =
                    JsonRpcUtf8Descriptor.Create<ICalculator>(new("calculator", new Version(0, 1)));
            }
        }

        #endregion
    }

    namespace FutureProofServices
    {
        #region FutureProofServices_2_0

        public static partial class MyHostServices
        {
            public static V2_0Descriptors V2_0 { get; } = V2_0Descriptors.Instance;

            public class V2_0Descriptors : V1_0Descriptors
            {
                internal static new readonly V2_0Descriptors Instance = new();

                protected V2_0Descriptors()
                {
                }

                public override IJsonRpcDescriptor<IGreeter> Greeter { get; } =
                    JsonRpcMessagePackDescriptor.Create<IGreeter>(new("greeter", new Version(0, 2)));

                public virtual IJsonRpcDescriptor<IWeatherService> Weather { get; } =
                    JsonRpcMessagePackDescriptor.Create<IWeatherService>(new("weather", new Version(0, 1)));
            }
        }
        #endregion

        [JsonRpcContract]
        [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
        public partial interface IWeatherService
        {
            ValueTask<string> GetWeatherAsync(string city, CancellationToken cancellationToken = default);
        }

        public class SomeClient
        {
            #region FutureProofClient
            // This client compiles against v1.0 of the host.
            // When the client targets v2 of the host, it changes just this one line and all client code consumes
            // the latest versions of the services.
            internal static readonly MyHostServices.V1_0Descriptors AppVersion = MyHostServices.V1_0;

            internal async Task GreetTheAppAsync(IServiceBroker serviceBroker, CancellationToken cancellationToken)
            {
                IGreeter? greeter = await serviceBroker.GetProxyAsync(AppVersion.Greeter, cancellationToken: cancellationToken);
                await using var lifetime = (IAsyncDisposable?)greeter;
                if (greeter is not null)
                {
                    await greeter.GreetAsync("World", cancellationToken);
                }
            }
            #endregion
        }
    }
}
