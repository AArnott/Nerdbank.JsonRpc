// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NET8_0_OR_GREATER

using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Nerdbank.Streams;
using PolyType;
using StreamRpc = StreamJsonRpc.JsonRpc;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IProgressInterop
{
	Task Run(IProgress<int> progress, CancellationToken cancellationToken);
}

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IAsyncEnumerableInterop
{
	IAsyncEnumerable<int> Produce(CancellationToken cancellationToken);

	Task<int> Sum(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
}

public class StreamJsonRpcInteropTests : TestBase
{
	[Test]
	public async Task Progress_StreamJsonRpcClient_NerdbankServer()
	{
		using StreamInteropFixture fixture = StreamInteropFixture.CreateNerdbankServer(new ProgressTarget());
		List<int> reports = [];
		await fixture.StreamRpc.InvokeAsync("run", new object[] { new Progress<int>(reports.Add) });
		Assert.Equal(new[] { 1, 2, 3 }, reports.OrderBy(static value => value));
	}

	[Test]
	public async Task AsyncEnumerable_NerdbankClient_StreamJsonRpcServer()
	{
		using StreamInteropFixture fixture = StreamInteropFixture.CreateStreamServer(new SequenceTarget());
		List<int> values = [];
		await foreach (int value in fixture.NerdbankSequenceClient.Produce(this.TimeoutToken))
		{
			values.Add(value);
		}

		Assert.Equal(new[] { 0, 1, 2 }, values);
	}

	[Test]
	public async Task AsyncEnumerable_StreamJsonRpcClient_NerdbankServer()
	{
		using StreamInteropFixture fixture = StreamInteropFixture.CreateNerdbankServer(new SequenceTarget());
		IAsyncEnumerable<int> values = await fixture.StreamRpc.InvokeAsync<IAsyncEnumerable<int>>("produce");
		List<int> received = [];
		await foreach (int value in values)
		{
			received.Add(value);
		}

		Assert.Equal(new[] { 0, 1, 2 }, received);
	}

	private sealed class ProgressTarget : IProgressInterop
	{
		[StreamJsonRpc.JsonRpcMethod("run")]
		public Task Run(IProgress<int> progress, CancellationToken cancellationToken)
		{
			progress.Report(1);
			progress.Report(2);
			progress.Report(3);
			return Task.CompletedTask;
		}
	}

	private sealed class SequenceTarget : IAsyncEnumerableInterop
	{
		[StreamJsonRpc.JsonRpcMethod("produce")]
		public async IAsyncEnumerable<int> Produce([EnumeratorCancellation] CancellationToken cancellationToken)
		{
			for (int i = 0; i < 3; i++)
			{
				yield return i;
			}

			await Task.CompletedTask;
		}

		[StreamJsonRpc.JsonRpcMethod("sum")]
		public async Task<int> Sum(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
		{
			int sum = 0;
			await foreach (int value in values.WithCancellation(cancellationToken))
			{
				sum += value;
			}

			return sum;
		}
	}

	private sealed class StreamInteropFixture : IDisposable
	{
		private readonly JsonRpc? nerdbankRpc;
		private readonly StreamRpc streamRpc;

		private StreamInteropFixture(StreamRpc streamRpc, JsonRpc? nerdbankRpc, IProgressInterop? progressClient, IAsyncEnumerableInterop? sequenceClient)
		{
			this.streamRpc = streamRpc;
			this.nerdbankRpc = nerdbankRpc;
			this.NerdbankProgressClient = progressClient!;
			this.NerdbankSequenceClient = sequenceClient!;
		}

		internal IProgressInterop NerdbankProgressClient { get; }

		internal IAsyncEnumerableInterop NerdbankSequenceClient { get; }

		internal StreamRpc StreamRpc => this.streamRpc;

		public void Dispose()
		{
			this.streamRpc.Dispose();
			this.nerdbankRpc?.Dispose();
		}

		internal static StreamInteropFixture CreateStreamServer(object target)
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
			StreamRpc streamRpc = new(CreateHandler(serverPipe));
			streamRpc.AddLocalRpcTarget(target);
			streamRpc.StartListening();
			JsonRpc nerdbankRpc = new(new JsonRpcJsonChannel(clientPipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
			nerdbankRpc.Start();
			return new(streamRpc, nerdbankRpc, nerdbankRpc.Attach<IProgressInterop>(), nerdbankRpc.Attach<IAsyncEnumerableInterop>());
		}

		internal static StreamInteropFixture CreateNerdbankServer(object target)
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
			JsonRpc nerdbankRpc = new(new JsonRpcJsonChannel(serverPipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
			if (target is IProgressInterop progressTarget)
			{
				nerdbankRpc.AddRpcTarget(progressTarget);
			}

			if (target is IAsyncEnumerableInterop sequenceTarget)
			{
				nerdbankRpc.AddRpcTarget(sequenceTarget);
			}

			nerdbankRpc.Start();
			StreamRpc streamRpc = new(CreateHandler(clientPipe));
			streamRpc.StartListening();
			return new(streamRpc, nerdbankRpc, null, null);
		}

		private static StreamJsonRpc.IJsonRpcMessageHandler CreateHandler(IDuplexPipe pipe)
			=> new StreamJsonRpc.NewLineDelimitedMessageHandler(pipe, new StreamJsonRpc.JsonMessageFormatter());
	}
}

#endif
