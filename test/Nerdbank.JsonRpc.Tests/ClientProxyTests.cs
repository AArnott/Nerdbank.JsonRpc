// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.VisualStudio.Threading;
using Nerdbank.JsonRpc.TestContracts;
using Nerdbank.Streams;

public class ClientProxyTests
{
	[Test]
	[Arguments(false, false)]
	[Arguments(false, true)]
	[Arguments(true, false)]
	[Arguments(true, true)]
	public async Task RootProxyReportsConfiguredCapabilities(bool enableOptional, bool nonGenericAttach)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe));
		serverRpc.AddRpcTarget<ICalculator>(new Calculator());
		if (enableOptional)
		{
			serverRpc.AddRpcTarget<ISubtractCapability>(new SubtractOptionalObject());
		}

		serverRpc.Start();
		clientRpc.Start();
		JsonRpcProxyOptions options = new()
		{
			EnabledInterfaces = enableOptional ? [typeof(ISubtractCapability)] : [],
		};
		IGroupedRootService client = nonGenericAttach
			? (IGroupedRootService)clientRpc.Attach(typeof(IGroupedRootService), options)
			: clientRpc.Attach<IGroupedRootService>(options);
		IClientProxy proxy = Assert.IsAssignableFrom<IClientProxy>(client);
		Assert.Equal(enableOptional, client is ISubtractCapability);
		Assert.True(proxy.Is(typeof(ICalculator)));
		Assert.Equal(enableOptional, proxy.Is(typeof(ISubtractCapability)));
		Assert.Equal(enableOptional, client.Is(typeof(ISubtractCapability)));
		Assert.Equal(enableOptional, client.As<ISubtractCapability>() is not null);
		Assert.True(proxy.Is(typeof(IGroupedRootService)));
		Assert.Same(client, client.As<IGroupedRootService>());
		Assert.Same(client, proxy.As<ICalculator>());
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Assert.Equal(5, await proxy.As<ICalculator>()!.AddAsync(2, 3, cts.Token));
		if (enableOptional)
		{
			Assert.Same(client, proxy.As<ISubtractCapability>());
			Assert.Equal(7, await proxy.As<ISubtractCapability>()!.CalculateAsync(3, cts.Token));
		}
		else
		{
			Assert.Null(proxy.As<ISubtractCapability>());
		}
	}

	[Test]
	public void DefaultEnablesImplementedContractsOnly()
	{
		(MockChannel<JsonRpcMessage> transport, _) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(transport));
		IClientProxy proxy = Assert.IsAssignableFrom<IClientProxy>(rpc.Attach<ICapabilityService>());
		Assert.True(proxy.Is(typeof(ICapabilityService)));
		Assert.True(proxy.Is(typeof(ICalculator)));
		Assert.True(proxy.Is(typeof(ISubtractCapability)));
		Assert.False(proxy.Is(typeof(IMultiplyCapability)));
		Assert.False(proxy.Is(typeof(IClientProxy)));
		Assert.False(proxy.Is(typeof(object)));
		Assert.False(proxy.Is(typeof(string)));
		Assert.Throws<ArgumentNullException>(() => proxy.Is(null!));
		Assert.Null(proxy.As<IMultiplyCapability>());
		Assert.Null(proxy.As<object>());
		Assert.Null(((IClientProxy?)null).As<ICalculator>());
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public void BatchProxyHonorsEnabledInterfaces(bool nonGenericAttach)
	{
		(MockChannel<JsonRpcMessage> transport, _) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(transport));
		using JsonRpcBatch batch = rpc.CreateBatch();
		JsonRpcProxyOptions options = new() { EnabledInterfaces = [] };
		object client = nonGenericAttach ? batch.Attach(typeof(ICompositeCalculator), options) : batch.Attach<ICompositeCalculator>(options);
		IClientProxy proxy = Assert.IsAssignableFrom<IClientProxy>(client);
		Assert.True(proxy.Is(typeof(ICompositeCalculator)));
		Assert.True(proxy.Is(typeof(ICalculator)));
		Assert.False(proxy.Is(typeof(ISubtractCapability)));
	}

	[Test]
	public void PrimaryAndInheritedInterfacesRemainEnabledWithEmptyOrUnknownCapabilities()
	{
		(MockChannel<JsonRpcMessage> transport, _) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(transport));
		IClientProxy empty = Assert.IsAssignableFrom<IClientProxy>(rpc.Attach<ICapabilityService>(new() { EnabledInterfaces = [] }));
		Assert.True(empty.Is(typeof(ICapabilityService)));
		Assert.True(empty.Is(typeof(ICalculator)));
		Assert.True(empty.Is(typeof(ISubtractCapability)));
		Assert.Same(empty, empty.As<ICapabilityService>());
		Assert.Same(empty, empty.As<ICalculator>());
		IClientProxy unknown = Assert.IsAssignableFrom<IClientProxy>(rpc.Attach<ICalculator>(new() { EnabledInterfaces = [typeof(IMultiplyCapability)] }));
		Assert.True(unknown.Is(typeof(ICalculator)));
		Assert.False(unknown.Is(typeof(IMultiplyCapability)));
	}

	[Test]
	public void OptionsSnapshotEnabledInterfacesAndSupportRecordCopies()
	{
		Type[] enabled = [typeof(ICalculator)];
		JsonRpcProxyOptions options = new() { EnabledInterfaces = enabled };
		enabled[0] = typeof(ISubtractCapability);
		Assert.Equal(typeof(ICalculator), Assert.Single(options.EnabledInterfaces!));
		Assert.Throws<NotSupportedException>(() => ((IList<Type>)options.EnabledInterfaces!)[0] = typeof(ISubtractCapability));
		Assert.Null((options with { EnabledInterfaces = null }).EnabledInterfaces);
		Assert.Empty((options with { EnabledInterfaces = [] }).EnabledInterfaces!);
		Assert.Null(JsonRpcProxyOptions.Default.EnabledInterfaces);
	}

	[Test]
	public void InterfaceExtensionsWorkOnLocalAndNullObjects()
	{
		ICalculator local = new Calculator();
		Assert.True(local.Is(typeof(ICalculator)));
		Assert.Same(local, local.As<ICalculator>());
		Assert.False(local.Is(typeof(ISubtractCapability)));
		Assert.Null(local.As<ISubtractCapability>());
		ICalculator? missing = null;
		Assert.False(missing.Is(typeof(ICalculator)));
		Assert.Null(missing.As<ICalculator>());
		Assert.Throws<ArgumentNullException>(() => local.Is(null!));
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public void RootGroupsSelectRequestedCapabilities(bool nonGeneric)
	{
		(MockChannel<JsonRpcMessage> transport, _) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(transport));
		IGroupedRootService basic = rpc.Attach<IGroupedRootService>();
		Assert.False(basic is ISubtractCapability);
		JsonRpcProxyOptions options = new() { EnabledInterfaces = [typeof(ICalculator), typeof(ISubtractCapability)] };
		IGroupedRootService extended = nonGeneric ? (IGroupedRootService)rpc.Attach(typeof(IGroupedRootService), options) : rpc.Attach<IGroupedRootService>(options);
		Assert.True(extended is ISubtractCapability);
		Assert.True(extended.Is(typeof(ISubtractCapability)));
		Assert.True(extended.Is(typeof(IGroupedRootService)));
		Assert.Same(extended, extended.As<ICalculator>());
	}

	[Test]
	public async Task RootGroupEventsRetainGeneratedRegistration()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe));
		GroupedEventSource source = new();
		serverRpc.AddRpcTarget<IGroupedEventCapability>(source);
		IGroupedRootService client = clientRpc.Attach<IGroupedRootService>(new() { EnabledInterfaces = [typeof(ICalculator), typeof(IGroupedEventCapability)] });
		TaskCompletionSource<int> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
		client.As<IGroupedEventCapability>()!.Changed += (sender, value) => received.TrySetResult(value);
		serverRpc.Start();
		clientRpc.Start();
		source.Raise(42);
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Assert.Equal(42, await received.Task.WithCancellation(cts.Token));
	}

	[Test]
	public void RootGroupsWithoutCoveringSupersetFailExplicitly()
	{
		(MockChannel<JsonRpcMessage> transport, _) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(transport));
		NotSupportedException exception = Assert.Throws<NotSupportedException>(() => rpc.Attach<IGroupedRootService>(new()
		{
			EnabledInterfaces = [typeof(ISubtractCapability), typeof(IGroupedEventCapability)],
		}));
		Assert.Contains(nameof(JsonRpcProxyInterfaceGroupAttribute), exception.Message);
	}

	[Test]
	public async Task GroupedMethodsPreserveInterfaceSpecificWireNames()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe));
		NamedOperations target = new();
		serverRpc.AddRpcTarget<IFirstNamedOperation>(target);
		serverRpc.AddRpcTarget<ISecondNamedOperation>(target);
		serverRpc.Start();
		clientRpc.Start();
		IFirstNamedOperation client = clientRpc.Attach<IFirstNamedOperation>();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Assert.Equal(1, await client.ExecuteAsync(cts.Token));
		Assert.Equal(2, await client.As<ISecondNamedOperation>()!.ExecuteAsync(cts.Token));
		Assert.Equal(3, await client.DescribeAsync(cts.Token));
		Assert.Equal("second", await client.As<ISecondNamedOperation>()!.DescribeAsync(cts.Token));
	}

	[Test]
	public async Task GroupedMethodsImplementEachInheritedDeclaration()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe));
		InheritedOperations target = new();
		serverRpc.AddRpcTarget<IInheritedOperations>(target);
		serverRpc.Start();
		clientRpc.Start();
		IInheritedGroupedRoot client = clientRpc.Attach<IInheritedGroupedRoot>();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Assert.Equal(1, await client.As<IFirstOperation>()!.PingAsync(cts.Token));
		Assert.Equal(2, await client.As<ISecondOperation>()!.PingAsync(cts.Token));
	}

	[Test]
	public async Task GroupedMembersResolveShapesFromReferencedContractAssembly()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe));
		Assert.Null(PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.GetTypeShape(typeof(ExternalPayload)));
		Assert.Null(PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.GetTypeShape(typeof(Action<ExternalPayload>)));
		ExternalCapability target = new();
		serverRpc.AddRpcTarget<IExternalCapability>(target);
		IExternalCapability client = clientRpc.Attach<IExternalGroupedRoot>().As<IExternalCapability>()!;
		TaskCompletionSource<string?> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
		client.ExternalChanged += value => received.TrySetResult(value.Text);
		serverRpc.Start();
		clientRpc.Start();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		ExternalPayload result = await client.EchoAsync(new ExternalPayload { Text = "payload" }, cts.Token);
		Assert.Equal("payload", result.Text);
		Assert.Equal("payload", await received.Task.WithCancellation(cts.Token));
	}

	[Test]
	[Arguments(false, false)]
	[Arguments(false, true)]
	[Arguments(true, false)]
	[Arguments(true, true)]
	public void RootAndBatchGroupsCoverInheritedEnabledCapabilities(bool batch, bool sharedBase)
	{
		(MockChannel<JsonRpcMessage> transport, _) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(transport));
		using JsonRpcBatch rpcBatch = rpc.CreateBatch();
		JsonRpcProxyOptions options = new() { EnabledInterfaces = [sharedBase ? typeof(IClientCapability) : typeof(IUnknownDerivedSubtractCapability)] };
		IGroupedInheritanceRoot client = batch ? rpcBatch.Attach<IGroupedInheritanceRoot>(options) : rpc.Attach<IGroupedInheritanceRoot>(options);
		Assert.Equal(!sharedBase, client.As<ISubtractCapability>() is not null);
		Assert.Null(client.As<IUnknownDerivedSubtractCapability>());
		Assert.Null(client.As<IDerivedSubtractCapability>());
		if (sharedBase)
		{
			Assert.NotNull(client.As<ICommonCapability>());
		}
	}

	[Test]
	public async Task WitnessBackedGroupedMethodsAndEventsUsePrimaryProvider()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe));
		WitnessCapability target = new();
		serverRpc.AddRpcTarget<IWitnessCapability>(target, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.IWitnessCapability);
		IWitnessCapability client = clientRpc.Attach<IWitnessGroupedRoot>().As<IWitnessCapability>()!;
		TaskCompletionSource<int> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
		client.WitnessChanged += value => received.TrySetResult(value.Value);
		serverRpc.Start();
		clientRpc.Start();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Assert.Equal(42, (await client.EchoWitnessAsync(new WitnessPayload { Value = 42 }, cts.Token)).Value);
		Assert.Equal(42, await received.Task.WithCancellation(cts.Token));
	}

	[Test]
	public void OptionsRejectInvalidEnabledInterfaces()
	{
		Assert.Throws<ArgumentException>(() => new JsonRpcProxyOptions { EnabledInterfaces = [typeof(string)] });
		Assert.Throws<ArgumentException>(() => new JsonRpcProxyOptions { EnabledInterfaces = [null!] });
	}

	private sealed class GroupedEventSource : IGroupedEventCapability
	{
		public event EventHandler<int>? Changed;

		internal void Raise(int value) => this.Changed?.Invoke(this, value);
	}

	private sealed class NamedOperations : IFirstNamedOperation, ISecondNamedOperation
	{
		Task<int> IFirstNamedOperation.ExecuteAsync(CancellationToken cancellationToken) => Task.FromResult(1);

		Task<int> ISecondNamedOperation.ExecuteAsync(CancellationToken cancellationToken) => Task.FromResult(2);

		Task<int> IFirstNamedOperation.DescribeAsync(CancellationToken cancellationToken) => Task.FromResult(3);

		Task<string> ISecondNamedOperation.DescribeAsync(CancellationToken cancellationToken) => Task.FromResult("second");
	}

	private sealed class InheritedOperations : IInheritedOperations
	{
		Task<int> IFirstOperation.PingAsync(CancellationToken cancellationToken) => Task.FromResult(1);

		Task<int> ISecondOperation.PingAsync(CancellationToken cancellationToken) => Task.FromResult(2);
	}

	private sealed class ExternalCapability : IExternalCapability
	{
		public event Action<ExternalPayload>? ExternalChanged;

		public Task<ExternalPayload> EchoAsync(ExternalPayload value, CancellationToken cancellationToken)
		{
			this.ExternalChanged?.Invoke(value);
			return Task.FromResult(value);
		}
	}

	private sealed class WitnessCapability : IWitnessCapability
	{
		public event Action<WitnessPayload>? WitnessChanged;

		public Task<WitnessPayload> EchoWitnessAsync(WitnessPayload value, CancellationToken cancellationToken)
		{
			this.WitnessChanged?.Invoke(value);
			return Task.FromResult(value);
		}
	}
}
