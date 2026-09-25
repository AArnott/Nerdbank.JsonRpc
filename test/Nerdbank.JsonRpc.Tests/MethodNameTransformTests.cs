// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests;

/// <summary>
/// Tests for <see cref="CommonMethodNameTransforms"/> and how method name transforms are applied
/// by <see cref="JsonRpcTargetOptions"/> (server dispatch) and <see cref="JsonRpcProxyOptions"/> (generated client proxies).
/// </summary>
public class MethodNameTransformTests : TestBase
{
	[Test]
	[Arguments(nameof(ICalculator.AddAsync), "add")]
	[Arguments(nameof(ICalculator.PingAsync), "ping")]
	public void Default_RemovesAsyncSuffixAndCamelCases(string clrName, string expected)
	{
		Assert.Equal(expected, CommonMethodNameTransforms.Default(clrName));
	}

	[Test]
	[Arguments(nameof(ICalculator.AddAsync), nameof(ICalculator.AddAsync))]
	[Arguments(nameof(ICalculator.PingAsync), nameof(ICalculator.PingAsync))]
	public void Identity_ReturnsMethodNameUnchanged(string clrName, string expected)
	{
		Assert.Equal(expected, CommonMethodNameTransforms.Identity(clrName));
	}

	[Test]
	[Arguments("GetValueAsync", "GetValue")]
	[Arguments("Async", "Async")]
	[Arguments("ping", "ping")]
	public void RemoveAsyncSuffix_OnlyRemovesTrailingAsync(string clrName, string expected)
	{
		Assert.Equal(expected, CommonMethodNameTransforms.RemoveAsyncSuffix(clrName));
	}

	[Test]
	[Arguments("GetValue", "getValue")]
	[Arguments("get", "get")]
	[Arguments("", "")]
	public void CamelCase_LowercasesOnlyFirstCharacter(string clrName, string expected)
	{
		Assert.Equal(expected, CommonMethodNameTransforms.CamelCase(clrName));
	}

	[Test]
	public void JsonRpcTargetOptions_MethodNameTransform_DefaultsToCommonDefault()
	{
		Assert.Same(CommonMethodNameTransforms.Default, new JsonRpcTargetOptions().MethodNameTransform);
	}

	[Test]
	public void JsonRpcTargetOptions_MethodNameTransform_RejectsNull()
	{
		Assert.Throws<ArgumentNullException>(() => new JsonRpcTargetOptions { MethodNameTransform = null! });
	}

	[Test]
	public void JsonRpcProxyOptions_MethodNameTransform_DefaultsToCommonDefault()
	{
		Assert.Same(CommonMethodNameTransforms.Default, new JsonRpcProxyOptions().MethodNameTransform);
	}

	[Test]
	public void JsonRpcProxyOptions_MethodNameTransform_RejectsNull()
	{
		Assert.Throws<ArgumentNullException>(() => new JsonRpcProxyOptions { MethodNameTransform = null! });
	}

	[Test]
	public async Task AddRpcTarget_DefaultTransform_DispatchesUnderCamelCaseName()
	{
		using JsonRpc client = await this.ConnectCalculatorAsync(new Calculator(), new JsonRpcTargetOptions());
		Assert.Equal(7, await this.RequestAddAsync(client, "add"));
	}

	[Test]
	public async Task AddRpcTarget_IdentityTransform_DispatchesUnderClrName()
	{
		using JsonRpc client = await this.ConnectCalculatorAsync(new Calculator(), new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.Identity });
		Assert.Equal(7, await this.RequestAddAsync(client, nameof(ICalculator.AddAsync)));
	}

	[Test]
	public async Task AddRpcTarget_CustomTransform_AppliesProvidedFunction()
	{
		using JsonRpc client = await this.ConnectCalculatorAsync(new Calculator(), new JsonRpcTargetOptions { MethodNameTransform = name => $"a.{name}" });
		Assert.Equal(7, await this.RequestAddAsync(client, $"a.{nameof(ICalculator.AddAsync)}"));
	}

	[Test]
	public async Task AddRpcTarget_ExplicitName_IsAuthoritativeAndDispatchesVerbatim()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc client = new(new JsonRpcMessagePackChannel(clientPipe, NullLogger.Instance));
		using JsonRpc server = new(new JsonRpcMessagePackChannel(serverPipe, NullLogger.Instance));

		// Even with a transform that would otherwise mangle the name, the explicit name must be used verbatim.
		server.AddRpcTarget<IExplicitNameCalculator>(new ExplicitNameCalculator(), new JsonRpcTargetOptions { MethodNameTransform = name => name.ToUpperInvariant() });
		client.Start();
		server.Start();

		int result = await client.RequestAsync(
			"custom\nadd",
			new JsonDirectArgs { A = 3, B = 4 },
			ShapeProvider.Default.JsonDirectArgs,
			ShapeProvider.Default.Int32,
			this.TimeoutToken);
		Assert.Equal(7, result);
	}

	[Test]
	public void AddRpcTarget_TransformReturningNull_Throws()
	{
		using JsonRpc server = new(new MockJsonRpcPipeChannel(MockChannel<JsonRpcMessage>.CreatePair().Item1));
		Assert.Throws<InvalidOperationException>(() => server.AddRpcTarget<ICalculator>(new Calculator(), new JsonRpcTargetOptions { MethodNameTransform = _ => null! }));
	}

	[Test]
	public void AddRpcTarget_TransformReturningEmpty_Throws()
	{
		using JsonRpc server = new(new MockJsonRpcPipeChannel(MockChannel<JsonRpcMessage>.CreatePair().Item1));
		Assert.Throws<InvalidOperationException>(() => server.AddRpcTarget<ICalculator>(new Calculator(), new JsonRpcTargetOptions { MethodNameTransform = _ => string.Empty }));
	}

	[Test]
	public void AddRpcTarget_CollisionWithExistingTarget_ThrowsWithoutPartialRegistration()
	{
		using JsonRpc server = new(new MockJsonRpcPipeChannel(MockChannel<JsonRpcMessage>.CreatePair().Item1));
		server.AddRpcTarget<IFooAsyncTarget>(new FooAsyncTarget());

		Assert.Throws<InvalidOperationException>(() => server.AddRpcTarget<ICollidingNamesTarget>(new CollidingNamesTarget()));
		Assert.Throws<InvalidOperationException>(() => server.AddRpcTarget<IFooTarget>(new FooTarget()));
	}

	[Test]
	public void AddRpcTarget_CollidingTransformedNames_Throws()
	{
		using JsonRpc server = new(new MockJsonRpcPipeChannel(MockChannel<JsonRpcMessage>.CreatePair().Item1));

		// FooAsync and Foo both transform to "foo" under the default transform.
		Assert.Throws<InvalidOperationException>(() => server.AddRpcTarget<ICollidingNamesTarget>(new CollidingNamesTarget()));
	}

	[Test]
	public async Task GeneratedProxy_CustomTransform_UsesMethodShapeContext()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc clientRpc = new(new MockJsonRpcPipeChannel(transport));
		clientRpc.Start();
		ICalculator client = clientRpc.Attach<ICalculator>(new JsonRpcProxyOptions { MethodNameTransform = name => $"a.{name}" });

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<int> resultTask = client.AddAsync(1, 2, cts.Token).AsTask();

		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));
		Assert.Equal($"a.{nameof(ICalculator.AddAsync)}", request.Method);

		await remote.Writer.WriteAsync(
			new JsonRpcResult
			{
				Id = request.Id!.Value,
				Result = (RawMessagePack)((IJsonRpcClient)clientRpc).Serializer.Serialize(3, ShapeProvider.Default.Int32, cts.Token),
			},
			cts.Token);
		Assert.Equal(3, await resultTask.WithCancellation(cts.Token));
	}

	[Test]
	public async Task GeneratedProxy_ExplicitName_SendsExactNameRegardlessOfTransform()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc clientRpc = new(new MockJsonRpcPipeChannel(transport));
		clientRpc.Start();

		// Even with a custom transform configured, the explicit name must be used verbatim.
		IExplicitNameCalculator client = clientRpc.Attach<IExplicitNameCalculator>(new JsonRpcProxyOptions { MethodNameTransform = name => name.ToUpperInvariant() });

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<int> resultTask = client.AddAsync(1, 2, cts.Token).AsTask();

		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));
		Assert.Equal("custom\nadd", request.Method);

		await remote.Writer.WriteAsync(
			new JsonRpcResult
			{
				Id = request.Id!.Value,
				Result = (RawMessagePack)((IJsonRpcClient)clientRpc).Serializer.Serialize(3, ShapeProvider.Default.Int32, cts.Token),
			},
			cts.Token);
		Assert.Equal(3, await resultTask.WithCancellation(cts.Token));
	}

	[Test]
	public async Task GeneratedProxy_IdentityTransform_SendsClrName()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc clientRpc = new(new MockJsonRpcPipeChannel(transport));
		clientRpc.Start();
		ICalculator client = clientRpc.Attach<ICalculator>(new JsonRpcProxyOptions { MethodNameTransform = CommonMethodNameTransforms.Identity });

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<int> resultTask = client.AddAsync(1, 2, cts.Token).AsTask();

		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));
		Assert.Equal(nameof(ICalculator.AddAsync), request.Method);

		await remote.Writer.WriteAsync(
			new JsonRpcResult
			{
				Id = request.Id!.Value,
				Result = (RawMessagePack)((IJsonRpcClient)clientRpc).Serializer.Serialize(3, ShapeProvider.Default.Int32, cts.Token),
			},
			cts.Token);
		Assert.Equal(3, await resultTask.WithCancellation(cts.Token));
	}

	private async Task<JsonRpc> ConnectCalculatorAsync(Calculator calculator, JsonRpcTargetOptions options)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		JsonRpcMessagePackChannel clientChannel = new(clientPipe, NullLogger.Instance);
		JsonRpcMessagePackChannel serverChannel = new(serverPipe, NullLogger.Instance);

		JsonRpc clientRpc = new(clientChannel);
		clientRpc.Start();

		JsonRpc serverRpc = new(serverChannel);
		serverRpc.AddRpcTarget<ICalculator>(calculator, options);
		serverRpc.Start();

		await Task.CompletedTask;
		return clientRpc;
	}

	private async Task<int> RequestAddAsync(JsonRpc client, string method)
	{
		return await client.RequestAsync(
			method,
			new JsonDirectArgs { A = 3, B = 4 },
			ShapeProvider.Default.JsonDirectArgs,
			ShapeProvider.Default.Int32,
			this.TimeoutToken);
	}
}
