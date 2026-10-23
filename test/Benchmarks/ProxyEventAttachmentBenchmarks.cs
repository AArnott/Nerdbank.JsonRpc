// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Nerdbank.JsonRpc;
using Nerdbank.Streams;
using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IEventAttachmentContract
{
	event EventHandler<int>? Changed;

	event Action<int, string>? Updated;
}

[MemoryDiagnoser]
public class ProxyEventAttachmentBenchmarks
{
	[Benchmark]
	public void Attach()
	{
		(IDuplexPipe pipe, _) = FullDuplexStream.CreatePipePair();
		using JsonRpc rpc = new(new JsonRpcMessagePackChannel(pipe));
		IEventAttachmentContract proxy = rpc.Attach<IEventAttachmentContract>();
		proxy.Changed += static (_, _) => { };
	}
}
