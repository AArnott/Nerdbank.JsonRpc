// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Nerdbank.JsonRpc.BrokeredServiceContracts;
using Nerdbank.JsonRpc.BrokeredServices;
using Nerdbank.Streams;
using PolyType;
using PolyType.Abstractions;

public class BrokeredServiceTests : TestBase
{
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task AsyncDisposableContractReleasesRpcConnection(JsonRpcEncoding encoding)
	{
		JsonRpcDescriptor<IAsyncLifetimeService> descriptor = CreateDescriptor<IAsyncLifetimeService>(new("async.lifetime"), encoding);
		(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = descriptor.CreateConnection(client);
		using JsonRpc serverRpc = descriptor.CreateConnection(server);
		clientRpc.Start();
		serverRpc.Start();
		await using IAsyncLifetimeService proxy = descriptor.CreateProxy(clientRpc, []);
		await proxy.DisposeAsync();
		await clientRpc.Completion.WithCancellation(this.TimeoutToken);
		await serverRpc.Completion.WithCancellation(this.TimeoutToken);
	}

	[Test]
	public async Task AsyncDisposableContractReleasesLocalContextAndEvents()
	{
		AsyncLifetimeService target = new();
		ServiceProxyContext context = ServiceProxyContext.Own(target);
		await using IAsyncLifetimeService proxy = JsonRpcUtf8Descriptor.Create<IAsyncLifetimeService>(new("async.lifetime")).CreateLocalProxy(context, []);
		proxy.Changed += (_, _) => { };
		Assert.Equal(1, target.Subscriptions);
		await proxy.DisposeAsync();
		Assert.True(context.IsDisposed);
		Assert.Equal(0, target.Subscriptions);
		Assert.Equal(1, target.Disposals);
		await proxy.DisposeAsync();
		Assert.Equal(1, target.Disposals);
	}

	[Test]
	[Arguments(false, JsonRpcEncoding.Json)]
	[Arguments(false, JsonRpcEncoding.MessagePack)]
	[Arguments(true, JsonRpcEncoding.Json)]
	[Arguments(true, JsonRpcEncoding.MessagePack)]
	public async Task SerializedCapabilityIdsResolveOnDemandAndRoundTrip(bool remote, JsonRpcEncoding encoding)
	{
		using BrokeredServiceContainer container = new();
		using JsonDocument manifest = JsonDocument.Parse("""{"name":"test.calculator","additionalInterfaceIds":["test.subtract.v1"]}""");
		ServiceMoniker moniker = new(manifest.RootElement.GetProperty("name").GetString()!);
		string[] ids = manifest.RootElement.GetProperty("additionalInterfaceIds").EnumerateArray().Select(static id => id.GetString()!).ToArray();
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = CreateCalculatorDescriptor(encoding) with
		{
			AdditionalInterfaces =
			[
				BrokeredTestServices.Subtract,
				ServiceInterface.Create("not.registered", TypeShapeResolver.ResolveDynamicOrThrow<ILocalBoundaryService>()),
			],
		};
		IDisposable? proffer = null;
		int loads = 0;
		int activations = 0;
		container.Register(new(
			moniker,
			(_, host) =>
			{
				loads++;
				proffer = host.Proffer(descriptor, () =>
				{
					activations++;
					return new Calculator();
				});
				return default;
			},
			additionalInterfaceIds: ids));
		ids[0] = "mutated.after.registration";
		Assert.Equal(0, loads);
		IServiceBrokerWithMetadata local = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		await using IpcRelayServiceBroker relay = new(local);
		using RemoteServiceBroker remoteBroker = await RemoteServiceBroker.ConnectAsync(relay, this.TimeoutToken);
		IServiceBrokerWithMetadata broker = remote ? remoteBroker : local;
		try
		{
			Assert.Equal(new[] { "test.subtract.v1" }, await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
			Assert.Equal(1, loads);
			Assert.Equal(0, activations);
			IBrokeredCalculator proxy = (await broker.GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken))!;
			await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
			IBrokeredSubtract subtract = Assert.IsAssignableFrom<IClientProxy>(proxy).As<IBrokeredSubtract>()!;
			Assert.Equal(5, await subtract.SubtractAsync(8, 3, this.TimeoutToken));
			Assert.Equal(1, activations);
			IBrokeredCalculator olderClient = (await broker.GetProxyAsync(descriptor with { AdditionalInterfaces = [] }, cancellationToken: this.TimeoutToken))!;
			await using System.IAsyncDisposable olderLifetime = (System.IAsyncDisposable)olderClient;
			Assert.Null(Assert.IsAssignableFrom<IClientProxy>(olderClient).As<IBrokeredSubtract>());
			Assert.Equal(7, await olderClient.AddAsync(3, 4, this.TimeoutToken));
		}
		finally
		{
			proffer?.Dispose();
		}
	}

	[Test]
	[Arguments(null)]
	[Arguments("")]
	[Arguments(" ")]
	[Arguments("duplicate")]
	public void RegistrationRejectsInvalidCapabilityIds(string? id)
	{
		string[] ids = id == "duplicate" ? [id, id] : [id!];
		Assert.Throws<ArgumentException>("additionalInterfaceIds", () => new ServiceRegistration(new("catalog"), additionalInterfaceIds: ids));
	}

	[Test]
	[Arguments("unknown")]
	[Arguments("TEST.SUBTRACT.V1")]
	public async Task ProfferRejectsUnknownCapabilityIdsWithoutPublishing(string id)
	{
		using BrokeredServiceContainer container = new();
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		container.Register(new(moniker, additionalInterfaceIds: [id]));
		InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => container.Proffer(BrokeredTestServices.Calculator, () => new Calculator()));
		Assert.Contains(id, failure.Message);
		Assert.Contains(moniker.ToString(), failure.Message);
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		Assert.Null(await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		JsonRpcDescriptor<IBrokeredCalculator> corrected = BrokeredTestServices.Descriptor with
		{
			AdditionalInterfaces = [ServiceInterface.Create(id, TypeShapeResolver.ResolveDynamicOrThrow<IBrokeredSubtract>())],
		};
		using IDisposable proffer = container.Proffer(corrected, () => new Calculator());
		Assert.Equal(new[] { id }, await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
	}

	[Test]
	public async Task DiscoveryRejectsUnknownRegisteredCapabilityIds()
	{
		using DiscoveringContainer container = new();
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		container.Register(new(moniker, additionalInterfaceIds: ["unknown"]));
		container.SetOffering(BrokeredTestServices.Calculator, (_, _, _, _) => new(new Calculator()));
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		await Assert.ThrowsAsync<InvalidOperationException>(async () => await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			IBrokeredCalculator? proxy = await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
			using (proxy as IDisposable)
			{
			}
		});
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task ResolvedCapabilityIdsStillValidateAndDisposeActivation(bool remote)
	{
		using BrokeredServiceContainer container = new();
		JsonRpcDescriptor<ILocalBoundaryService> descriptor = new JsonRpcMessagePackDescriptor<ILocalBoundaryService>(
			new("invalid-capability"), TypeShapeResolver.ResolveDynamicOrThrow<ILocalBoundaryService>())
		{
			AdditionalInterfaces = [BrokeredTestServices.Subtract],
		};
		container.Register(new(descriptor.Moniker, additionalInterfaceIds: ["test.subtract.v1"]));
		LocalBoundaryService target = new();
		using IDisposable proffer = container.Proffer(descriptor, () => target);
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		Assert.Equal(new[] { "test.subtract.v1" }, await broker.GetCapabilitiesAsync(descriptor.Moniker, this.TimeoutToken));
		InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			if (remote)
			{
				await using BrokeredServiceConnection? connection = await broker.GetConnectionAsync(descriptor.Moniker, cancellationToken: this.TimeoutToken);
			}
			else
			{
				ILocalBoundaryService? proxy = await broker.GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken);
				using (proxy as IDisposable)
				{
				}
			}
		});
		Assert.Contains("test.subtract.v1", failure.Message);
		Assert.True(target.Disposed);
	}

	[Test]
	public async Task ShapeResolvingFactoriesSupportDirectAndWitnessContracts()
	{
		ServiceMoniker moniker = new("shape-factories");
		JsonRpcDescriptor<IBrokeredCalculator>[] descriptors =
		[
			JsonRpcMessagePackDescriptor.Create<IBrokeredCalculator>(moniker),
			JsonRpcUtf8Descriptor.Create<IBrokeredCalculator>(moniker),
		];
		ServiceInterface<IBrokeredCalculator>[] contracts =
		[
			ServiceInterface.Create<IBrokeredCalculator>("calculator"),
			descriptors[0].CreateInterface<IBrokeredCalculator>("calculator"),
		];
		Assert.Equal(moniker, JsonRpcMessagePackDescriptor.Create<IWitnessCapability, WitnessCapabilityShapes>(moniker).Moniker);
		Assert.Equal(moniker, JsonRpcUtf8Descriptor.Create<IWitnessCapability, WitnessCapabilityShapes>(moniker).Moniker);
		Assert.Equal(typeof(IWitnessCapability), ServiceInterface.Create<IWitnessCapability, WitnessCapabilityShapes>("witness").InterfaceType);
		Assert.Equal(typeof(IWitnessCapability), descriptors[0].CreateInterface<IWitnessCapability, WitnessCapabilityShapes>("witness").InterfaceType);
		foreach (JsonRpcDescriptor<IBrokeredCalculator> descriptor in descriptors)
		{
			Assert.Equal(moniker, descriptor.Moniker);
			(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
			using JsonRpc clientRpc = descriptor.CreateConnection(client);
			using JsonRpc serverRpc = descriptor.CreateConnection(server);
			descriptor.AddRpcTarget(serverRpc, new Calculator(), []);
			IBrokeredCalculator proxy = descriptor.CreateProxy(clientRpc, []);
			serverRpc.Start();
			clientRpc.Start();
			Assert.Equal(7, await proxy.AddAsync(3, 4, this.TimeoutToken));
		}

		foreach (ServiceInterface<IBrokeredCalculator> contract in contracts)
		{
			await using ServiceProxyContext context = ServiceProxyContext.Own(new Calculator());
			IBrokeredCalculator proxy = contract.CreateLocalProxy(context);
			Assert.Equal("calculator", contract.Id);
			Assert.Equal(7, await proxy.AddAsync(3, 4, this.TimeoutToken));
		}
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task TypedContractHandlesCreateTypedProxies(bool remote)
	{
		ServiceInterface<IBrokeredCalculator> contract = ServiceInterface.Create("calculator", TypeShapeResolver.ResolveDynamicOrThrow<IBrokeredCalculator>());
		Calculator target = new();
		IJsonRpcDescriptor<IBrokeredCalculator> descriptor = BrokeredTestServices.Calculator;
		IBrokeredCalculator proxy;
		if (remote)
		{
			(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
			using JsonRpc serverRpc = descriptor.CreateConnection(server);
			using JsonRpc clientRpc = descriptor.CreateConnection(client);
			contract.AddRpcTarget(serverRpc, target, new());
			proxy = contract.CreateProxy(clientRpc);
			serverRpc.Start();
			clientRpc.Start();
			Assert.Equal(7, await proxy.AddAsync(3, 4, this.TimeoutToken));
		}
		else
		{
			await using ServiceProxyContext context = ServiceProxyContext.Own(target);
			proxy = contract.CreateLocalProxy(context);
			Assert.Equal(7, await proxy.AddAsync(3, 4, this.TimeoutToken));
		}
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task TypedDescriptorTargetsWorkThroughTypedAndErasedViews(JsonRpcEncoding encoding)
	{
		IJsonRpcDescriptor<IBrokeredCalculator> descriptor = CreateCalculatorDescriptor(encoding);
		await using ServiceProxyContext context = ServiceProxyContext.Own(new Calculator());
		IBrokeredCalculator target = descriptor.CreateLocalTarget(context, []);
		Assert.Equal(5, await target.AddAsync(2, 3, this.TimeoutToken));
		IJsonRpcDescriptor erased = descriptor;
		IBrokeredCalculator erasedTarget = Assert.IsAssignableFrom<IBrokeredCalculator>(erased.CreateLocalTarget(context, []));
		Assert.Equal(7, await erasedTarget.AddAsync(3, 4, this.TimeoutToken));
		(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
		using JsonRpc serverRpc = descriptor.CreateConnection(server);
		using JsonRpc clientRpc = descriptor.CreateConnection(client);
		descriptor.AddRpcTarget(serverRpc, target, []);
		IBrokeredCalculator proxy = descriptor.CreateProxy(clientRpc, []);
		serverRpc.Start();
		clientRpc.Start();
		Assert.Equal(9, await proxy.AddAsync(4, 5, this.TimeoutToken));
	}

	[Test]
	public void ProfferingRequiresRegistration()
	{
		using BrokeredServiceContainer container = new();
		Assert.Throws<InvalidOperationException>(() => container.Proffer(BrokeredTestServices.Calculator, () => new Calculator()));
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		Assert.True(container.Unregister(BrokeredTestServices.Calculator.Moniker));
		Assert.False(container.Unregister(BrokeredTestServices.Calculator.Moniker));
		Assert.Throws<InvalidOperationException>(() => container.Proffer(BrokeredTestServices.Calculator, () => new Calculator()));
		Assert.Throws<ArgumentNullException>(() => container.Unregister(null!));
		container.Dispose();
		Assert.Throws<ObjectDisposedException>(() => container.Unregister(BrokeredTestServices.Calculator.Moniker));
	}

	[Test]
	public async Task ConcurrentRegistrationAndUnregistrationHaveOneWinner()
	{
		using BrokeredServiceContainer container = new();
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		TaskCompletionSource<bool> start = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<bool>[] writers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
		{
			await start.Task.WithCancellation(this.TimeoutToken);
			try
			{
				container.Register(new(moniker));
				return true;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
		})).ToArray();
		start.TrySetResult(true);
		Assert.Single((await Task.WhenAll(writers).WithCancellation(this.TimeoutToken)).Where(static registered => registered));
		Assert.Equal(new[] { moniker }, container.GetRegisteredServices());
		Task<bool>[] removers = Enumerable.Range(0, 16).Select(_ => Task.Run(() => container.Unregister(moniker))).ToArray();
		Assert.Single((await Task.WhenAll(removers).WithCancellation(this.TimeoutToken)).Where(static removed => removed));
		Assert.Empty(container.GetRegisteredServices());
	}

	[Test]
	public async Task LiveRegistrationChangesRespectViewAudiences()
	{
		using BrokeredServiceContainer container = new();
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		IServiceBrokerWithMetadata process = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker(ServiceAudience.Process));
		IServiceBrokerWithMetadata local = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		IServiceBrokerWithMetadata sdk = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker(ServiceAudience.Local | ServiceAudience.PublicSdk));
		int processChanges = 0;
		int localChanges = 0;
		int sdkChanges = 0;
		process.AvailabilityChanged += (_, _) => processChanges++;
		local.AvailabilityChanged += (_, _) => localChanges++;
		sdk.AvailabilityChanged += (_, _) => sdkChanges++;
		foreach (IServiceBrokerWithMetadata view in new[] { process, local, sdk })
		{
			Assert.Null(await view.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		}

		container.Register(new(moniker, audience: ServiceAudience.Process));
		using IDisposable first = container.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
		Assert.NotNull(await process.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		Assert.True(container.Unregister(moniker));
		Assert.Equal(2, processChanges);
		Assert.Equal(0, localChanges);
		Assert.Equal(0, sdkChanges);
		Assert.Null(await process.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		container.Register(new(moniker, audience: ServiceAudience.Local | ServiceAudience.PublicSdk));
		using IDisposable second = container.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
		Assert.Equal(3, processChanges);
		Assert.Equal(1, localChanges);
		Assert.Equal(1, sdkChanges);
		Assert.NotNull(await sdk.GetCapabilitiesAsync(moniker, this.TimeoutToken));
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task LiveUnregistrationPreservesClientsAndIsolatesReplacement(bool remote)
	{
		using BrokeredServiceContainer container = new();
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		await using IpcRelayServiceBroker relay = new(broker);
		using RemoteServiceBroker remoteBroker = await RemoteServiceBroker.ConnectAsync(relay, this.TimeoutToken);
		IServiceBroker client = remote ? remoteBroker : broker;
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		container.Register(new(moniker, additionalInterfaceIds: [BrokeredTestServices.Subtract.Id]));
		Calculator original = new();
		using IDisposable oldProffer = container.Proffer(BrokeredTestServices.Calculator, () => original);
		IBrokeredCalculator proxy = (await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		Assert.NotNull(Assert.IsAssignableFrom<IClientProxy>(proxy).As<IBrokeredSubtract>());

		Assert.True(container.Unregister(moniker));
		Assert.Empty(container.GetRegisteredServices());
		Assert.Null(await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		IBrokeredCalculator? removed = await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		using (removed as IDisposable)
		{
			Assert.Null(removed);
		}

		Assert.False(original.Disposed);
		Assert.Equal(5, await proxy.AddAsync(2, 3, this.TimeoutToken));
		container.Register(new(moniker));
		using IDisposable replacement = container.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
		oldProffer.Dispose();
		IBrokeredCalculator next = (await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable nextLifetime = (System.IAsyncDisposable)next;
		Assert.Null(Assert.IsAssignableFrom<IClientProxy>(next).As<IBrokeredSubtract>());
		Assert.Equal(9, await next.AddAsync(4, 5, this.TimeoutToken));
	}

	[Test]
	[Arguments(false, false)]
	[Arguments(false, true)]
	[Arguments(true, false)]
	[Arguments(true, true)]
	public async Task PendingLoaderCannotExposeRemovedRegistration(bool replace, bool profferLate)
	{
		using BrokeredServiceContainer container = new();
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		container.Register(new(moniker, async (_, host) =>
		{
			started.TrySetResult(true);
			await release.Task.WithCancellation(this.TimeoutToken);
			if (profferLate)
			{
				host.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
			}
		}));
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		Task<IReadOnlyList<string>?> pending = broker.GetCapabilitiesAsync(moniker, this.TimeoutToken).AsTask();
		await started.Task.WithCancellation(this.TimeoutToken);
		Assert.True(container.Unregister(moniker));
		if (replace)
		{
			container.Register(new(moniker, additionalInterfaceIds: [BrokeredTestServices.Subtract.Id]));
		}

		release.TrySetResult(true);
		if (profferLate)
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
		}
		else
		{
			Assert.Null(await pending);
		}

		Assert.Null(await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		if (replace)
		{
			using IDisposable proffer = container.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
			Assert.Equal(new[] { BrokeredTestServices.Subtract.Id }, await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		}
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task PendingDiscoveryUsesConcurrentlyProfferedFactory(bool discoverOffering)
	{
		using DelayedDiscoveryContainer container = new() { DiscoverOffering = discoverOffering };
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		container.Register(new(moniker));
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		async Task AcquireAsync()
		{
			IBrokeredCalculator? proxy = await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
			await using System.IAsyncDisposable? lifetime = (System.IAsyncDisposable?)proxy;
			Assert.NotNull(proxy);
			Assert.Equal(7, await proxy.AddAsync(3, 4, this.TimeoutToken));
		}

		Task pending = AcquireAsync();
		await container.Started.Task.WithCancellation(this.TimeoutToken);
		int activations = 0;
		using IDisposable proffer = container.Proffer(BrokeredTestServices.Calculator, () =>
		{
			activations++;
			return new Calculator();
		});
		container.Release.TrySetResult(true);
		await pending.WithCancellation(this.TimeoutToken);
		Assert.Equal(1, activations);
	}

	[Test]
	public async Task PendingDiscoveryCannotUseReplacementMetadata()
	{
		using DelayedDiscoveryContainer container = new();
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		container.Register(new(moniker));
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		Task<IReadOnlyList<string>?> pending = broker.GetCapabilitiesAsync(moniker, this.TimeoutToken).AsTask();
		await container.Started.Task.WithCancellation(this.TimeoutToken);
		Assert.True(container.Unregister(moniker));
		container.Register(new(moniker, additionalInterfaceIds: [BrokeredTestServices.Subtract.Id]));
		container.Release.TrySetResult(true);
		Assert.Null(await pending);
		using IDisposable proffer = container.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
		Assert.Equal(new[] { BrokeredTestServices.Subtract.Id }, await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
	}

	[Test]
	public async Task ReregisteringSameMetadataRestartsLoaderAfterFailure()
	{
		using BrokeredServiceContainer container = new();
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		int loads = 0;
		IDisposable? proffer = null;
		ServiceRegistration registration = new(moniker, (_, host) =>
		{
			if (++loads == 1)
			{
				throw new InvalidOperationException("First load failed.");
			}

			proffer = host.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
			return default;
		});
		container.Register(registration);
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		await Assert.ThrowsAsync<InvalidOperationException>(async () => await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		Assert.True(container.Unregister(moniker));
		container.Register(registration);
		try
		{
			Assert.NotNull(await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
			Assert.Equal(2, loads);
		}
		finally
		{
			proffer?.Dispose();
		}
	}

	[Test]
	public async Task LiveCatalogChangesInvalidateCacheWithoutDestroyingRentals()
	{
		using BrokeredServiceContainer container = new();
		using ServiceBrokerClient client = new(container.GetServiceBroker());
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		container.Register(new(moniker));
		Calculator original = new();
		using IDisposable oldProffer = container.Proffer(BrokeredTestServices.Calculator, () => original);
		using ServiceBrokerClient.Rental<IBrokeredCalculator> first = await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		Assert.NotNull(first.Proxy);
		Assert.True(container.Unregister(moniker));
		using ServiceBrokerClient.Rental<IBrokeredCalculator> absent = await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		Assert.Null(absent.Proxy);
		Assert.False(original.Disposed);
		Assert.Equal(3, await first.Proxy.AddAsync(1, 2, this.TimeoutToken));
		container.Register(new(moniker));
		using IDisposable replacement = container.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
		using ServiceBrokerClient.Rental<IBrokeredCalculator> next = await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		Assert.NotNull(next.Proxy);
		Assert.NotSame(first.Proxy, next.Proxy);
	}

	[Test]
	public async Task ConcurrentCatalogMutationsAndQueriesAreSafe()
	{
		using BrokeredServiceContainer container = new();
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		TaskCompletionSource<bool> start = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task writer = Task.Run(async () =>
		{
			await start.Task.WithCancellation(this.TimeoutToken);
			for (int i = 0; i < 100; i++)
			{
				container.Register(new(moniker, additionalInterfaceIds: [BrokeredTestServices.Subtract.Id]));
				using IDisposable proffer = container.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
				await Task.Yield();
				Assert.True(container.Unregister(moniker));
			}
		});
		Task[] readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
		{
			await start.Task.WithCancellation(this.TimeoutToken);
			for (int i = 0; i < 100; i++)
			{
				IReadOnlyList<string>? capabilities = await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken);
				if (capabilities is not null)
				{
					Assert.Equal(new[] { BrokeredTestServices.Subtract.Id }, capabilities);
				}

				IBrokeredCalculator? proxy = await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
				if (proxy is not null)
				{
					await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
					Assert.Equal(3, await proxy.AddAsync(1, 2, this.TimeoutToken));
				}
			}
		})).ToArray();
		start.TrySetResult(true);
		await Task.WhenAll(readers.Append(writer)).WithCancellation(this.TimeoutToken);
		Assert.Empty(container.GetRegisteredServices());
	}

	[Test]
	[Arguments(0, false)]
	[Arguments(0, true)]
	[Arguments(1, false)]
	[Arguments(1, true)]
	[Arguments(2, false)]
	[Arguments(2, true)]
	public async Task SimpleFactoriesActivateServices(int factoryKind, bool remote)
	{
		using BrokeredServiceContainer container = new();
		int activations = 0;
		Calculator Create()
		{
			activations++;
			return new();
		}

		async ValueTask<IBrokeredCalculator?> CreateAsync()
		{
			await Task.Yield();
			return Create();
		}

		container.Register(new(BrokeredTestServices.Descriptor.Moniker, audience: ServiceAudience.Local | ServiceAudience.PublicSdk, additionalInterfaceIds: [BrokeredTestServices.Subtract.Id]));
		IBrokeredServiceContainer host = container;
		using IDisposable registration = factoryKind switch
		{
			0 => host.Proffer(BrokeredTestServices.Calculator, Create),
			1 => host.Proffer(BrokeredTestServices.Calculator, CreateAsync),
			_ => host.Proffer(BrokeredTestServices.Calculator, token =>
			{
				Assert.Equal(this.TimeoutToken, token);
				return CreateAsync();
			}),
		};
		IServiceBroker broker = container.GetServiceBroker(ServiceAudience.Local | ServiceAudience.PublicSdk);
		JsonRpc? rpc = null;
		try
		{
			IBrokeredCalculator proxy;
			if (remote)
			{
				IDuplexPipe pipe = (await broker.GetPipeAsync(BrokeredTestServices.Descriptor.Moniker, cancellationToken: this.TimeoutToken))!;
				rpc = BrokeredTestServices.Descriptor.CreateConnection(pipe);
				proxy = BrokeredTestServices.Descriptor.CreateProxy(rpc, [BrokeredTestServices.Subtract]);
				rpc.Start();
			}
			else
			{
				proxy = (await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken))!;
			}

			await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
			Assert.Equal(5, await proxy.AddAsync(2, 3, this.TimeoutToken));
			Assert.Equal(1, await Assert.IsAssignableFrom<IClientProxy>(proxy).As<IBrokeredSubtract>()!.SubtractAsync(3, 2, this.TimeoutToken));
			Assert.Equal(1, activations);
		}
		finally
		{
			rpc?.Dispose();
		}
	}

	[Test]
	public void SimpleFactoriesRejectNull()
	{
		using BrokeredServiceContainer container = new();
		Assert.Throws<ArgumentNullException>("factory", () => container.Proffer(BrokeredTestServices.Descriptor, (Func<ValueTask<IBrokeredCalculator?>>)null!));
		Assert.Throws<ArgumentNullException>("factory", () => container.Proffer(BrokeredTestServices.Descriptor, (Func<CancellationToken, ValueTask<IBrokeredCalculator?>>)null!));
		Assert.Throws<ArgumentNullException>("factory", () => container.Proffer(BrokeredTestServices.Descriptor, (Func<IBrokeredCalculator?>)null!));
	}

	[Test]
	[Arguments(0)]
	[Arguments(1)]
	[Arguments(2)]
	public async Task SimpleFactoriesPreserveUnavailabilityAndFailures(int factoryKind)
	{
		using BrokeredServiceContainer container = new();
		IServiceBroker broker = container.GetServiceBroker();
		container.Register(new(BrokeredTestServices.Descriptor.Moniker));
		async ValueTask<IBrokeredCalculator?> UnavailableAsync()
		{
			await Task.Yield();
			return null;
		}

		using (factoryKind switch
		{
			0 => container.Proffer(BrokeredTestServices.Calculator, () => (IBrokeredCalculator?)null),
			1 => container.Proffer(BrokeredTestServices.Calculator, UnavailableAsync),
			_ => container.Proffer(BrokeredTestServices.Calculator, _ => UnavailableAsync()),
		})
		{
			IBrokeredCalculator? proxy = await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
			using (proxy as IDisposable)
			{
				Assert.Null(proxy);
			}
		}

		InvalidOperationException failure = new("Activation failed.");
		async ValueTask<IBrokeredCalculator?> FailAsync()
		{
			await Task.Yield();
			throw failure;
		}

		using IDisposable registration = factoryKind switch
		{
			0 => container.Proffer(BrokeredTestServices.Calculator, (Func<IBrokeredCalculator?>)(() => throw failure)),
			1 => container.Proffer(BrokeredTestServices.Calculator, FailAsync),
			_ => container.Proffer(BrokeredTestServices.Calculator, _ => FailAsync()),
		};
		InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			IBrokeredCalculator? proxy = await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
			using (proxy as IDisposable)
			{
			}
		});
		Assert.Same(failure, actual);
	}

	[Test]
	public void AudienceOrdinalsMatchServiceHub()
	{
		Assert.Equal(0, (int)ServiceAudience.None);
		Assert.Equal(1, (int)ServiceAudience.Process);
		Assert.Equal(3, (int)ServiceAudience.Local);
		Assert.Equal(0x10000000, (int)ServiceAudience.PublicSdk);
	}

	[Test]
	[Arguments(ServiceAudience.Process, ServiceAudience.Process, true)]
	[Arguments(ServiceAudience.Process, ServiceAudience.Local, false)]
	[Arguments(ServiceAudience.Local, ServiceAudience.Process, true)]
	[Arguments(ServiceAudience.Local, ServiceAudience.Local, true)]
	[Arguments(ServiceAudience.Local, ServiceAudience.Process | ServiceAudience.PublicSdk, false)]
	[Arguments(ServiceAudience.Local | ServiceAudience.PublicSdk, ServiceAudience.Process | ServiceAudience.PublicSdk, true)]
	[Arguments(ServiceAudience.Process | ServiceAudience.PublicSdk, ServiceAudience.Local | ServiceAudience.PublicSdk, false)]
	[Arguments(ServiceAudience.Local | ServiceAudience.PublicSdk, ServiceAudience.Local | ServiceAudience.PublicSdk, true)]
	[Arguments(ServiceAudience.None, ServiceAudience.None, false)]
	[Arguments(ServiceAudience.None, ServiceAudience.Process, false)]
	[Arguments(ServiceAudience.PublicSdk, ServiceAudience.PublicSdk, false)]
	[Arguments(ServiceAudience.Process, ServiceAudience.None, true)]
	public async Task AudienceFiltersAllAcquisitionSurfaces(ServiceAudience serviceAudience, ServiceAudience filter, bool visible)
	{
		using BrokeredServiceContainer container = new();
		int activations = 0;
		BrokeredServiceFactory<IBrokeredCalculator> factory = (_, _, _, _) =>
		{
			activations++;
			return new(new Calculator());
		};
		container.Register(new(BrokeredTestServices.Descriptor.Moniker, audience: serviceAudience));
		using IDisposable proffer = container.Proffer(BrokeredTestServices.Descriptor, factory);
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker(filter));
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		Assert.Equal(visible, await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken) is not null);
		IBrokeredCalculator? proxy = await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		using (proxy as IDisposable)
		{
			Assert.Equal(visible, proxy is not null);
		}

		bool rpcVisible = visible && (serviceAudience & ServiceAudience.Local) == ServiceAudience.Local;
		await using BrokeredServiceConnection? connection = await broker.GetConnectionAsync(moniker, cancellationToken: this.TimeoutToken);
		Assert.Equal(rpcVisible, connection is not null);
		IDuplexPipe? pipe = await broker.GetPipeAsync(moniker, cancellationToken: this.TimeoutToken);
		Assert.Equal(rpcVisible, pipe is not null);
		if (pipe is not null)
		{
			await pipe.Input.CompleteAsync();
			await pipe.Output.CompleteAsync();
		}

		Assert.Equal((visible ? 1 : 0) + (rpcVisible ? 2 : 0), activations);
	}

	[Test]
	[Arguments(ServiceAudience.Process)]
	[Arguments(ServiceAudience.None)]
	[Arguments(ServiceAudience.Local)]
	[Arguments(ServiceAudience.Process | ServiceAudience.PublicSdk)]
	[Arguments(ServiceAudience.Local | ServiceAudience.PublicSdk)]
	public async Task AudienceFilteringPrecedesLazyLoading(ServiceAudience audience)
	{
		using BrokeredServiceContainer container = new();
		int loads = 0;
		IDisposable? proffer = null;
		BrokeredServiceLoader loader = (_, host) =>
		{
			loads++;

			// The catalog policy remains authoritative even when the loaded offering uses its default.
			proffer = host.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
			return default;
		};
		container.Register(new(BrokeredTestServices.Calculator.Moniker, loader, audience));
		IServiceBrokerWithMetadata remote = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker(ServiceAudience.Local | ServiceAudience.PublicSdk));
		IServiceBrokerWithMetadata local = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker(ServiceAudience.Process));
		try
		{
			if ((audience & ServiceAudience.Local) != ServiceAudience.Local)
			{
				Assert.Null(await local.GetConnectionAsync(BrokeredTestServices.Calculator.Moniker, cancellationToken: this.TimeoutToken));
				Assert.Equal(0, loads);
			}

			bool remoteVisible = audience == (ServiceAudience.Local | ServiceAudience.PublicSdk);
			Assert.Equal(remoteVisible, await remote.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken) is not null);
			Assert.Equal(remoteVisible ? 1 : 0, loads);
			Assert.Equal(audience != ServiceAudience.None, await local.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken) is not null);
			Assert.Equal(audience == ServiceAudience.None ? 0 : 1, loads);
			Assert.Equal(remoteVisible, await remote.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken) is not null);
		}
		finally
		{
			proffer?.Dispose();
		}
	}

	[Test]
	public async Task AudienceFiltersAvailabilityNotifications()
	{
		using BrokeredServiceContainer container = new();
		IServiceBrokerWithMetadata process = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker(ServiceAudience.Process));
		IServiceBrokerWithMetadata remote = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker(ServiceAudience.Local));
		IServiceBrokerWithMetadata sdk = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker(ServiceAudience.Process | ServiceAudience.PublicSdk));
		int processChanges = 0;
		int remoteChanges = 0;
		int sdkChanges = 0;
		process.AvailabilityChanged += (_, _) => processChanges++;
		remote.AvailabilityChanged += (_, _) => remoteChanges++;
		sdk.AvailabilityChanged += (_, _) => sdkChanges++;
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		foreach (IServiceBrokerWithMetadata view in new[] { process, remote, sdk })
		{
			Assert.Null(await view.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		}

		container.Register(new(BrokeredTestServices.Descriptor.Moniker, audience: ServiceAudience.Process));
		using IDisposable proffer = container.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
		Assert.Equal(1, processChanges);
		Assert.Equal(0, remoteChanges);
		Assert.Equal(0, sdkChanges);
		await process.GetCapabilitiesAsync(moniker, this.TimeoutToken);
		proffer.Dispose();
		Assert.Equal(2, processChanges);
		Assert.Equal(0, remoteChanges);
		Assert.Equal(0, sdkChanges);
	}

	[Test]
	public async Task HostDiscoveredOfferingsRespectAudience()
	{
		using DiscoveringContainer container = new();
		container.Register(new(BrokeredTestServices.Descriptor.Moniker, audience: ServiceAudience.Process));
		container.SetOffering(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
		IServiceBrokerWithMetadata remote = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker(ServiceAudience.Local));
		Assert.Null(await remote.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken));
		Assert.Null(await remote.GetConnectionAsync(BrokeredTestServices.Calculator.Moniker, cancellationToken: this.TimeoutToken));
		IBrokeredCalculator? hidden = await remote.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		using (hidden as IDisposable)
		{
			Assert.Null(hidden);
		}
	}

	[Test]
	public void InvalidAudienceFlagsAreRejected()
	{
		using BrokeredServiceContainer container = new();
		ServiceAudience invalid = (ServiceAudience)0x100;
		Assert.Throws<ArgumentOutOfRangeException>(() => container.GetServiceBroker(invalid));
		Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceRegistration(BrokeredTestServices.Calculator.Moniker, audience: invalid));
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public void SubscribingAfterDisposalDoesNotRetainHandler(bool disposeContainer)
	{
		using BrokeredServiceContainer container = new();
		IServiceBroker broker = container.GetServiceBroker();
		if (disposeContainer)
		{
			container.Dispose();
		}
		else
		{
			((IDisposable)broker).Dispose();
		}

		WeakReference target = SubscribeCollectibleHandler(broker);
		CollectBrokerViews();
		Assert.False(target.IsAlive);
		GC.KeepAlive(broker);
		GC.KeepAlive(container);
	}

	[Test]
	[Arguments("none")]
	[Arguments("removed")]
	[Arguments("disposed")]
	public void UnusedBrokerViewsCanBeCollected(string subscription)
	{
		using BrokeredServiceContainer container = new();
		WeakReference view = CreateCollectibleBrokerView(container, subscription);
		CollectBrokerViews();
		Assert.False(view.IsAlive);
		GC.KeepAlive(container);
	}

	[Test]
	public void SubscribedBrokerViewIsRootedUntilContainerDisposal()
	{
		using BrokeredServiceContainer container = new();
		WeakReference view = CreateCollectibleBrokerView(container, "subscribed");
		CollectBrokerViews();
		Assert.True(view.IsAlive);
		container.Dispose();
		CollectBrokerViews();
		Assert.False(view.IsAlive);
		GC.KeepAlive(container);
	}

	[Test]
	public async Task BrokerViewSubscriptionsCanBeRemovedAndRestored()
	{
		using BrokeredServiceContainer container = new();
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		int firstCalls = 0;
		int secondCalls = 0;
		EventHandler<BrokeredServicesChangedEventArgs> first = (_, _) => firstCalls++;
		EventHandler<BrokeredServicesChangedEventArgs> second = (_, _) => secondCalls++;
		broker.AvailabilityChanged += first;
		broker.AvailabilityChanged += second;
		await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken);
		container.Register(new(BrokeredTestServices.Descriptor.Moniker));
		using IDisposable proffer = container.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
		Assert.Equal(1, firstCalls);
		Assert.Equal(1, secondCalls);
		broker.AvailabilityChanged -= first;
		await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken);
		proffer.Dispose();
		Assert.Equal(1, firstCalls);
		Assert.Equal(2, secondCalls);
		broker.AvailabilityChanged -= second;
		broker.AvailabilityChanged += first;
		await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken);
		container.Dispose();
		Assert.Equal(2, firstCalls);
		Assert.Equal(2, secondCalls);
		broker.AvailabilityChanged -= first;
	}

	[Test]
	public async Task DescriptorsPreserveConfiguredSerializers()
	{
		JsonRpcUtf8Descriptor<IBrokeredCalculator> json = JsonRpcUtf8Descriptor.Create(
			BrokeredTestServices.Calculator.Moniker, TypeShapeResolver.ResolveDynamicOrThrow<IBrokeredCalculator>());
		JsonRpcMessagePackDescriptor<IBrokeredCalculator> messagePack = BrokeredTestServices.Descriptor;
		JsonRpcUtf8Descriptor<IBrokeredCalculator> configuredJson = json with
		{
			Serializer = json.Serializer with { PropertyNamingPolicy = Nerdbank.Json.JsonNamingPolicy.SnakeLowerCase },
		};
		JsonRpcMessagePackDescriptor<IBrokeredCalculator> configuredMessagePack = messagePack with
		{
			Serializer = messagePack.Serializer with { PropertyNamingPolicy = Nerdbank.MessagePack.MessagePackNamingPolicy.SnakeLowerCase },
		};
		Assert.NotSame(json.Serializer, configuredJson.Serializer);
		Assert.NotSame(messagePack.Serializer, configuredMessagePack.Serializer);
		Assert.Same(JsonRpcMessagePackChannel.DefaultSerializer, messagePack.Serializer);
		Assert.Throws<ArgumentNullException>(() => json with { Serializer = null! });
		Assert.Throws<ArgumentNullException>(() => messagePack with { Serializer = null! });
		foreach (JsonRpcDescriptor<IBrokeredCalculator> descriptor in new JsonRpcDescriptor<IBrokeredCalculator>[] { configuredJson, configuredMessagePack })
		{
			(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
			using JsonRpc rpc = descriptor.CreateConnection(client);
			if (((IJsonRpcClient)rpc).Serializer is JsonSerializerPlugin jsonPlugin)
			{
				Assert.Same(configuredJson.Serializer.PropertyNamingPolicy, jsonPlugin.Serializer.PropertyNamingPolicy);
			}
			else
			{
				MessagePackSerializerPlugin plugin = Assert.IsType<MessagePackSerializerPlugin>(((IJsonRpcClient)rpc).Serializer);
				Assert.Same(configuredMessagePack.Serializer.PropertyNamingPolicy, plugin.Serializer.PropertyNamingPolicy);
			}

			await server.Input.CompleteAsync();
			await server.Output.CompleteAsync();
		}
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task RegistrationLoadsAndProffersOnlyOnDemand(bool remote)
	{
		using BrokeredServiceContainer container = new();
		IServiceBroker broker = container.GetServiceBroker();
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		IDisposable? proffer = null;
		int loads = 0;
		int activations = 0;
		container.Register(new(moniker, (requested, host) =>
		{
			Assert.Equal(moniker, requested);
			loads++;
			proffer = host.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) =>
			{
				activations++;
				return new(new Calculator());
			});
			return default;
		}));
		Assert.Equal(new[] { moniker }, container.GetRegisteredServices());
		Assert.Equal(0, loads);
		Assert.Equal(0, activations);
		try
		{
			if (remote)
			{
				IDuplexPipe pipe = (await broker.GetPipeAsync(moniker, cancellationToken: this.TimeoutToken))!;
				using JsonRpc rpc = BrokeredTestServices.Descriptor.CreateConnection(pipe);
				IBrokeredCalculator proxy = BrokeredTestServices.Descriptor.CreateProxy(rpc, []);
				using IDisposable lifetime = (IDisposable)proxy;
				rpc.Start();
				Assert.Equal(3, await proxy.AddAsync(1, 2, this.TimeoutToken));
			}
			else
			{
				IBrokeredCalculator proxy = (await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken))!;
				await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
				Assert.Equal(3, await proxy.AddAsync(1, 2, this.TimeoutToken));
			}

			Assert.Equal(1, loads);
			Assert.Equal(1, activations);
			proffer!.Dispose();
			Assert.Equal(new[] { moniker }, container.GetRegisteredServices());
			IBrokeredCalculator? unavailable = await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
			using (unavailable as IDisposable)
			{
				Assert.Null(unavailable);
			}

			Assert.Equal(1, loads);
			using IDisposable replacement = container.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
			IBrokeredCalculator restored = (await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken))!;
			await using System.IAsyncDisposable restoredLifetime = (System.IAsyncDisposable)restored;
			Assert.Equal(7, await restored.AddAsync(3, 4, this.TimeoutToken));
		}
		finally
		{
			proffer?.Dispose();
		}
	}

	[Test]
	[Arguments("proxy")]
	[Arguments("pipe")]
	[Arguments("connection")]
	[Arguments("capabilities")]
	public async Task MissingRegistrationWaitsForPendingCatalog(string request)
	{
		using RegistrationDiscoveryContainer container = new();
		TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		IDisposable? proffer = null;
		int loads = 0;
		container.LoadRegistrations = async (moniker, cancellationToken) =>
		{
			Assert.Equal(BrokeredTestServices.Calculator.Moniker, moniker);
			started.TrySetResult(true);
			await release.Task.WithCancellation(cancellationToken);
		};
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		async Task AcquireAsync()
		{
			switch (request)
			{
				case "proxy":
					IBrokeredCalculator proxy = (await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken))!;
					await using ((System.IAsyncDisposable)proxy)
					{
						Assert.Equal(7, await proxy.AddAsync(3, 4, this.TimeoutToken));
					}

					break;
				case "pipe":
					IDuplexPipe? pipe = await broker.GetPipeAsync(BrokeredTestServices.Calculator.Moniker, cancellationToken: this.TimeoutToken);
					Assert.NotNull(pipe);
					await pipe.Input.CompleteAsync();
					await pipe.Output.CompleteAsync();
					break;
				case "connection":
					await using (BrokeredServiceConnection? connection = await broker.GetConnectionAsync(BrokeredTestServices.Calculator.Moniker, cancellationToken: this.TimeoutToken))
					{
						Assert.NotNull(connection);
					}

					break;
				case "capabilities":
					Assert.NotNull(await broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken));
					break;
				default:
					throw new InvalidOperationException(request);
			}
		}

		Task acquisition = AcquireAsync();
		await started.Task.WithCancellation(this.TimeoutToken);
		Assert.False(acquisition.IsCompleted);
		try
		{
			await Task.Run(() => container.Register(new(BrokeredTestServices.Calculator.Moniker, (_, host) =>
			{
				Interlocked.Increment(ref loads);
				proffer = host.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
				return default;
			}))).WithCancellation(this.TimeoutToken);
			release.SetResult(true);
			await acquisition.WithCancellation(this.TimeoutToken);
			Assert.Equal(1, container.RegistrationLookups);
			Assert.Equal(1, loads);
		}
		finally
		{
			release.TrySetResult(true);
			proffer?.Dispose();
		}
	}

	[Test]
	public async Task RegisteredServicesDoNotWaitForOtherCatalogSources()
	{
		using RegistrationDiscoveryContainer container = new();
		TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		container.LoadRegistrations = async (_, cancellationToken) => await release.Task.WithCancellation(cancellationToken);
		container.Register(new(BrokeredTestServices.Calculator.Moniker, audience: ServiceAudience.Process));
		using IDisposable proffer = container.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
		IServiceBrokerWithMetadata local = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		Task<IReadOnlyList<string>?> missing = local.GetCapabilitiesAsync(new("pending-catalog"), this.TimeoutToken).AsTask();
		try
		{
			Assert.False(missing.IsCompleted);
			Assert.Null(await local.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken));
			IServiceBroker process = container.GetServiceBroker(ServiceAudience.Process);
			IBrokeredCalculator proxy = (await process.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken))!;
			await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
			Assert.Equal(7, await proxy.AddAsync(3, 4, this.TimeoutToken));
			Assert.Equal(1, container.RegistrationLookups);
		}
		finally
		{
			release.SetResult(true);
			Assert.Null(await missing.WithCancellation(this.TimeoutToken));
		}
	}

	[Test]
	public async Task MissingRegistrationWaitsAreIndependentAndCancelable()
	{
		using RegistrationDiscoveryContainer container = new();
		TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		container.LoadRegistrations = async (_, cancellationToken) => await release.Task.WithCancellation(cancellationToken);
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		using CancellationTokenSource cancellation = new();
		Task<IReadOnlyList<string>?> canceled = broker.GetCapabilitiesAsync(new("missing"), cancellation.Token).AsTask();
		Task<IReadOnlyList<string>?> remaining = broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken).AsTask();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
		Assert.False(remaining.IsCompleted);
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		using IDisposable proffer = container.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
		release.SetResult(true);
		Assert.NotNull(await remaining.WithCancellation(this.TimeoutToken));
		Assert.Equal(2, container.RegistrationLookups);
	}

	[Test]
	public async Task MissingRegistrationIsRetriedOncePerRequestWithoutNegativeCaching()
	{
		using RegistrationDiscoveryContainer container = new();
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		for (int i = 1; i <= 2; i++)
		{
			Assert.Null(await broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken));
			Assert.Equal(i, container.RegistrationLookups);
		}

		container.LoadRegistrations = (moniker, _) =>
		{
			container.Register(new(moniker));
			return default;
		};
		Assert.Null(await broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken));
		using IDisposable proffer = container.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
		Assert.NotNull(await broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken));
		Assert.Equal(3, container.RegistrationLookups);
		Assert.True(container.Unregister(BrokeredTestServices.Calculator.Moniker));
		Assert.Null(await broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken));
		Assert.Equal(4, container.RegistrationLookups);
	}

	[Test]
	public async Task PendingRegistrationStillAppliesAudienceBeforeLoading()
	{
		using RegistrationDiscoveryContainer container = new();
		int loads = 0;
		container.LoadRegistrations = (moniker, _) =>
		{
			container.Register(new(
				moniker,
				(_, _) =>
				{
					loads++;
					return default;
				},
				audience: ServiceAudience.Process));
			return default;
		};
		Assert.Empty(container.GetRegisteredServices());
		Assert.Equal(0, container.RegistrationLookups);
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		Assert.Null(await broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken));
		Assert.Equal(1, container.RegistrationLookups);
		Assert.Equal(0, loads);
	}

	[Test]
	public async Task MissingRegistrationFailuresPropagate()
	{
		using RegistrationDiscoveryContainer container = new();
		container.LoadRegistrations = (_, _) => throw new InvalidOperationException("Catalog failed.");
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
			async () => await broker.GetCapabilitiesAsync(new("missing"), this.TimeoutToken));
		Assert.Equal("Catalog failed.", failure.Message);
	}

	[Test]
	public async Task MissingRegistrationRechecksDisposalAndCancellationAfterHostWait()
	{
		using RegistrationDiscoveryContainer container = new();
		TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		container.LoadRegistrations = async (_, _) => await release.Task.WithCancellation(this.TimeoutToken);
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		using CancellationTokenSource cancellation = new();
		Task<IReadOnlyList<string>?> canceled = broker.GetCapabilitiesAsync(new("missing"), cancellation.Token).AsTask();
		Task<IReadOnlyList<string>?> disposed = broker.GetCapabilitiesAsync(new("missing"), this.TimeoutToken).AsTask();
		cancellation.Cancel();
		container.Dispose();
		release.SetResult(true);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => disposed);
	}

	[Test]
	public async Task ConcurrentRequestsShareLoaderWithoutSharingCancellation()
	{
		using BrokeredServiceContainer container = new();
		TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		IDisposable? proffer = null;
		int loads = 0;
		container.Register(new(BrokeredTestServices.Calculator.Moniker, async (_, host) =>
		{
			Interlocked.Increment(ref loads);
			started.TrySetResult(true);
			await release.Task.WithCancellation(this.TimeoutToken);
			proffer = host.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
		}));
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		using CancellationTokenSource cancellation = new();
		Task<IReadOnlyList<string>?> canceled = broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, cancellation.Token).AsTask();
		await started.Task.WithCancellation(this.TimeoutToken);
		Task<IReadOnlyList<string>?>[] requests = Enumerable.Range(0, 16)
			.Select(_ => broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken).AsTask()).ToArray();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
		release.TrySetResult(true);
		try
		{
			Assert.All(await Task.WhenAll(requests).WithCancellation(this.TimeoutToken), capabilities => Assert.NotNull(capabilities));
			Assert.Equal(1, loads);
		}
		finally
		{
			proffer?.Dispose();
		}
	}

	[Test]
	public async Task LoaderFailuresPropagateAndAreNotRepeated()
	{
		using BrokeredServiceContainer container = new();
		int loads = 0;
		container.Register(new(BrokeredTestServices.Calculator.Moniker, (_, _) =>
		{
			loads++;
			throw new InvalidOperationException("Loading failed.");
		}));
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		for (int i = 0; i < 2; i++)
		{
			InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
				async () => await broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken));
			Assert.Equal("Loading failed.", failure.Message);
		}

		Assert.Equal(1, loads);
	}

	[Test]
	public async Task RegistrationAndProfferingHaveSeparateLifetimes()
	{
		using BrokeredServiceContainer container = new();
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		container.Register(new(moniker));
		Assert.Throws<InvalidOperationException>(() => container.Register(new(moniker)));
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		Assert.Null(await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		using IDisposable proffer = container.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
		Assert.NotNull(await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		Assert.Throws<InvalidOperationException>(() => container.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator())));
		container.Dispose();
		Assert.Throws<ObjectDisposedException>(() => container.Register(new(new("after.disposal"))));
	}

	[Test]
	public async Task PreCanceledRequestsDoNotLoadRegisteredServices()
	{
		using BrokeredServiceContainer container = new();
		int loads = 0;
		container.Register(new(BrokeredTestServices.Calculator.Moniker, (_, _) =>
		{
			loads++;
			return default;
		}));
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			async () => await broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, cancellation.Token));
		Assert.Equal(0, loads);
	}

	[Test]
	public async Task CapabilityQueriesLoadButDoNotActivate()
	{
		using JoinableTaskContext threading = new();
		using BrokeredServiceContainer container = new() { JoinableTaskFactory = threading.Factory };
		int loads = 0;
		int activations = 0;
		IDisposable? proffer = null;
		container.Register(new(
			BrokeredTestServices.Calculator.Moniker,
			(_, host) =>
			{
				loads++;
				BrokeredServiceFactory<IBrokeredCalculator> factory = (_, _, _, _) =>
				{
					activations++;
					return new(new Calculator());
				};
				proffer = host.Proffer(BrokeredTestServices.Descriptor, factory);
				return default;
			},
			additionalInterfaceIds: [BrokeredTestServices.Subtract.Id]));
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		try
		{
			Assert.Equal(
				new[] { BrokeredTestServices.Subtract.Id },
				await broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken));
			Assert.Equal(1, loads);
			Assert.Equal(0, activations);
		}
		finally
		{
			proffer?.Dispose();
		}
	}

	[Test]
	public async Task ContainerDisposalRejectsProfferingFromPendingLoader()
	{
		using BrokeredServiceContainer container = new();
		TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		container.Register(new(BrokeredTestServices.Calculator.Moniker, async (_, host) =>
		{
			started.TrySetResult(true);
			await release.Task.WithCancellation(this.TimeoutToken);
			host.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
		}));
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		Task<IReadOnlyList<string>?> request = broker.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken).AsTask();
		await started.Task.WithCancellation(this.TimeoutToken);
		container.Dispose();
		release.TrySetResult(true);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => request);
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task SharedFactoryReceivesRequestedMoniker(bool remote)
	{
		using BrokeredServiceContainer container = new();
		JsonRpcDescriptor<IBrokeredCalculator> first = BrokeredTestServices.Descriptor;
		JsonRpcDescriptor<IBrokeredCalculator> second = first with { Moniker = new("test.second.calculator") };
		Calculator firstTarget = new();
		Calculator secondTarget = new();
		BrokeredServiceFactory<IBrokeredCalculator> factory = (moniker, _, _, _) => new(
			moniker == first.Moniker ? firstTarget
			: moniker == second.Moniker ? secondTarget
			: throw new InvalidOperationException("Unexpected service moniker."));
		container.Register(new(first.Moniker));
		using IDisposable firstRegistration = container.Proffer(first, factory);
		container.Register(new(second.Moniker));
		using IDisposable secondRegistration = container.Proffer(second, factory);
		IServiceBroker broker = container.GetServiceBroker();
		foreach (JsonRpcDescriptor<IBrokeredCalculator> descriptor in new[] { first, second })
		{
			if (remote)
			{
				IDuplexPipe pipe = (await broker.GetPipeAsync(descriptor.Moniker, cancellationToken: this.TimeoutToken))!;
				using JsonRpc rpc = descriptor.CreateConnection(pipe);
				IBrokeredCalculator proxy = descriptor.CreateProxy(rpc, []);
				using IDisposable lifetime = (IDisposable)proxy;
				rpc.Start();
				Assert.Equal(3, await proxy.AddAsync(1, 2, this.TimeoutToken));
			}
			else
			{
				IBrokeredCalculator proxy = (await broker.GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken))!;
				await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
				Assert.Equal(3, await proxy.AddAsync(1, 2, this.TimeoutToken));
			}
		}

		Assert.True(await firstTarget.Disposal.Task.WithCancellation(this.TimeoutToken));
		Assert.True(await secondTarget.Disposal.Task.WithCancellation(this.TimeoutToken));
	}

	[Test]
	[Arguments(true)]
	[Arguments(false)]
	public async Task LocalInvocationPreservesExplicitRpcErrors(bool includeDetails)
	{
		JsonRpcErrorDetails details = new() { Code = (JsonRpcErrorCode)42, Message = "Explicit service error" };
		using BrokeredServiceContainer container = new();
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = BrokeredTestServices.Descriptor with
		{
			RpcOptions = JsonRpcOptions.Default with { IncludeExceptionDetails = includeDetails },
		};
		container.Register(new(descriptor.Moniker));
		using IDisposable registration = container.Proffer(descriptor, (_, _, _, _) => new(new Calculator { Failure = new JsonRpcException(details) }));
		IBrokeredCalculator proxy = (await container.GetServiceBroker().GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		RemoteInvocationException failure = await Assert.ThrowsAsync<RemoteInvocationException>(() => proxy.FailAsync(this.TimeoutToken));
		Assert.Equal(details.Code, failure.ErrorDetails.Code);
		Assert.Equal(details.Message, failure.Message);
		Assert.Null(failure.InnerException);
		Assert.Null(failure.RemoteException);
	}

	[Test]
	public async Task RegistrationWithdrawalCanRaceContainerDisposal()
	{
		for (int iteration = 0; iteration < 256; iteration++)
		{
			using BrokeredServiceContainer container = new();
			container.Register(new(BrokeredTestServices.Descriptor.Moniker));
			using IDisposable registration = container.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
			TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Task withdraw = Task.Run(async () =>
			{
				await ready.Task;
				registration.Dispose();
			});
			Task dispose = Task.Run(async () =>
			{
				await ready.Task;
				container.Dispose();
			});
			ready.SetResult(true);
			await Task.WhenAll(withdraw, dispose).WithCancellation(this.TimeoutToken);
		}
	}

	[Test]
	public async Task PreCanceledCacheRentalDoesNotActivateService()
	{
		using BrokeredServiceContainer container = new();
		int activations = 0;
		container.Register(new(BrokeredTestServices.Descriptor.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) =>
		{
			activations++;
			return new(new Calculator());
		});
		using ServiceBrokerClient client = new(container.GetServiceBroker());
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: cancellation.Token));
		Assert.Equal(0, activations);
	}

	[Test]
	public async Task LocalAcquisitionRejectsAndDisposesWrongPrimaryContract()
	{
		using BrokeredServiceContainer container = new();
		LocalBoundaryService target = new();
		JsonRpcDescriptor<ILocalBoundaryService> descriptor = new JsonRpcMessagePackDescriptor<ILocalBoundaryService>(
			BrokeredTestServices.Descriptor.Moniker, TypeShapeResolver.ResolveDynamicOrThrow<ILocalBoundaryService>());
		container.Register(new(BrokeredTestServices.Descriptor.Moniker));
		using IDisposable registration = container.Proffer(descriptor, (_, _, _, _) => new(target));
		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			IBrokeredCalculator? proxy = await container.GetServiceBroker().GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
			using (proxy as IDisposable)
			{
			}
		});
		Assert.True(target.Disposed);
	}

	[Test]
	public async Task RemoteServiceConnectionsPreserveClientInfrastructure()
	{
		using BrokeredServiceContainer container = new();
		container.Register(new(BrokeredTestServices.Descriptor.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
		await using IpcRelayServiceBroker relay = new(Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker()));
		(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
		JsonRpcDescriptor<IRemoteServiceBroker> control = FrameworkServices.RemoteServiceBroker;
		using JsonRpc serverRpc = control.CreateConnection(server);
		control.AddRpcTarget(serverRpc, relay, []);
		serverRpc.Start();
		ServiceRpcContext infrastructure = new() { Logger = LoggerFactory.CreateLogger<JsonRpc>(), SynchronizationContext = null };
		using RemoteServiceBroker broker = await RemoteServiceBroker.ConnectAsync(client, control, infrastructure, this.TimeoutToken);
		ServiceRpcContext? captured = null;
		ContextRecordingDescriptor descriptor = new() { ConnectionCreated = context => captured = context };
		IBrokeredCalculator proxy = (await broker.GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		Assert.Same(infrastructure, captured);
		Assert.Equal(42, await proxy.AddAsync(19, 23, this.TimeoutToken));
	}

	[Test]
	public async Task CancellationCallbackFailureDoesNotLeakCachedActivation()
	{
		using BrokeredServiceContainer container = new();
		Calculator target = new();
		container.Register(new(BrokeredTestServices.Descriptor.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Descriptor, (_, _, _, token) =>
		{
			token.Register(() => throw new InvalidOperationException("Cancellation callback failed."));
			return new(target);
		});
		using ServiceBrokerClient client = new(container.GetServiceBroker());
		using (ServiceBrokerClient.Rental<IBrokeredCalculator> rental = await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken))
		{
			Assert.NotNull(rental.Proxy);
		}

		Assert.Throws<AggregateException>(client.Dispose);
		Assert.True(await target.Disposal.Task.WithCancellation(this.TimeoutToken));
	}

	[Test]
	[Arguments(1)]
	[Arguments(2)]
	[Arguments(3)]
	public async Task FailedEnumerationRevokesBorrowedCallbacks(int failure)
	{
		LocalBoundaryService target = new();
		JsonRpcMessagePackDescriptor<ILocalBoundaryService> descriptor = new(new("enumeration"), TypeShapeResolver.ResolveDynamicOrThrow<ILocalBoundaryService>());
		ILocalBoundaryService proxy = descriptor.CreateLocalProxy(ServiceProxyContext.Own(target), []);
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		TestObserver observer = new();
		await Assert.ThrowsAsync<RemoteInvocationException>(async () =>
		{
			await foreach (int value in proxy.EnumerateWithObserverAsync(observer, failure, this.TimeoutToken))
			{
				Assert.Equal(1, value);
			}
		});
		target.Observer!.OnNext(42);
		Assert.Equal(0, observer.Updates.Count);
	}

	[Test]
	[Arguments("null")]
	[Arguments("type")]
	[Arguments("id")]
	public void CapabilityMetadataRejectsAmbiguousContracts(string ambiguity)
	{
		ServiceInterface first = BrokeredTestServices.Subtract;
		ServiceInterface other = ambiguity switch
		{
			"null" => null!,
			"type" => ServiceInterface.Create("different.id", TypeShapeResolver.ResolveDynamicOrThrow<IBrokeredSubtract>()),
			_ => ServiceInterface.Create(first.Id, TypeShapeResolver.ResolveDynamicOrThrow<IBrokeredCalculator>()),
		};
		Assert.Throws<ArgumentException>(() => BrokeredTestServices.Descriptor with { AdditionalInterfaces = [first, other] });
	}

	[Test]
	public async Task HostCanNotifyLazyDiscoveryAndObserveLocalAndPipeRequests()
	{
		using DiscoveringContainer container = new();
		container.Register(new(BrokeredTestServices.Descriptor.Moniker));
		IServiceBrokerWithMetadata broker = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		IBrokeredCalculator? unavailable = await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		using (unavailable as IDisposable)
		{
			Assert.Null(unavailable);
		}

		TaskCompletionSource<BrokeredServicesChangedEventArgs> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		broker.AvailabilityChanged += (_, args) => changed.TrySetResult(args);
		container.SetOffering(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
		container.SignalChange();
		Assert.Contains(BrokeredTestServices.Calculator.Moniker, (await changed.Task.WithCancellation(this.TimeoutToken)).ImpactedServices);
		IBrokeredCalculator proxy = (await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		await using BrokeredServiceConnection connection = (await broker.GetConnectionAsync(BrokeredTestServices.Calculator.Moniker, default, this.TimeoutToken))!;
		container.SetOffering(BrokeredTestServices.Descriptor, (_, _, _, _) => throw new InvalidOperationException("Host activation failed."));
		await Assert.ThrowsAsync<InvalidOperationException>(async () => await broker.GetConnectionAsync(BrokeredTestServices.Calculator.Moniker, default, this.TimeoutToken));
		Assert.Equal(new[] { false, true, true, false }, container.Requests.Select(static request => request.Available));
		Assert.IsType<InvalidOperationException>(container.Requests[3].Exception);
	}

	[Test]
	public async Task ResilientSubscriptionRejectsFailedRestorationAndRetries()
	{
		JsonRpcMessagePackDescriptor<IObserverService> descriptor = new(new("observer"), TypeShapeResolver.ResolveDynamicOrThrow<IObserverService>());
		ObserverService first = new();
		ObserverService broken = new() { FailSubscriptions = true };
		ObserverService replacement = new();
		SequenceBroker broker = new(
			descriptor.CreateLocalProxy(ServiceProxyContext.Own(first), []),
			descriptor.CreateLocalProxy(ServiceProxyContext.Own(broken), []),
			descriptor.CreateLocalProxy(ServiceProxyContext.Own(replacement), []))
		{
			Moniker = descriptor.Moniker,
		};
		await using ResilientServiceProxy<IObserverService> resilient = await broker.GetResilientProxyAsync(descriptor, cancellationToken: this.TimeoutToken);
		TestObserver observer = new();
		using IDisposable subscription = await resilient.Proxy.SubscribeAsync(observer, this.TimeoutToken);
		AsyncQueue<ResilientServiceProxyChangeKind> changes = new();
		resilient.BackingServiceChanged += (_, args) => changes.Enqueue(args.Kind);
		broker.SignalChange();
		Assert.Equal(ResilientServiceProxyChangeKind.Lost, await changes.DequeueAsync(this.TimeoutToken));
		Assert.False(resilient.IsAvailable);
		Assert.True(await first.Unsubscribed.Task.WithCancellation(this.TimeoutToken));
		using IDisposable returnedObserver = Assert.IsAssignableFrom<IDisposable>(await resilient.Proxy.GetObserverAsync(this.TimeoutToken));
		Assert.Equal(ResilientServiceProxyChangeKind.Gained, await changes.DequeueAsync(this.TimeoutToken));
		replacement.Publish(42);
		Assert.Equal(42, await observer.Updates.DequeueAsync(this.TimeoutToken));
		subscription.Dispose();
		Assert.True(await replacement.Unsubscribed.Task.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public async Task EventAccessorCanDisposeItsProxyDuringAttachment()
	{
		Calculator target = new();
		IBrokeredCalculator proxy = BrokeredTestServices.Descriptor.CreateLocalProxy(ServiceProxyContext.Own(target), []);
		target.OnEventAttachment = Assert.IsAssignableFrom<IDisposable>(proxy).Dispose;
		Assert.Throws<ObjectDisposedException>(() => proxy.Changed += (_, _) => { });
		await ((System.IAsyncDisposable)proxy).DisposeAsync();
		Assert.Equal(0, target.EventHandlerCount);
		Assert.True(target.Disposed);
	}

	[Test]
	public async Task EventAccessorCanDisposeResilientOwnerDuringMigration()
	{
		Calculator first = new();
		Calculator replacement = new();
		SequenceBroker broker = new(
			BrokeredTestServices.Descriptor.CreateLocalProxy(ServiceProxyContext.Own(first), []),
			BrokeredTestServices.Descriptor.CreateLocalProxy(ServiceProxyContext.Own(replacement), []));
		await using ResilientServiceProxy<IBrokeredCalculator> resilient = await broker.GetResilientProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		resilient.Proxy.Changed += (_, _) => { };
		replacement.OnEventAttachment = resilient.Dispose;
		broker.SignalChange();
		await resilient.DisposeAsync();
		Assert.Equal(0, first.EventHandlerCount);
		Assert.Equal(0, replacement.EventHandlerCount);
		Assert.True(replacement.Disposed);
	}

	[Test]
	public async Task ReturnedStreamRetainsItsLocalService()
	{
		Calculator owner = new();
		await using ServiceProxyContext context = ServiceProxyContext.Own(new OutOfBandStreamService(), owner);
		JsonRpcMessagePackDescriptor<IOutOfBandStreamService> descriptor = new(new("streams"), TypeShapeResolver.ResolveDynamicOrThrow<IOutOfBandStreamService>());
		IOutOfBandStreamService proxy = descriptor.CreateLocalProxy(context, []);
		using Stream stream = await proxy.ReturnStreamAsync(this.TimeoutToken);
		context.Dispose();
		Assert.False(owner.Disposed);
		byte[] buffer = new byte[5];
		Assert.Equal(5, await stream.ReadAsync(buffer, 0, buffer.Length, this.TimeoutToken));
		Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(buffer));
		await Assert.IsAssignableFrom<System.IAsyncDisposable>(stream).DisposeAsync();
		await context.DisposeAsync();
		Assert.True(owner.Disposed);
	}

	[Test]
	[Arguments("reader")]
	[Arguments("writer")]
	[Arguments("duplex")]
	public async Task ReturnedPipeEndsRetainTheirLocalService(string resource)
	{
		LocalBoundaryService target = new();
		await using ServiceProxyContext context = ServiceProxyContext.Own(target);
		JsonRpcMessagePackDescriptor<ILocalBoundaryService> descriptor = new(new("pipes"), TypeShapeResolver.ResolveDynamicOrThrow<ILocalBoundaryService>());
		ILocalBoundaryService proxy = descriptor.CreateLocalProxy(context, []);
		PipeReader? reader = null;
		PipeWriter? writer = null;
		try
		{
			switch (resource)
			{
				case "reader":
					reader = await proxy.GetReaderAsync(this.TimeoutToken);
					break;
				case "writer":
					writer = await proxy.GetWriterAsync(this.TimeoutToken);
					break;
				default:
					IDuplexPipe pipe = await proxy.GetPipeAsync(this.TimeoutToken);
					reader = pipe.Input;
					writer = pipe.Output;
					break;
			}

			context.Dispose();
			Assert.False(target.Disposed);
			if (reader is not null)
			{
				await reader.CompleteAsync();
				if (writer is not null)
				{
					Assert.False(target.Disposed);
				}
			}
		}
		finally
		{
			try
			{
				if (reader is not null)
				{
					await reader.CompleteAsync();
				}
			}
			finally
			{
				if (writer is not null)
				{
					await writer.CompleteAsync();
				}
			}
		}

		await context.DisposeAsync();
		Assert.True(target.Disposed);
	}

	[Test]
	public async Task ReturnedMarshalableObjectRetainsItsServiceAndRoundTripsByIdentity()
	{
		RemoteCounterService target = new();
		Calculator owner = new();
		await using ServiceProxyContext context = ServiceProxyContext.Own(target, owner);
		JsonRpcMessagePackDescriptor<IRemoteCounterService> descriptor = new(new("counter"), TypeShapeResolver.ResolveDynamicOrThrow<IRemoteCounterService>());
		IRemoteCounterService proxy = descriptor.CreateLocalProxy(context, []);
		using IRemoteCounter counter = await proxy.GetCounterAsync(this.TimeoutToken);
		Assert.NotSame(target.Counter, counter);
		Assert.True(await proxy.IsSameCounterAsync(counter, this.TimeoutToken));
		context.Dispose();
		Assert.False(owner.Disposed);
		Assert.Equal(1, await counter.IncrementAsync(this.TimeoutToken));
		counter.Dispose();
		await context.DisposeAsync();
		Assert.True(owner.Disposed);
		Assert.True(target.Counter.IsDisposed);
	}

	[Test]
	public async Task LocalMarshalableCallbacksAreBorrowedAndCallScopedCallbacksExpire()
	{
		RemoteCounterService target = new();
		await using ServiceProxyContext context = ServiceProxyContext.Own(target);
		JsonRpcMessagePackDescriptor<IRemoteCounterService> descriptor = new(new("counter"), TypeShapeResolver.ResolveDynamicOrThrow<IRemoteCounterService>());
		IRemoteCounterService proxy = descriptor.CreateLocalProxy(context, []);
		RemoteCounter callerOwned = new();
		Assert.False(await proxy.IsSameCounterAsync(callerOwned, this.TimeoutToken));
		Assert.NotSame(callerOwned, target.LastExplicitProxy);
		target.LastExplicitProxy!.Dispose();
		Assert.False(callerOwned.IsDisposed);
		Assert.Equal(1, await callerOwned.IncrementAsync(this.TimeoutToken));
		CallScopedCounter callScoped = new();
		Assert.Equal(1, await proxy.UseCallScopedCounterAsync(callScoped, this.TimeoutToken));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => target.LastCallScopedProxy!.IncrementAsync(this.TimeoutToken));
		await Assert.ThrowsAsync<RemoteInvocationException>(() => proxy.ReturnCallScopedCounterAsync(this.TimeoutToken));
		await Assert.ThrowsAsync<RemoteInvocationException>(() => proxy.EchoCallScopedCounterAsync(callScoped, this.TimeoutToken));
	}

	[Test]
	public async Task ReturnedObserverRetainsItsServiceWithoutOwningTheObserver()
	{
		ObserverService target = new();
		Calculator owner = new();
		await using ServiceProxyContext context = ServiceProxyContext.Own(target, owner);
		JsonRpcMessagePackDescriptor<IObserverService> descriptor = new(new("observer"), TypeShapeResolver.ResolveDynamicOrThrow<IObserverService>());
		IObserverService proxy = descriptor.CreateLocalProxy(context, []);
		IObserver<int> observer = await proxy.GetObserverAsync(this.TimeoutToken);
		await using System.IAsyncDisposable boundary = Assert.IsAssignableFrom<System.IAsyncDisposable>(observer);
		Assert.NotSame(target.OwnedObserver, observer);
		Assert.True(await proxy.IsOwnedObserverAsync(observer, this.TimeoutToken));
		context.Dispose();
		Assert.False(owner.Disposed);
		observer.OnNext(42);
		Assert.Equal(42, await target.OwnedObserver.Updates.DequeueAsync(this.TimeoutToken));
		observer.OnCompleted();
		Assert.True(await target.OwnedObserver.Completed.Task.WithCancellation(this.TimeoutToken));
		await context.DisposeAsync();
		Assert.True(owner.Disposed);
		Assert.False(target.OwnedObserver.IsDisposed);
	}

	[Test]
	public async Task FailedCallsRevokeBorrowedObservers()
	{
		ObserverService target = new();
		await using ServiceProxyContext context = ServiceProxyContext.Own(target);
		JsonRpcMessagePackDescriptor<IObserverService> descriptor = new(new("observer"), TypeShapeResolver.ResolveDynamicOrThrow<IObserverService>());
		IObserverService proxy = descriptor.CreateLocalProxy(context, []);
		TestObserver observer = new();
		await Assert.ThrowsAsync<RemoteInvocationException>(() => proxy.FailAfterReceivingAsync(observer, this.TimeoutToken));
		target.Subscriber!.OnNext(99);
		Assert.Empty(observer.Values);
		Assert.False(observer.IsDisposed);
	}

	[Test]
	public async Task ProgressCallbacksExpireAtTheEndOfTheLocalCall()
	{
		await using ServiceProxyContext context = ServiceProxyContext.Own(new ProgressService());
		JsonRpcMessagePackDescriptor<IProgressService> descriptor = new(new("progress"), TypeShapeResolver.ResolveDynamicOrThrow<IProgressService>());
		IProgressService proxy = descriptor.CreateLocalProxy(context, []);
		RecordingProgress<int> progress = new();
		Assert.Equal(3, await proxy.ReportProgressAsync(progress, this.TimeoutToken));
		Assert.True(await proxy.ReportAfterCompletionAsync(this.TimeoutToken));
		Assert.Equal([1, 2], progress.Values);
	}

	[Test]
	[Arguments(true)]
	[Arguments(false)]
	public async Task LocalExceptionsHonorDisclosurePolicy(bool includeDetails)
	{
		using BrokeredServiceContainer container = new() { LoggerFactory = TestBase.LoggerFactory };
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = BrokeredTestServices.Descriptor with
		{
			RpcOptions = JsonRpcOptions.Default with { IncludeExceptionDetails = includeDetails },
		};
		container.Register(new(descriptor.Moniker));
		using IDisposable registration = container.Proffer(descriptor, (_, _, _, _) => new(new Calculator()));
		IBrokeredCalculator proxy = (await container.GetServiceBroker().GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		RemoteInvocationException failure = await Assert.ThrowsAsync<RemoteInvocationException>(() => proxy.FailAsync(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.InvocationError, failure.ErrorDetails.Code);
		Assert.Equal(includeDetails ? "Service failure" : "The request could not be completed.", failure.Message);
		if (includeDetails)
		{
			Assert.IsType<InvalidOperationException>(failure.InnerException);
			Assert.Equal("System.InvalidOperationException", failure.RemoteException?.TypeName);
		}
		else
		{
			Assert.Null(failure.InnerException);
			Assert.Null(failure.RemoteException);
		}
	}

	[Test]
	[Arguments(1)]
	[Arguments(2)]
	[Arguments(3)]
	public async Task LocalEnumerationTranslatesAcquisitionMoveAndDisposalFailures(int failure)
	{
		LocalBoundaryService target = new();
		JsonRpcMessagePackDescriptor<ILocalBoundaryService> descriptor = new(new("enumeration"), TypeShapeResolver.ResolveDynamicOrThrow<ILocalBoundaryService>());
		ILocalBoundaryService proxy = descriptor.CreateLocalProxy(ServiceProxyContext.Own(target), []);
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(async () =>
		{
			await foreach (int value in proxy.EnumerateAsync(failure, this.TimeoutToken))
			{
				Assert.Equal(1, value);
				break;
			}
		});
		Assert.IsType<InvalidOperationException>(exception.InnerException);
	}

	[Test]
	public async Task LocalEnumerationRetainsGenerationAndHonorsConsumerCancellation()
	{
		LocalBoundaryService target = new();
		JsonRpcMessagePackDescriptor<ILocalBoundaryService> descriptor = new(new("enumeration"), TypeShapeResolver.ResolveDynamicOrThrow<ILocalBoundaryService>());
		ILocalBoundaryService proxy = descriptor.CreateLocalProxy(ServiceProxyContext.Own(target), []);
		using CancellationTokenSource cancellation = new();
		await using (IAsyncEnumerator<int> enumerator = proxy.EnumerateAsync(0).GetAsyncEnumerator(cancellation.Token))
		{
			Assert.True(await enumerator.MoveNextAsync());
			Assert.Equal(1, enumerator.Current);
			Assert.IsAssignableFrom<IDisposable>(proxy).Dispose();
			Assert.False(target.Disposed);
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
		}

		await ((System.IAsyncDisposable)proxy).DisposeAsync();
		Assert.True(target.Disposed);
	}

	[Test]
	[Arguments(true)]
	[Arguments(false)]
	public async Task UnclaimedIpcRequestsReleaseTheirService(bool cancel)
	{
		using BrokeredServiceContainer container = new();
		Calculator target = new();
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(target));
		await using IpcRelayServiceBroker relay = new(Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker()))
		{
			ConnectionTimeout = TimeSpan.FromMilliseconds(50),
		};
		RemoteServiceConnectionInfo info = (await relay.RequestServiceChannelAsync(BrokeredTestServices.Calculator.Moniker, default, this.TimeoutToken))!;
		if (cancel)
		{
			await relay.CancelServiceRequestAsync(info.RequestId, this.TimeoutToken);
		}

		Assert.True(await target.Disposal.Task.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public async Task RelayCapabilitiesComeFromTheActivatedRegistration()
	{
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = BrokeredTestServices.Descriptor with { AdditionalInterfaces = [BrokeredTestServices.Subtract] };
		using AlternatingContainer container = new(
			descriptor, (_, _, _, _) => new(new Calculator()));
		container.Register(new(descriptor.Moniker));
		await using IpcRelayServiceBroker relay = new(Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker()));
		using RemoteServiceBroker broker = await RemoteServiceBroker.ConnectAsync(relay, this.TimeoutToken);
		IBrokeredCalculator proxy = (await broker.GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		Assert.Equal(1, container.Lookups);
		Assert.Null(Assert.IsAssignableFrom<IClientProxy>(proxy).As<IBrokeredSubtract>());
		Assert.Equal(3, await proxy.AddAsync(1, 2, this.TimeoutToken));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task RemoteActivationPreservesCulturesAndArguments(JsonRpcEncoding encoding)
	{
		using BrokeredServiceContainer container = new();
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = CreateCalculatorDescriptor(encoding);
		TaskCompletionSource<ServiceActivationOptions> activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
		container.Register(new(descriptor.Moniker));
		using IDisposable registration = container.Proffer(descriptor, (_, options, _, _) =>
		{
			Assert.Equal(string.Empty, System.Globalization.CultureInfo.CurrentCulture.Name);
			Assert.Equal(string.Empty, System.Globalization.CultureInfo.CurrentUICulture.Name);
			activated.TrySetResult(options);
			return new(new Calculator());
		});
		await using IpcRelayServiceBroker relay = new(Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker()));
		(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
		JsonRpcDescriptor<IRemoteServiceBroker> control = CreateControlDescriptor(encoding);
		using JsonRpc serverRpc = control.CreateConnection(server);
		control.AddRpcTarget(serverRpc, relay, []);
		serverRpc.Start();
		using RemoteServiceBroker broker = await RemoteServiceBroker.ConnectAsync(client, control, cancellationToken: this.TimeoutToken);
		Assert.Empty((await broker.GetCapabilitiesAsync(descriptor.Moniker, this.TimeoutToken))!);
		Assert.False(activated.Task.IsCompleted);
		IBrokeredCalculator proxy = (await broker.GetProxyAsync(
			descriptor,
			new()
			{
				ActivationArguments = new Dictionary<string, string> { ["request"] = "culture-test" },
				ClientCulture = System.Globalization.CultureInfo.InvariantCulture,
				ClientUICulture = System.Globalization.CultureInfo.InvariantCulture,
			},
			this.TimeoutToken))!;
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		ServiceActivationOptions received = await activated.Task.WithCancellation(this.TimeoutToken);
		Assert.Equal(string.Empty, received.ClientCulture?.Name);
		Assert.Equal(string.Empty, received.ClientUICulture?.Name);
		Assert.Equal("culture-test", received.ActivationArguments?["request"]);
	}

	[Test]
	public async Task ResilientProxyRejectsFailedEventMigrationAndRetries()
	{
		Calculator first = new();
		Calculator broken = new() { FailEventAttachment = true };
		Calculator replacement = new();
		SequenceBroker broker = new(
			BrokeredTestServices.Descriptor.CreateLocalProxy(ServiceProxyContext.Own(first), []),
			BrokeredTestServices.Descriptor.CreateLocalProxy(ServiceProxyContext.Own(broken), []),
			BrokeredTestServices.Descriptor.CreateLocalProxy(ServiceProxyContext.Own(replacement), []));
		await using ResilientServiceProxy<IBrokeredCalculator> resilient = await broker.GetResilientProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		int received = 0;
		resilient.Proxy.Changed += (_, value) => received = value;
		AsyncQueue<ResilientServiceProxyChangeKind> changes = new();
		resilient.BackingServiceChanged += (_, args) => changes.Enqueue(args.Kind);

		broker.SignalChange();
		Assert.Equal(ResilientServiceProxyChangeKind.Lost, await changes.DequeueAsync(this.TimeoutToken));
		Assert.False(resilient.IsAvailable);
		Assert.True(broken.Disposed);
		Assert.Equal(7, await resilient.Proxy.AddAsync(3, 4, this.TimeoutToken));
		Assert.Equal(ResilientServiceProxyChangeKind.Gained, await changes.DequeueAsync(this.TimeoutToken));
		Assert.Equal(7, received);
		Assert.True(first.Disposed);
	}

	[Test]
	public async Task ResilientProxyReplacesDisconnectedRpcConnection()
	{
		static (JsonRpc Server, IBrokeredCalculator Proxy) CreatePair()
		{
			(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
			JsonRpcDescriptor<IBrokeredCalculator> descriptor = BrokeredTestServices.Descriptor;
			JsonRpc serverRpc = descriptor.CreateConnection(server);
			descriptor.AddRpcTarget(serverRpc, new Calculator(), []);
			JsonRpc clientRpc = descriptor.CreateConnection(client);
			IBrokeredCalculator proxy = descriptor.CreateProxy(clientRpc, []);
			clientRpc.Start();
			serverRpc.Start();
			return (serverRpc, proxy);
		}

		(JsonRpc firstServer, IBrokeredCalculator first) = CreatePair();
		(JsonRpc secondServer, IBrokeredCalculator second) = CreatePair();
		using JsonRpc firstLifetime = firstServer;
		using JsonRpc secondLifetime = secondServer;
		SequenceBroker broker = new(first, second);
		await using ResilientServiceProxy<IBrokeredCalculator> resilient = await broker.GetResilientProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		TaskCompletionSource<ResilientServiceProxyChangeKind> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		resilient.BackingServiceChanged += (_, args) => changed.TrySetResult(args.Kind);
		firstServer.Dispose();
		Assert.Equal(ResilientServiceProxyChangeKind.Replaced, await changed.Task.WithCancellation(this.TimeoutToken));
		Assert.Equal(5, await resilient.Proxy.AddAsync(2, 3, this.TimeoutToken));
	}

	[Test]
	public async Task ResilientObserverSubscriptionFollowsReplacement()
	{
		JsonRpcMessagePackDescriptor<IObserverService> descriptor = new(new("observer"), TypeShapeResolver.ResolveDynamicOrThrow<IObserverService>());
		using BrokeredServiceContainer container = new();
		ObserverService first = new();
		container.Register(new(descriptor.Moniker));
		IDisposable registration = container.Proffer(descriptor, (_, _, _, _) => new(first));
		await using ResilientServiceProxy<IObserverService> resilient = await container.GetServiceBroker().GetResilientProxyAsync(descriptor, cancellationToken: this.TimeoutToken);
		TestObserver observer = new();
		using IDisposable subscription = await resilient.Proxy.SubscribeAsync(observer, this.TimeoutToken);
		first.Publish(1);
		Assert.Equal(1, await observer.Updates.DequeueAsync(this.TimeoutToken));
		TaskCompletionSource<bool> lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> gained = new(TaskCreationOptions.RunContinuationsAsynchronously);
		resilient.BackingServiceChanged += (_, args) =>
		{
			(args.Kind == ResilientServiceProxyChangeKind.Lost ? lost : gained).TrySetResult(true);
		};
		registration.Dispose();
		await lost.Task.WithCancellation(this.TimeoutToken);
		Assert.True(await first.Unsubscribed.Task.WithCancellation(this.TimeoutToken));
		ObserverService second = new();
		using IDisposable replacement = container.Proffer(descriptor, (_, _, _, _) => new(second));
		await gained.Task.WithCancellation(this.TimeoutToken);
		second.Publish(2);
		Assert.Equal(2, await observer.Updates.DequeueAsync(this.TimeoutToken));
		subscription.Dispose();
		Assert.True(await second.Unsubscribed.Task.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public async Task LifecycleHandlerCanDisposeResilientOwnerReentrantly()
	{
		using BrokeredServiceContainer container = new();
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(new Calculator()));
		ResilientServiceProxy<IBrokeredCalculator> resilient = await container.GetServiceBroker().GetResilientProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		TaskCompletionSource<bool> completedInHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);
		resilient.BackingServiceChanged += (sender, _) =>
		{
			Task cleanup = resilient.DisposeAsync().AsTask();
			completedInHandler.TrySetResult(ReferenceEquals(sender, resilient) && cleanup.IsCompleted);
		};
		registration.Dispose();
		Assert.True(await completedInHandler.Task.WithCancellation(this.TimeoutToken));
		await resilient.DisposeAsync();
	}

	[Test]
	public async Task CacheRetriesActivationThatFailsAfterWaiterCancels()
	{
		using BrokeredServiceContainer container = new();
		TaskCompletionSource<IBrokeredCalculator?> activation = new();
		TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int attempts = 0;
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, async (_, _, _, _) =>
		{
			if (++attempts == 1)
			{
				started.SetResult(true);
				return await activation.Task;
			}

			return new Calculator();
		});
		using ServiceBrokerClient client = new(container.GetServiceBroker());
		using CancellationTokenSource cancellation = new();
		Task canceled = AcquireAsync();
		await started.Task.WithCancellation(this.TimeoutToken);
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

		// Inline continuations let the shared activation finish faulting before the next acquisition.
		activation.SetException(new InvalidOperationException("Delayed activation failure"));
		using ServiceBrokerClient.Rental<IBrokeredCalculator> rental = await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		Assert.NotNull(rental.Proxy);
		Assert.Equal(2, attempts);

		async Task AcquireAsync()
		{
			using ServiceBrokerClient.Rental<IBrokeredCalculator> rental = await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: cancellation.Token);
		}
	}

	[Test]
	public async Task CacheRetriesFailedActivations()
	{
		using BrokeredServiceContainer container = new();
		int attempts = 0;
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) =>
		{
			if (++attempts == 1)
			{
				throw new InvalidOperationException("Transient activation failure");
			}

			return new(new Calculator());
		});
		using ServiceBrokerClient client = new(container.GetServiceBroker());
		await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken));
		using ServiceBrokerClient.Rental<IBrokeredCalculator> rental = await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		Assert.NotNull(rental.Proxy);
		Assert.Equal(2, attempts);
	}

	[Test]
	public async Task LocalCallbackBoundaryBorrowsClientTarget()
	{
		using BrokeredServiceContainer container = new();
		Calculator callbackTarget = new();
		IBrokeredCalculator? callbackProxy = null;
		Calculator service = new();
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = BrokeredTestServices.Descriptor with
		{
			CallbackInterface = BrokeredTestServices.Descriptor.CreateInterface<IBrokeredCalculator>("calculator.callback"),
		};
		container.Register(new(descriptor.Moniker));
		using IDisposable registration = container.Proffer<IBrokeredCalculator>(descriptor, async (_, options, _, token) =>
		{
			callbackProxy = Assert.IsAssignableFrom<IBrokeredCalculator>(options.ClientRpcTarget);
			Assert.Equal(5, await callbackProxy.AddAsync(2, 3, token));
			return service;
		});
		IBrokeredCalculator proxy = (await container.GetServiceBroker().GetProxyAsync(descriptor, new() { ClientRpcTarget = callbackTarget }, this.TimeoutToken))!;
		Assert.NotSame(callbackTarget, callbackProxy);
		await ((System.IAsyncDisposable)proxy).DisposeAsync();
		Assert.True(service.Disposed);
		Assert.False(callbackTarget.Disposed);
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await callbackProxy!.AddAsync(1, 2));
	}

	[Test]
	public async Task SetupFailureDisposesTheActivation()
	{
		using BrokeredServiceContainer container = new();
		Calculator service = new();
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(service));
		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			IBrokeredCalculator? proxy = await container.GetServiceBroker().GetProxyAsync(
				BrokeredTestServices.Calculator,
				new() { ConfigureProxy = _ => throw new InvalidOperationException("Setup failure") },
				this.TimeoutToken);
			using (proxy as IDisposable)
			{
			}
		});
		Assert.True(service.Disposed);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task MultiplexedBrokerRelaysIndependentServiceChannels(JsonRpcEncoding encoding, bool forwardViaIpc)
	{
		(IDuplexPipe first, IDuplexPipe second) = FullDuplexStream.CreatePipePair();
		Task<MultiplexingStream> firstTask = MultiplexingStream.CreateAsync(first.AsStream());
		Task<MultiplexingStream> secondTask = MultiplexingStream.CreateAsync(second.AsStream());
		await Task.WhenAll(firstTask, secondTask);
		using MultiplexingStream clientMultiplexer = await firstTask;
		using MultiplexingStream serverMultiplexer = await secondTask;
		using BrokeredServiceContainer container = new();
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = CreateCalculatorDescriptor(encoding) with { AdditionalInterfaces = [BrokeredTestServices.Subtract] };
		container.Register(new(descriptor.Moniker, additionalInterfaceIds: [BrokeredTestServices.Subtract.Id]));
		using IDisposable registration = container.Proffer(descriptor, (_, _, _, _) => new(new Calculator()));
		JsonRpcDescriptor<IOutOfBandStreamService> resourceDescriptor = CreateDescriptor<IOutOfBandStreamService>(new("resources"), encoding) with { MultiplexingStreamOptions = new() { ProtocolMajorVersion = 3 } };
		container.Register(new(resourceDescriptor.Moniker));
		using IDisposable resourceRegistration = container.Proffer(resourceDescriptor, (_, _, _, _) => new(new OutOfBandStreamService()));
		IServiceBrokerWithMetadata source = Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker());
		await using IpcRelayServiceBroker downstreamRelay = new(source);
		using RemoteServiceBroker middleBroker = await RemoteServiceBroker.ConnectAsync(downstreamRelay, this.TimeoutToken);
		using MultiplexingRelayServiceBroker relay = new(forwardViaIpc ? middleBroker : source, serverMultiplexer);
		Task<MultiplexingStream.Channel> accepted = serverMultiplexer.AcceptChannelAsync(string.Empty, this.TimeoutToken);
		MultiplexingStream.Channel controlPipe = clientMultiplexer.CreateChannel();
		JsonRpcDescriptor<IRemoteServiceBroker> control = CreateControlDescriptor(encoding);
		using JsonRpc serverRpc = control.CreateConnection(await accepted);
		control.AddRpcTarget(serverRpc, relay, []);
		serverRpc.Start();
		using RemoteServiceBroker broker = await RemoteServiceBroker.ConnectAsync(controlPipe, clientMultiplexer, control, cancellationToken: this.TimeoutToken);
		IBrokeredCalculator proxy = (await broker.GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		Assert.Equal(11, await proxy.AddAsync(7, 4, this.TimeoutToken));
		Assert.Equal(3, await Assert.IsAssignableFrom<IClientProxy>(proxy).As<IBrokeredSubtract>()!.SubtractAsync(7, 4, this.TimeoutToken));

		IOutOfBandStreamService resources = (await broker.GetProxyAsync(resourceDescriptor, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable resourceLifetime = (System.IAsyncDisposable)resources;
		using Stream returned = await resources.ReturnStreamAsync(this.TimeoutToken);
		using MemoryStream received = new();
		await returned.CopyToAsync(received, 4096, this.TimeoutToken);
		Assert.Equal("hello"u8.ToArray(), received.ToArray());
		Pipe content = new();
		await content.Writer.WriteAsync("stream"u8.ToArray(), this.TimeoutToken);
		await content.Writer.CompleteAsync();
		Assert.Equal(6, await resources.CountAsync(content.Reader, this.TimeoutToken));
		await content.Reader.CompleteAsync();
		Pipe destination = new();
		await resources.WriteAsync(destination.Writer, this.TimeoutToken);
		ReadResult result = await destination.Reader.ReadAsync(this.TimeoutToken);
		Assert.Equal("hello"u8.ToArray(), result.Buffer.ToArray());
		destination.Reader.AdvanceTo(result.Buffer.End);
		await destination.Reader.CompleteAsync();

		await resourceLifetime.DisposeAsync();
		Assert.Equal(12, await proxy.AddAsync(8, 4, this.TimeoutToken));
		Assert.False(clientMultiplexer.Completion.IsCompleted);
		Assert.False(serverMultiplexer.Completion.IsCompleted);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task IpcBrokerSupportsDedicatedServiceMultiplexing(JsonRpcEncoding encoding)
	{
		using BrokeredServiceContainer container = new();
		JsonRpcDescriptor<IOutOfBandStreamService> descriptor = CreateDescriptor<IOutOfBandStreamService>(new("resources"), encoding) with { MultiplexingStreamOptions = new() { ProtocolMajorVersion = 3 } };
		container.Register(new(descriptor.Moniker));
		using IDisposable registration = container.Proffer(descriptor, (_, _, _, _) => new(new OutOfBandStreamService()));
		await using IpcRelayServiceBroker relay = new(Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker()));
		using RemoteServiceBroker broker = await RemoteServiceBroker.ConnectAsync(relay, this.TimeoutToken);
		IOutOfBandStreamService proxy = (await broker.GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		using Stream returned = await proxy.ReturnStreamAsync(this.TimeoutToken);
		using MemoryStream received = new();
		await returned.CopyToAsync(received, 4096, this.TimeoutToken);
		Assert.Equal("hello"u8.ToArray(), received.ToArray());
	}

	[Test]
	public void DescriptorCapturesMultiplexingOptions()
	{
		MultiplexingStream.Options options = new() { ProtocolMajorVersion = 3 };
		options.SeededChannels.Add(new());
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = BrokeredTestServices.Descriptor with { MultiplexingStreamOptions = options };
		options.ProtocolMajorVersion = 4;
		options.SeededChannels.Clear();
		Assert.NotNull(descriptor.MultiplexingStreamOptions);
		Assert.True(descriptor.MultiplexingStreamOptions.IsFrozen);
		Assert.Equal(3, descriptor.MultiplexingStreamOptions.ProtocolMajorVersion);
		Assert.Single(descriptor.MultiplexingStreamOptions.SeededChannels);
		Assert.Equal(4, (descriptor with { MultiplexingStreamOptions = options }).MultiplexingStreamOptions!.ProtocolMajorVersion);
		Assert.Throws<ArgumentException>(() => descriptor with { MultiplexingStreamOptions = new() { ProtocolMajorVersion = 2 } });
		Assert.Null((descriptor with { MultiplexingStreamOptions = null }).MultiplexingStreamOptions);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task DescriptorPreservesSeededChannelIndices(JsonRpcEncoding encoding)
	{
		MultiplexingStream.Options options = new() { ProtocolMajorVersion = 3 };
		options.SeededChannels.Add(new());
		options.SeededChannels.Add(new());
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = CreateCalculatorDescriptor(encoding) with { MultiplexingStreamOptions = options };
		(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = descriptor.CreateConnection(client);
		using JsonRpc serverRpc = descriptor.CreateConnection(server);
		Assert.Throws<InvalidOperationException>(() => clientRpc.MultiplexingStream!.AcceptChannel(2));
		Assert.Throws<InvalidOperationException>(() => serverRpc.MultiplexingStream!.AcceptChannel(2));
		MultiplexingStream.Channel clientApplication = clientRpc.MultiplexingStream!.AcceptChannel(1);
		MultiplexingStream.Channel serverApplication = serverRpc.MultiplexingStream!.AcceptChannel(1);
		await clientApplication.Output.WriteAsync("hello"u8.ToArray(), this.TimeoutToken);
		await clientApplication.Output.CompleteAsync();
		using MemoryStream received = new();
		await serverApplication.Input.CopyToAsync(received, this.TimeoutToken);
		Assert.Equal("hello"u8.ToArray(), received.ToArray());
		Assert.Equal(2, descriptor.MultiplexingStreamOptions!.SeededChannels.Count);
		descriptor.AddRpcTarget(serverRpc, new Calculator(), []);
		IBrokeredCalculator proxy = descriptor.CreateProxy(clientRpc, []);
		serverRpc.Start();
		clientRpc.Start();
		Assert.Equal(5, await proxy.AddAsync(2, 3, this.TimeoutToken));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json, 0)]
	[Arguments(JsonRpcEncoding.MessagePack, 0)]
	[Arguments(JsonRpcEncoding.Json, 3)]
	[Arguments(JsonRpcEncoding.MessagePack, 3)]
	public async Task DescriptorOwnsDedicatedMultiplexer(JsonRpcEncoding encoding, int protocolVersion)
	{
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = CreateCalculatorDescriptor(encoding);
		Assert.Null(descriptor.MultiplexingStreamOptions);
		bool useMultiplexing = protocolVersion != 0;
		if (useMultiplexing)
		{
			descriptor = descriptor with { MultiplexingStreamOptions = new() { ProtocolMajorVersion = protocolVersion } };
		}

		(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = descriptor.CreateConnection(client);
		using JsonRpc serverRpc = descriptor.CreateConnection(server);
		descriptor.AddRpcTarget(serverRpc, new Calculator(), []);
		IBrokeredCalculator proxy = descriptor.CreateProxy(clientRpc, []);
		Assert.Equal(useMultiplexing, clientRpc.MultiplexingStream is not null);
		Assert.Equal(useMultiplexing, serverRpc.MultiplexingStream is not null);
		if (useMultiplexing)
		{
			Assert.NotSame(clientRpc.MultiplexingStream, serverRpc.MultiplexingStream);
		}

		serverRpc.Start();
		clientRpc.Start();
		Assert.Equal(5, await proxy.AddAsync(2, 3, this.TimeoutToken));
		clientRpc.Dispose();
		await clientRpc.Completion.WithCancellation(this.TimeoutToken);
		await serverRpc.Completion.WithCancellation(this.TimeoutToken);
		if (useMultiplexing)
		{
			await clientRpc.MultiplexingStream!.Completion.WithCancellation(this.TimeoutToken);
			await serverRpc.MultiplexingStream!.Completion.WithCancellation(this.TimeoutToken);
		}
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task DedicatedMultiplexerTransfersLargeRpcMessages(JsonRpcEncoding encoding)
	{
		using BrokeredServiceContainer container = new();
		JsonRpcDescriptor<IEchoService> descriptor = CreateDescriptor<IEchoService>(new("echo"), encoding) with { MultiplexingStreamOptions = new() { ProtocolMajorVersion = 3 } };
		container.Register(new(descriptor.Moniker));
		using IDisposable registration = container.Proffer(descriptor, (_, _, _, _) => new(new EchoService()));
		IDuplexPipe pipe = (await container.GetServiceBroker().GetPipeAsync(descriptor.Moniker, cancellationToken: this.TimeoutToken))!;
		using JsonRpc rpc = descriptor.CreateConnection(pipe);
		IEchoService proxy = descriptor.CreateProxy(rpc, []);
		rpc.Start();
		string value = new('x', 4 * 1024 * 1024);
		Assert.Equal(value, await proxy.EchoAsync(value, this.TimeoutToken));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task FailedDescriptorConstructionClosesDedicatedTransport(JsonRpcEncoding encoding)
	{
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = encoding == JsonRpcEncoding.Json
			? new JsonRpcUtf8Descriptor<IBrokeredCalculator>(new("invalid"), TypeShapeResolver.ResolveDynamicOrThrow<IBrokeredCalculator>()) { Framing = (JsonRpcJsonFraming)99 }
			: BrokeredTestServices.Descriptor with { Framing = (JsonRpcMessagePackFraming)99 };
		descriptor = descriptor with { MultiplexingStreamOptions = new() { ProtocolMajorVersion = 3 } };
		(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
		Assert.Throws<ArgumentOutOfRangeException>(() => descriptor.CreateConnection(client));
		ReadResult result;
		do
		{
			result = await server.Input.ReadAsync(this.TimeoutToken);
			server.Input.AdvanceTo(result.Buffer.End);
		}
		while (!result.IsCompleted);

		await server.Input.CompleteAsync();
		await server.Output.CompleteAsync();
	}

	[Test]
	public async Task CanceledMultiplexedAllocationReleasesItsActivation()
	{
		(IDuplexPipe first, IDuplexPipe second) = FullDuplexStream.CreatePipePair();
		Task<MultiplexingStream> firstTask = MultiplexingStream.CreateAsync(first.AsStream());
		Task<MultiplexingStream> secondTask = MultiplexingStream.CreateAsync(second.AsStream());
		await Task.WhenAll(firstTask, secondTask);
		using MultiplexingStream clientMultiplexer = await firstTask;
		using MultiplexingStream serverMultiplexer = await secondTask;
		using BrokeredServiceContainer container = new();
		using CancellationTokenSource cancellation = new();
		Calculator target = new();
		container.Register(new(BrokeredTestServices.Descriptor.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) =>
		{
			cancellation.Cancel();
			return new(target);
		});
		using MultiplexingRelayServiceBroker relay = new(Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker()), serverMultiplexer);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.RequestServiceChannelAsync(BrokeredTestServices.Calculator.Moniker, default, cancellation.Token));
		Assert.True(await target.Disposal.Task.WithCancellation(this.TimeoutToken));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task FactoryCanAwaitClientCallbackDuringRpcActivation(JsonRpcEncoding encoding)
	{
		using BrokeredServiceContainer container = new();
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = CreateCalculatorDescriptor(encoding) with
		{
			CallbackInterface = BrokeredTestServices.Descriptor.CreateInterface<IBrokeredCalculator>("calculator.callback"),
		};
		object? factoryCallback = null;
		container.Register(new(descriptor.Moniker));
		using IDisposable registration = container.Proffer<IBrokeredCalculator>(descriptor, async (moniker, options, _, token) =>
		{
			Assert.Equal(descriptor.Moniker, moniker);
			factoryCallback = options.ClientRpcTarget;
			IBrokeredCalculator callback = Assert.IsAssignableFrom<IBrokeredCalculator>(factoryCallback);
			Assert.Equal(5, await callback.AddAsync(2, 3, token));
			return new Calculator();
		});
		IDuplexPipe pipe = (await container.GetServiceBroker().GetPipeAsync(descriptor.Moniker, cancellationToken: this.TimeoutToken))!;
		using JsonRpc rpc = descriptor.CreateConnection(pipe);
		Calculator client = new();
		IBrokeredCalculator proxy = descriptor.CreateProxy(rpc, [], new() { ClientRpcTarget = client });
		rpc.Start();
		Assert.Equal(9, await proxy.AddAsync(4, 5, this.TimeoutToken));
		Assert.NotNull(factoryCallback);
		Assert.NotSame(client, factoryCallback);
		Assert.False(client.Disposed);
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task CacheSharesActivationsAfterSerializerUse(bool useJson)
	{
		using BrokeredServiceContainer container = new();
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = useJson
			? new JsonRpcUtf8Descriptor<IBrokeredCalculator>(BrokeredTestServices.Calculator.Moniker, TypeShapeResolver.ResolveDynamicOrThrow<IBrokeredCalculator>()) { Serializer = new() }
			: BrokeredTestServices.Descriptor with { Serializer = new() };
		int activations = 0;
		container.Register(new(descriptor.Moniker));
		using IDisposable registration = container.Proffer(descriptor, (_, _, _, _) =>
		{
			activations++;
			return new(new Calculator());
		});
		using ServiceBrokerClient client = new(container.GetServiceBroker());
		using ServiceBrokerClient.Rental<IBrokeredCalculator> first = await client.GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken);
		if (descriptor is JsonRpcUtf8Descriptor<IBrokeredCalculator> json)
		{
			json.Serializer.Serialize(42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
		}
		else
		{
			((JsonRpcMessagePackDescriptor<IBrokeredCalculator>)descriptor).Serializer.Serialize(42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
		}

		using ServiceBrokerClient.Rental<IBrokeredCalculator> second = await client.GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken);
		Assert.Same(first.Proxy, second.Proxy);
		Assert.Equal(1, activations);
		registration.Dispose();
		using ServiceBrokerClient.Rental<IBrokeredCalculator> unavailable = await client.GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken);
		Assert.Null(unavailable.Proxy);
		Assert.Equal(5, await first.Proxy!.AddAsync(2, 3, this.TimeoutToken));
	}

	[Test]
	public async Task CacheSeparatesDescriptorInstances()
	{
		using BrokeredServiceContainer container = new();
		container.Register(new(BrokeredTestServices.Descriptor.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator()));
		using ServiceBrokerClient client = new(container.GetServiceBroker());
		using ServiceBrokerClient.Rental<IBrokeredCalculator> first = await client.GetProxyAsync(BrokeredTestServices.Descriptor, cancellationToken: this.TimeoutToken);
		using ServiceBrokerClient.Rental<IBrokeredCalculator> second = await client.GetProxyAsync(BrokeredTestServices.Descriptor with { }, cancellationToken: this.TimeoutToken);
		Assert.NotNull(first.Proxy);
		Assert.NotNull(second.Proxy);
		Assert.NotSame(first.Proxy, second.Proxy);
	}

	[Test]
	public async Task CacheSharesActivationsAndInvalidationPreservesRentals()
	{
		using BrokeredServiceContainer container = new();
		int activations = 0;
		Calculator service = new();
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) =>
		{
			activations++;
			return new(service);
		});
		using ServiceBrokerClient client = new(container.GetServiceBroker());
		ServiceBrokerClient.Rental<IBrokeredCalculator> first = await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		ServiceBrokerClient.Rental<IBrokeredCalculator> second = await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		Assert.Same(first.Proxy, second.Proxy);
		Assert.Equal(1, activations);
		registration.Dispose();
		using ServiceBrokerClient.Rental<IBrokeredCalculator> unavailable = await client.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		Assert.Null(unavailable.Proxy);
		first.Dispose();
		Assert.False(service.Disposed);
		Assert.Equal(5, await second.Proxy!.AddAsync(2, 3));
		second.Dispose();
		Assert.True(service.Disposed);
	}

	[Test]
	public async Task AggregatorUsesFirstAvailableRegistration()
	{
		using BrokeredServiceContainer highPriority = new();
		using BrokeredServiceContainer fallback = new();
		using ServiceBrokerAggregator aggregate = new(highPriority.GetServiceBroker(), fallback.GetServiceBroker());
		fallback.Register(new(BrokeredTestServices.Calculator.Moniker, additionalInterfaceIds: [BrokeredTestServices.Subtract.Id]));
		using IDisposable registration = fallback.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(new Calculator()));
		IBrokeredCalculator proxy = (await aggregate.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken))!;
		using IDisposable lifetime = (IDisposable)proxy;
		Assert.True(Assert.IsAssignableFrom<IClientProxy>(proxy).Is(typeof(IBrokeredSubtract)));
		Assert.Equal(new[] { BrokeredTestServices.Subtract.Id }, await aggregate.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken));
		highPriority.Register(new(BrokeredTestServices.Calculator.Moniker));
		using IDisposable preferred = highPriority.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(new Calculator()));
		Assert.Empty((await aggregate.GetCapabilitiesAsync(BrokeredTestServices.Calculator.Moniker, this.TimeoutToken))!);
	}

	[Test]
	public async Task SubscriptionRetainsLocalGenerationAndIsolatesObserverExceptions()
	{
		ObserverService target = new();
		Calculator owner = new();
		ServiceProxyContext context = ServiceProxyContext.Own(target, owner);
		JsonRpcMessagePackDescriptor<IObserverService> descriptor = new(new("observer"), TypeShapeResolver.ResolveDynamicOrThrow<IObserverService>());
		IObserverService proxy = descriptor.CreateLocalProxy(context, []);
		IDisposable subscription = await proxy.SubscribeAsync(new ThrowingObserver(), this.TimeoutToken);
		target.Publish(1);
		Assert.IsNotType<ThrowingObserver>(target.Subscriber);
		context.Dispose();
		Assert.False(owner.Disposed);
		subscription.Dispose();
		subscription.Dispose();
		await context.DisposeAsync();
		Assert.True(owner.Disposed);
		Assert.True(await target.Unsubscribed.Task.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public async Task LocalEventExceptionsDoNotEscapeIntoService()
	{
		using BrokeredServiceContainer container = new();
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(new Calculator()));
		IBrokeredCalculator proxy = (await container.GetServiceBroker().GetProxyAsync(BrokeredTestServices.Calculator))!;
		using IDisposable lifetime = (IDisposable)proxy;
		object? sender = null;
		proxy.Changed += (_, _) => throw new InvalidOperationException("Client callback failure");
		proxy.Changed += (source, _) => sender = source;
		Assert.Equal(5, await proxy.AddAsync(2, 3));
		Assert.Same(proxy, sender);
	}

	[Test]
	public async Task DescriptorInfersProxyTypeAcrossAssemblies()
	{
		using BrokeredServiceContainer container = new();
		Calculator service = new();
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(service));
		IServiceBroker broker = container.GetServiceBroker();
		IBrokeredCalculator? proxy = await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		Assert.NotNull(proxy);
		Assert.NotSame(service, proxy);
		Assert.Equal(5, await proxy.AddAsync(2, 3, this.TimeoutToken));
		await ((System.IAsyncDisposable)proxy).DisposeAsync();
		Assert.True(service.Disposed);
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await proxy.AddAsync(1, 2));
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task CapabilitiesBelongToTheRegistration(bool additional)
	{
		using BrokeredServiceContainer container = new();
		Calculator service = new();
		string[] additionalInterfaces = additional ? [BrokeredTestServices.Subtract.Id] : [];
		container.Register(new(BrokeredTestServices.Calculator.Moniker, additionalInterfaceIds: additionalInterfaces));
		if (additional)
		{
			additionalInterfaces[0] = null!;
		}

		using IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(service));
		IBrokeredCalculator? proxy = await container.GetServiceBroker().GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		Assert.NotNull(proxy);
		using IDisposable lifetime = (IDisposable)proxy;
		IClientProxy capabilities = Assert.IsAssignableFrom<IClientProxy>(proxy);
		Assert.True(capabilities.Is(typeof(IBrokeredCalculator)));
		Assert.Equal(additional, capabilities.Is(typeof(IBrokeredSubtract)));
		if (additional)
		{
			Assert.Equal(7, await capabilities.As<IBrokeredSubtract>()!.SubtractAsync(10, 3, this.TimeoutToken));
		}
	}

	[Test]
	public async Task LocalExceptionsAndEventsCrossProxyBoundary()
	{
		using BrokeredServiceContainer container = new();
		Calculator service = new();
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		using IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(service));
		IBrokeredCalculator proxy = (await container.GetServiceBroker().GetProxyAsync(BrokeredTestServices.Calculator))!;
		using IDisposable lifetime = (IDisposable)proxy;
		int received = 0;
		EventHandler<int> handler = (_, value) => received = value;
		proxy.Changed += handler;
		await proxy.AddAsync(2, 3);
		Assert.Equal(5, received);
		proxy.Changed -= handler;
		await proxy.AddAsync(3, 4);
		Assert.Equal(5, received);
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(() => proxy.FailAsync());
		Assert.IsType<InvalidOperationException>(exception.InnerException);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task DescriptorConnectsBothRpcParties(JsonRpcEncoding encoding, bool alternateFraming)
	{
		JsonRpcJsonFraming jsonFraming = alternateFraming ? JsonRpcJsonFraming.NewlineDelimited : JsonRpcJsonFraming.ContentLength;
		JsonRpcMessagePackFraming messagePackFraming = alternateFraming ? JsonRpcMessagePackFraming.SelfDelimiting : JsonRpcMessagePackChannel.DefaultFraming;
		JsonRpcDescriptor<IBrokeredCalculator> original = CreateCalculatorDescriptor(encoding);
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = original switch
		{
			JsonRpcUtf8Descriptor<IBrokeredCalculator> json => json with { Framing = jsonFraming },
			JsonRpcMessagePackDescriptor<IBrokeredCalculator> messagePack => messagePack with { Framing = messagePackFraming },
			_ => throw new InvalidOperationException(),
		};
		Assert.Equal(original.Moniker, descriptor.Moniker);
		if (original is JsonRpcUtf8Descriptor<IBrokeredCalculator> jsonOriginal)
		{
			Assert.Equal(JsonRpcJsonFraming.ContentLength, jsonOriginal.Framing);
		}
		else
		{
			Assert.Equal(JsonRpcMessagePackChannel.DefaultFraming, Assert.IsType<JsonRpcMessagePackDescriptor<IBrokeredCalculator>>(original).Framing);
		}

		(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
		using JsonRpc serverRpc = new(encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(server, new Nerdbank.Json.JsonSerializer(), jsonFraming)
			: new JsonRpcMessagePackChannel(server, JsonRpcMessagePackChannel.DefaultSerializer, messagePackFraming));
		Calculator target = new();
		descriptor.AddRpcTarget(serverRpc, target, [BrokeredTestServices.Subtract]);
		using JsonRpc clientRpc = descriptor.CreateConnection(client);
		IBrokeredCalculator proxy = descriptor.CreateProxy(clientRpc, [BrokeredTestServices.Subtract]);
		using IDisposable lifetime = (IDisposable)proxy;
		TaskCompletionSource<int> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		proxy.Changed += (_, value) => changed.TrySetResult(value);
		clientRpc.Start();
		serverRpc.Start();
		Assert.Equal(12, await proxy.AddAsync(7, 5, this.TimeoutToken));
		Assert.Equal(12, await changed.Task.WithCancellation(this.TimeoutToken));
		Assert.Equal(2, await Assert.IsAssignableFrom<IClientProxy>(proxy).As<IBrokeredSubtract>()!.SubtractAsync(7, 5, this.TimeoutToken));
		await Assert.ThrowsAsync<RemoteInvocationException>(() => proxy.FailAsync(this.TimeoutToken));
	}

	[Test]
	public async Task RegistrationDisposalDoesNotDestroyIssuedServices()
	{
		using BrokeredServiceContainer container = new();
		IServiceBroker broker = container.GetServiceBroker();
		IBrokeredCalculator? beforeRegistration = await broker.GetProxyAsync(BrokeredTestServices.Calculator);
		using (beforeRegistration as IDisposable)
		{
			Assert.Null(beforeRegistration);
		}

		int changes = 0;
		broker.AvailabilityChanged += (_, _) => changes++;
		Calculator service = new();
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(service));
		Assert.Equal(1, changes);
		IBrokeredCalculator proxy = (await broker.GetProxyAsync(BrokeredTestServices.Calculator))!;
		registration.Dispose();
		Assert.Equal(2, changes);
		Assert.False(service.Disposed);
		Assert.Equal(3, await proxy.AddAsync(1, 2));
		IBrokeredCalculator? afterRegistration = await broker.GetProxyAsync(BrokeredTestServices.Calculator);
		using (afterRegistration as IDisposable)
		{
			Assert.Null(afterRegistration);
		}

		await ((System.IAsyncDisposable)proxy).DisposeAsync();
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task RemoteBrokerRelaysCapabilitiesAndCalls(JsonRpcEncoding encoding)
	{
		using BrokeredServiceContainer container = new();
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = CreateCalculatorDescriptor(encoding) with { AdditionalInterfaces = [BrokeredTestServices.Subtract] };
		container.Register(new(descriptor.Moniker, additionalInterfaceIds: [BrokeredTestServices.Subtract.Id]));
		using IDisposable registration = container.Proffer(descriptor, (_, _, _, _) => new(new Calculator()));
		await using IpcRelayServiceBroker relay = new(Assert.IsAssignableFrom<IServiceBrokerWithMetadata>(container.GetServiceBroker()));
		(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
		JsonRpcDescriptor<IRemoteServiceBroker> control = CreateControlDescriptor(encoding);
		using JsonRpc serverRpc = control.CreateConnection(server);
		control.AddRpcTarget(serverRpc, relay, []);
		serverRpc.Start();
		using RemoteServiceBroker broker = await RemoteServiceBroker.ConnectAsync(client, control, cancellationToken: this.TimeoutToken);
		IBrokeredCalculator proxy = (await broker.GetProxyAsync(descriptor, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		Assert.Equal(12, await proxy.AddAsync(7, 5, this.TimeoutToken));
		Assert.Equal(2, await Assert.IsAssignableFrom<IClientProxy>(proxy).As<IBrokeredSubtract>()!.SubtractAsync(7, 5, this.TimeoutToken));
		IBrokeredCalculator? missing = await broker.GetProxyAsync(descriptor with { Moniker = new("missing") }, cancellationToken: this.TimeoutToken);
		using (missing as IDisposable)
		{
			Assert.Null(missing);
		}
	}

	[Test]
	public async Task ResilientProxyLosesAndRegainsServiceAndEvents()
	{
		using BrokeredServiceContainer container = new();
		Calculator first = new();
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		IDisposable registration = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(first));
		await using ResilientServiceProxy<IBrokeredCalculator> resilient = await container.GetServiceBroker().GetResilientProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken);
		Assert.True(resilient.IsAvailable);
		int eventValue = 0;
		resilient.Proxy.Changed += (_, value) => eventValue = value;
		Assert.Equal(3, await resilient.Proxy.AddAsync(1, 2));
		Assert.Equal(3, eventValue);
		TaskCompletionSource<bool> lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> gained = new(TaskCreationOptions.RunContinuationsAsynchronously);
		resilient.BackingServiceChanged += (_, args) =>
		{
			if (args.Kind == ResilientServiceProxyChangeKind.Lost)
			{
				lost.TrySetResult(true);
			}
			else if (args.Kind == ResilientServiceProxyChangeKind.Gained)
			{
				gained.TrySetResult(true);
			}
		};
		registration.Dispose();
		await lost.Task.WithCancellation(this.TimeoutToken);
		Assert.False(resilient.IsAvailable);
		await Assert.ThrowsAsync<BrokeredServiceUnavailableException>(async () => await resilient.Proxy.AddAsync(2, 3));
		using IDisposable replacement = container.Proffer(BrokeredTestServices.Calculator, (_, _, _, _) => new(new Calculator()));
		await gained.Task.WithCancellation(this.TimeoutToken);
		Assert.True(resilient.IsAvailable);
		Assert.Equal(7, await resilient.Proxy.AddAsync(3, 4));
		Assert.Equal(7, eventValue);
		Assert.True(first.Disposed);
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static WeakReference SubscribeCollectibleHandler(IServiceBroker broker)
	{
		object target = new();
		broker.AvailabilityChanged += (_, _) => GC.KeepAlive(target);
		return new(target);
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static WeakReference CreateCollectibleBrokerView(BrokeredServiceContainer container, string subscription)
	{
		IServiceBroker view = container.GetServiceBroker();
		EventHandler<BrokeredServicesChangedEventArgs> handler = static (_, _) => { };
		if (subscription != "none")
		{
			view.AvailabilityChanged += handler;
		}

		if (subscription == "removed")
		{
			view.AvailabilityChanged -= handler;
		}
		else if (subscription == "disposed")
		{
			((IDisposable)view).Dispose();
		}

		return new(view);
	}

	private static void CollectBrokerViews()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
	}

	private static JsonRpcDescriptor<T> CreateDescriptor<T>(ServiceMoniker moniker, JsonRpcEncoding encoding)
		where T : class => encoding switch
		{
			JsonRpcEncoding.Json => JsonRpcUtf8Descriptor.Create(moniker, TypeShapeResolver.ResolveDynamicOrThrow<T>()),
			JsonRpcEncoding.MessagePack => JsonRpcMessagePackDescriptor.Create(moniker, TypeShapeResolver.ResolveDynamicOrThrow<T>()),
			_ => throw new ArgumentOutOfRangeException(nameof(encoding)),
		};

	private static JsonRpcDescriptor<IBrokeredCalculator> CreateCalculatorDescriptor(JsonRpcEncoding encoding)
		=> CreateDescriptor<IBrokeredCalculator>(BrokeredTestServices.Descriptor.Moniker, encoding) with { AdditionalInterfaces = BrokeredTestServices.Descriptor.AdditionalInterfaces };

	private static JsonRpcDescriptor<IRemoteServiceBroker> CreateControlDescriptor(JsonRpcEncoding encoding)
		=> CreateDescriptor<IRemoteServiceBroker>(FrameworkServices.RemoteServiceBroker.Moniker, encoding);

	private sealed class AsyncLifetimeService : IAsyncLifetimeService
	{
		public event EventHandler Changed
		{
			add => this.Subscriptions++;
			remove => this.Subscriptions--;
		}

		internal int Subscriptions { get; private set; }

		internal int Disposals { get; private set; }

		public ValueTask DisposeAsync()
		{
			this.Disposals++;
			return default;
		}
	}

	private sealed class Calculator : IBrokeredCalculator, IBrokeredSubtract, IDisposable
	{
		public event EventHandler<int>? Changed
		{
			add
			{
				this.ChangedCore += value;
				this.OnEventAttachment?.Invoke();
				if (this.FailEventAttachment)
				{
					throw new InvalidOperationException("Event attachment failed after adding the handler.");
				}
			}

			remove => this.ChangedCore -= value;
		}

		private event EventHandler<int>? ChangedCore;

		public Exception? Failure { get; init; }

		internal bool Disposed { get; private set; }

		internal bool FailEventAttachment { get; init; }

		internal Action? OnEventAttachment { get; set; }

		internal int EventHandlerCount => this.ChangedCore?.GetInvocationList().Length ?? 0;

		internal TaskCompletionSource<bool> Disposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public ValueTask<int> AddAsync(int left, int right, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			this.ChangedCore?.Invoke(this, left + right);
			return new(left + right);
		}

		public ValueTask<int> SubtractAsync(int left, int right, CancellationToken cancellationToken) => new(left - right);

		public Task FailAsync(CancellationToken cancellationToken) => Task.FromException(this.Failure ?? new InvalidOperationException("Service failure"));

		public void Dispose()
		{
			this.Disposed = true;
			this.Disposal.TrySetResult(true);
		}
	}

	private sealed class EchoService : IEchoService
	{
		public Task<string> EchoAsync(string value, CancellationToken cancellationToken) => Task.FromResult(value);

		public void Notify(string value, CancellationToken cancellationToken)
		{
		}

		public Task<int> DoubleAsync(int value, CancellationToken cancellationToken) => Task.FromResult(value * 2);
	}

	private sealed class RegistrationDiscoveryContainer : BrokeredServiceContainer
	{
		private int registrationLookups;

		internal int RegistrationLookups => this.registrationLookups;

		internal Func<ServiceMoniker, CancellationToken, ValueTask> LoadRegistrations { get; set; } = (_, _) => default;

		protected override ValueTask OnMissingServiceRegistrationAsync(ServiceMoniker moniker, CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref this.registrationLookups);
			return this.LoadRegistrations(moniker, cancellationToken);
		}
	}

	private sealed class DelayedDiscoveryContainer : BrokeredServiceContainer
	{
		internal bool DiscoverOffering { get; init; } = true;

		internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		protected override async ValueTask<ServiceOffering?> TryGetServiceOfferingAsync(ServiceMoniker moniker, CancellationToken cancellationToken)
		{
			this.Started.TrySetResult(true);
			await this.Release.Task.WithCancellation(cancellationToken);
			return this.DiscoverOffering ? ServiceOffering.Create(BrokeredTestServices.Descriptor, (_, _, _, _) => new(new Calculator())) : null;
		}
	}

	private sealed class AlternatingContainer(IJsonRpcDescriptor<IBrokeredCalculator> descriptor, BrokeredServiceFactory<IBrokeredCalculator> factory) : BrokeredServiceContainer
	{
		internal int Lookups { get; private set; }

		protected override ValueTask<ServiceOffering?> TryGetServiceOfferingAsync(ServiceMoniker moniker, CancellationToken cancellationToken)
		{
			this.Lookups++;
			return new(ServiceOffering.Create(descriptor, factory));
		}
	}

	private sealed class DiscoveringContainer : BrokeredServiceContainer
	{
		private ServiceOffering? offering;

		internal List<(Exception? Exception, bool Available)> Requests { get; } = [];

		internal void SetOffering<T>(IJsonRpcDescriptor<T> descriptor, BrokeredServiceFactory<T> factory)
			where T : class => this.offering = ServiceOffering.Create(descriptor, factory);

		internal void SignalChange() => this.NotifyAvailabilityChanged(BrokeredTestServices.Calculator.Moniker);

		protected override ValueTask<ServiceOffering?> TryGetServiceOfferingAsync(ServiceMoniker moniker, CancellationToken cancellationToken) => new(this.offering);

		protected override void OnRequestHandled(ServiceMoniker moniker, Exception? exception, bool available) => this.Requests.Add((exception, available));
	}

	private sealed record ContextRecordingDescriptor() : JsonRpcMessagePackDescriptor<IBrokeredCalculator>(BrokeredTestServices.Calculator.Moniker, TypeShapeResolver.ResolveDynamicOrThrow<IBrokeredCalculator>())
	{
		internal Action<ServiceRpcContext?>? ConnectionCreated { get; init; }

		public override JsonRpc CreateConnection(IDuplexPipe pipe, ServiceRpcContext? context = null)
		{
			this.ConnectionCreated?.Invoke(context);
			return base.CreateConnection(pipe, context);
		}
	}

	private sealed class LocalBoundaryService : ILocalBoundaryService, IDisposable
	{
		private readonly Pipe pipe = new();

		internal IObserver<int>? Observer { get; private set; }

		internal bool Disposed { get; private set; }

		public Task<PipeReader> GetReaderAsync(CancellationToken cancellationToken) => Task.FromResult(this.pipe.Reader);

		public Task<PipeWriter> GetWriterAsync(CancellationToken cancellationToken) => Task.FromResult(this.pipe.Writer);

		public Task<IDuplexPipe> GetPipeAsync(CancellationToken cancellationToken) => Task.FromResult<IDuplexPipe>(new DuplexPipe(this.pipe.Reader, this.pipe.Writer));

		public IAsyncEnumerable<int> EnumerateAsync(int failure, CancellationToken cancellationToken)
			=> failure == 3 ? throw new InvalidOperationException("Enumerator acquisition failure") : this.IterateAsync(failure, cancellationToken);

		public IAsyncEnumerable<int> EnumerateWithObserverAsync(IObserver<int> observer, int failure, CancellationToken cancellationToken)
		{
			this.Observer = observer;
			return this.EnumerateAsync(failure, cancellationToken);
		}

		public void Dispose()
		{
			this.Disposed = true;
			this.pipe.Reader.Complete();
			this.pipe.Writer.Complete();
		}

		private async IAsyncEnumerable<int> IterateAsync(int failure, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
		{
			try
			{
				if (failure == 1)
				{
					throw new InvalidOperationException("Move failure");
				}

				yield return 1;
				if (failure == 0)
				{
					await Task.Delay(Timeout.Infinite, cancellationToken);
				}
			}
			finally
			{
				if (failure == 2)
				{
					throw new InvalidOperationException("Disposal failure");
				}
			}
		}
	}

	private sealed class SequenceBroker(params object[] proxies) : IServiceBroker
	{
		private readonly Queue<object> remaining = new(proxies);

		public event EventHandler<BrokeredServicesChangedEventArgs>? AvailabilityChanged;

		internal ServiceMoniker Moniker { get; init; } = BrokeredTestServices.Calculator.Moniker;

		public ValueTask<T?> GetProxyAsync<T>(IJsonRpcDescriptor<T> descriptor, ServiceActivationOptions options = default, CancellationToken cancellationToken = default)
			where T : class
		{
			cancellationToken.ThrowIfCancellationRequested();
			lock (this.remaining)
			{
				return new(this.remaining.Count == 0 ? null : Assert.IsAssignableFrom<T>(this.remaining.Dequeue()));
			}
		}

		public ValueTask<IDuplexPipe?> GetPipeAsync(ServiceMoniker moniker, ServiceActivationOptions options = default, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		internal void SignalChange() => this.AvailabilityChanged?.Invoke(this, new(
			System.Collections.Immutable.ImmutableHashSet.Create(this.Moniker)));
	}

	private sealed class ThrowingObserver : IObserver<int>
	{
		public void OnCompleted() => throw new InvalidOperationException("Observer failure");

		public void OnError(Exception error) => throw new InvalidOperationException("Observer failure");

		public void OnNext(int value) => throw new InvalidOperationException("Observer failure");
	}
}
