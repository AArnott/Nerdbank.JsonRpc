// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Nerdbank.MessagePack;

namespace Benchmarks;

/// <summary>Measures argument construction in the same shape as generated proxies.</summary>
[MemoryDiagnoser]
public class ArgumentBuilderBenchmarks
{
	private JsonRpc rpc = null!;

	/// <summary>Gets or sets the codec.</summary>
	[Params(JsonRpcEncoding.Json, JsonRpcEncoding.MessagePack)]
	public JsonRpcEncoding Encoding { get; set; }

	/// <summary>Gets or sets a value indicating whether arguments are named.</summary>
	[Params(false, true)]
	public bool Named { get; set; }

	/// <summary>Gets or sets the number of arguments.</summary>
	[Params(0, 2, 6)]
	public int Count { get; set; }

	/// <summary>Configures the serializer and validates the benchmark input.</summary>
	[GlobalSetup]
	public void Setup()
	{
		JsonRpcSerializer serializer = this.Encoding == JsonRpcEncoding.Json ? new JsonSerializerPlugin(new Nerdbank.Json.JsonSerializer()) : new MessagePackSerializerPlugin(new MessagePackSerializer());
		this.rpc = new JsonRpc(Channel.CreateUnbounded<JsonRpcMessage>()) { Serializer = serializer };
		if (!this.Build().HasValue)
		{
			throw new InvalidOperationException("The result must be present.");
		}
	}

	/// <summary>Builds a set of arguments.</summary>
	/// <returns>The owned parameters.</returns>
	[Benchmark]
	public JsonRpcValue Build()
	{
		using JsonRpcArgumentsBuilder builder = this.rpc.CreateArguments(this.Named, this.Count);
		for (int i = 0; i < this.Count; i++)
		{
			builder.Add(this.Named ? "arg" + i : null, i, PolyType.SourceGenerator.TypeShapeProvider_Benchmarks.Default.Int32);
		}

		return builder.Build();
	}
}
