// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Nerdbank.Streams;
using PolyType;

using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests;

public partial class FlexibleNamedArgumentTests
{
	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial interface IFlexibleTarget
	{
		ValueTask<string> DescribeAsync(int a, int b, int c = 5);

		ValueTask<string> EchoRequiredAsync([ParameterShape(IsRequired = true)] string name, string? suffix);

		ValueTask<string> EchoRequiredWithDefaultAsync([ParameterShape(IsRequired = true)] string name = "fallback");
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task Disabled_RejectsUnknownAndMissingNamedArguments(JsonRpcEncoding encoding)
	{
		(JsonRpc client, JsonRpc server) = Create(encoding, new JsonRpcTargetOptions());
		using (client)
		using (server)
		{
			RemoteInvocationException ex = await Assert.ThrowsAsync<RemoteInvocationException>(async () => await InvokeAsync(client, ("a", 1), ("extra", 2)));
			Assert.Contains("extra", ex.Message);
			await Assert.ThrowsAsync<RemoteInvocationException>(async () => await InvokeAsync(client, ("a", 1)));
		}
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task Enabled_IgnoresUnknownNamedArguments(JsonRpcEncoding encoding)
	{
		(JsonRpc client, JsonRpc server) = Create(encoding, new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true });
		using (client)
		using (server)
		{
			Assert.Equal("1,2,5", await InvokeAsync(client, ("a", 1), ("b", 2), ("unknown", 99)));
		}
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task Enabled_DefaultsMissingArguments(JsonRpcEncoding encoding)
	{
		(JsonRpc client, JsonRpc server) = Create(encoding, new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true });
		using (client)
		using (server)
		{
			Assert.Equal("0,0,5", await InvokeAsync(client, ("unknown", 1)));
			Assert.Equal("7,0,8", await InvokeAsync(client, ("c", 8), ("a", 7)));
		}
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task Enabled_RequiredAttributeMustBeSupplied(JsonRpcEncoding encoding)
	{
		(JsonRpc client, JsonRpc server) = Create(encoding, new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true });
		using (client)
		using (server)
		{
			Assert.Equal("x:", await InvokeAsync(client, "echoRequired", ("name", "x"), ("junk", 1)));
			await Assert.ThrowsAsync<RemoteInvocationException>(async () => await InvokeAsync(client, "echoRequired", ("junk", 1)));
			await Assert.ThrowsAsync<RemoteInvocationException>(async () => await InvokeAsync(client, "echoRequiredWithDefault", ("junk", 1)));
			Assert.Equal("d", await InvokeAsync(client, "echoRequiredWithDefault", ("name", "d")));
		}
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task Enabled_DoesNotAffectPositionalArguments(JsonRpcEncoding encoding)
	{
		(JsonRpc client, JsonRpc server) = Create(encoding, new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true });
		using (client)
		using (server)
		{
			JsonRpcValue arguments;
			using (JsonRpcArgumentsBuilder builder = client.CreateArguments(named: false, count: 1))
			{
				builder.Add("a", 1, ShapeProvider.Default.Int32);
				arguments = builder.Build();
			}

			using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
			await Assert.ThrowsAsync<RemoteInvocationException>(async () => await client.RequestAsync("describe", arguments, ShapeProvider.Default.String, cts.Token));
		}
	}

	private static ValueTask<string> InvokeAsync(JsonRpc client, params (string Name, object Value)[] args)
		=> InvokeAsync(client, "describe", args);

	private static async ValueTask<string> InvokeAsync(JsonRpc client, string method, params (string Name, object Value)[] args)
	{
		JsonRpcValue arguments;
		using (JsonRpcArgumentsBuilder builder = client.CreateArguments(named: true, count: args.Length))
		{
			foreach ((string name, object value) in args)
			{
				switch (value)
				{
					case int i:
						builder.Add(name, i, ShapeProvider.Default.Int32);
						break;
					case string s:
						builder.Add(name, s, ShapeProvider.Default.String);
						break;
				}
			}

			arguments = builder.Build();
		}

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		return await client.RequestAsync(method, arguments, ShapeProvider.Default.String, cts.Token);
	}

	private static (JsonRpc Client, JsonRpc Server) Create(JsonRpcEncoding encoding, JsonRpcTargetOptions options)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		JsonRpc client = new(CreateChannel(clientPipe, encoding));
		JsonRpc server = new(CreateChannel(serverPipe, encoding));
		server.AddRpcTarget<IFlexibleTarget>(new FlexibleTarget(), options);
		server.Start();
		client.Start();
		return (client, server);
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
		=> encoding switch
		{
			JsonRpcEncoding.MessagePack => new JsonRpcMessagePackChannel(pipe),
			JsonRpcEncoding.Json => new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited),
			_ => throw new ArgumentOutOfRangeException(nameof(encoding)),
		};

	private sealed class FlexibleTarget : IFlexibleTarget
	{
		public ValueTask<string> DescribeAsync(int a, int b, int c = 5) => new($"{a},{b},{c}");

		public ValueTask<string> EchoRequiredAsync(string name, string? suffix) => new($"{name}:{suffix}");

		public ValueTask<string> EchoRequiredWithDefaultAsync(string name = "fallback") => new(name);
	}
}
