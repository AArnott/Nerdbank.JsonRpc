// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;
using StreamRpc = StreamJsonRpc.JsonRpc;

/// <summary>A marshalable interface understood by both libraries.</summary>
[Nerdbank.JsonRpc.RpcMarshalable]
[StreamJsonRpc.RpcMarshalable]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICounter : IDisposable
{
	Task<int> Add(int value, CancellationToken cancellationToken);
}

/// <summary>The contract exercised by the interop tests.</summary>
[GenerateJsonRpcProxy]
[StreamJsonRpc.JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IInteropContract
{
	Task<int> AddNumbers(int a, int b, CancellationToken cancellationToken);

	Task Report(IProgress<int> progress, CancellationToken cancellationToken);

	Task Observe(IObserver<int> observer, CancellationToken cancellationToken);

	Task<ICounter> GetCounter(CancellationToken cancellationToken);

	Task<int> UseCounter(ICounter counter, CancellationToken cancellationToken);

	IAsyncEnumerable<int> Produce(CancellationToken cancellationToken);

	Task<int> Sum(IAsyncEnumerable<int> values, CancellationToken cancellationToken);

	Task<int> CountBytes(PipeReader reader, CancellationToken cancellationToken);

	Task WriteBytes(PipeWriter writer, CancellationToken cancellationToken);

	Task<int> EchoDuplex(IDuplexPipe pipe, CancellationToken cancellationToken);
}

/// <summary>
/// Verifies that Nerdbank.JsonRpc is wire-compatible with StreamJsonRpc for every exotic type,
/// with Nerdbank.JsonRpc acting as both the client and the server.
/// </summary>
public class StreamJsonRpcInteropTests : TestBase
{
	[Test]
	public async Task Primitives_NerdbankClient()
	{
		using InteropFixture fixture = InteropFixture.StreamJsonRpcServer();
		Assert.Equal(5, await fixture.NerdbankClient.AddNumbers(2, 3, this.TimeoutToken));
	}

	[Test]
	public async Task Primitives_NerdbankServer()
	{
		using InteropFixture fixture = InteropFixture.NerdbankServer();
		Assert.Equal(5, await fixture.StreamJsonRpcClient.AddNumbers(2, 3, this.TimeoutToken));
	}

	[Test]
	public async Task Progress_NerdbankClient()
	{
		using InteropFixture fixture = InteropFixture.StreamJsonRpcServer();
		RecordingProgress reports = new();
		await fixture.NerdbankClient.Report(reports, this.TimeoutToken);
		Assert.Equal(new[] { 1, 2, 3 }, await reports.Values.WaitForAsync(3, this.TimeoutToken));
	}

	[Test]
	public async Task Progress_NerdbankServer()
	{
		using InteropFixture fixture = InteropFixture.NerdbankServer();
		RecordingProgress reports = new();
		await fixture.StreamJsonRpcClient.Report(reports, this.TimeoutToken);
		Assert.Equal(new[] { 1, 2, 3 }, await reports.Values.WaitForAsync(3, this.TimeoutToken));
	}

	[Test]
	public async Task Observer_NerdbankClient()
	{
		using InteropFixture fixture = InteropFixture.StreamJsonRpcServer();
		RecordingObserver observer = new();
		await fixture.NerdbankClient.Observe(observer, this.TimeoutToken);
		Assert.Equal(new[] { 1, 2, 3 }, await observer.Values.WaitForAsync(3, this.TimeoutToken));
		await observer.Completed.WithCancellation(this.TimeoutToken);
	}

	[Test]
	public async Task Observer_NerdbankServer()
	{
		using InteropFixture fixture = InteropFixture.NerdbankServer();
		RecordingObserver observer = new();
		await fixture.StreamJsonRpcClient.Observe(observer, this.TimeoutToken);
		Assert.Equal(new[] { 1, 2, 3 }, await observer.Values.WaitForAsync(3, this.TimeoutToken));
		await observer.Completed.WithCancellation(this.TimeoutToken);
	}

	[Test]
	public async Task MarshalableReturnValue_NerdbankClient()
	{
		using InteropFixture fixture = InteropFixture.StreamJsonRpcServer();
		using ICounter counter = await fixture.NerdbankClient.GetCounter(this.TimeoutToken);
		Assert.Equal(5, await counter.Add(5, this.TimeoutToken));
		Assert.Equal(9, await counter.Add(4, this.TimeoutToken));
	}

	[Test]
	public async Task MarshalableReturnValue_NerdbankServer()
	{
		using InteropFixture fixture = InteropFixture.NerdbankServer();
		using ICounter counter = await fixture.StreamJsonRpcClient.GetCounter(this.TimeoutToken);
		Assert.Equal(5, await counter.Add(5, this.TimeoutToken));
		Assert.Equal(9, await counter.Add(4, this.TimeoutToken));
	}

	[Test]
	public async Task MarshalableArgument_NerdbankClient()
	{
		using InteropFixture fixture = InteropFixture.StreamJsonRpcServer();
		using Counter counter = new();
		Assert.Equal(3, await fixture.NerdbankClient.UseCounter(counter, this.TimeoutToken));
		Assert.Equal(3, counter.Value);
	}

	[Test]
	public async Task MarshalableArgument_NerdbankServer()
	{
		using InteropFixture fixture = InteropFixture.NerdbankServer();
		using Counter counter = new();
		Assert.Equal(3, await fixture.StreamJsonRpcClient.UseCounter(counter, this.TimeoutToken));
		Assert.Equal(3, counter.Value);
	}

	[Test]
	public async Task AsyncEnumerableResult_NerdbankClient()
	{
		using InteropFixture fixture = InteropFixture.StreamJsonRpcServer();
		List<int> values = [];
		await foreach (int value in fixture.NerdbankClient.Produce(this.TimeoutToken))
		{
			values.Add(value);
		}

		Assert.Equal(new[] { 0, 1, 2 }, values);
	}

	[Test]
	public async Task AsyncEnumerableResult_NerdbankServer()
	{
		using InteropFixture fixture = InteropFixture.NerdbankServer();
		List<int> values = [];
		await foreach (int value in fixture.StreamJsonRpcClient.Produce(this.TimeoutToken))
		{
			values.Add(value);
		}

		Assert.Equal(new[] { 0, 1, 2 }, values);
	}

	[Test]
	public async Task AsyncEnumerableArgument_NerdbankClient()
	{
		using InteropFixture fixture = InteropFixture.StreamJsonRpcServer();
		Assert.Equal(6, await fixture.NerdbankClient.Sum(Range(1, 3), this.TimeoutToken));
	}

	[Test]
	public async Task AsyncEnumerableArgument_NerdbankServer()
	{
		using InteropFixture fixture = InteropFixture.NerdbankServer();
		Assert.Equal(6, await fixture.StreamJsonRpcClient.Sum(Range(1, 3), this.TimeoutToken));
	}

	[Test]
	public async Task OutOfBandPipeReader_NerdbankClient()
	{
		using InteropFixture fixture = await InteropFixture.StreamJsonRpcServerAsync();
		Assert.Equal(5, await fixture.NerdbankClient.CountBytes(CreateContent("hello"u8), this.TimeoutToken));
	}

	[Test]
	public async Task OutOfBandPipeReader_NerdbankServer()
	{
		using InteropFixture fixture = await InteropFixture.NerdbankServerAsync();
		Assert.Equal(5, await fixture.StreamJsonRpcClient.CountBytes(CreateContent("hello"u8), this.TimeoutToken));
	}

	[Test]
	public async Task OutOfBandPipeWriter_NerdbankClient()
	{
		using InteropFixture fixture = await InteropFixture.StreamJsonRpcServerAsync();
		Pipe pipe = new();
		await fixture.NerdbankClient.WriteBytes(pipe.Writer, this.TimeoutToken);
		Assert.Equal("hello"u8.ToArray(), await ReadAllAsync(pipe.Reader, this.TimeoutToken));
	}

	[Test]
	public async Task OutOfBandPipeWriter_NerdbankServer()
	{
		using InteropFixture fixture = await InteropFixture.NerdbankServerAsync();
		Pipe pipe = new();
		await fixture.StreamJsonRpcClient.WriteBytes(pipe.Writer, this.TimeoutToken);
		Assert.Equal("hello"u8.ToArray(), await ReadAllAsync(pipe.Reader, this.TimeoutToken));
	}

	[Test]
	public async Task OutOfBandDuplexPipe_NerdbankClient()
	{
		using InteropFixture fixture = await InteropFixture.StreamJsonRpcServerAsync();
		Pipe content = new();
		await content.Writer.WriteAsync("hello"u8.ToArray(), this.TimeoutToken);
		await content.Writer.CompleteAsync();
		Assert.Equal(5, await fixture.NerdbankClient.EchoDuplex(new DuplexPipe(content.Reader, content.Writer), this.TimeoutToken));
	}

	[Test]
	public async Task OutOfBandDuplexPipe_NerdbankServer()
	{
		using InteropFixture fixture = await InteropFixture.NerdbankServerAsync();
		Pipe content = new();
		await content.Writer.WriteAsync("hello"u8.ToArray(), this.TimeoutToken);
		await content.Writer.CompleteAsync();
		Assert.Equal(5, await fixture.StreamJsonRpcClient.EchoDuplex(new DuplexPipe(content.Reader, content.Writer), this.TimeoutToken));
	}

	private static PipeReader CreateContent(ReadOnlySpan<byte> content)
	{
		Pipe pipe = new();
		content.CopyTo(pipe.Writer.GetSpan(content.Length));
		pipe.Writer.Advance(content.Length);
		pipe.Writer.Complete();
		return pipe.Reader;
	}

	private static async Task<byte[]> ReadAllAsync(PipeReader reader, CancellationToken cancellationToken)
	{
		while (true)
		{
			ReadResult result = await reader.ReadAsync(cancellationToken);
			if (result.Buffer.Length > 0 || result.IsCompleted)
			{
				byte[] bytes = BuffersExtensions.ToArray(result.Buffer);
				reader.AdvanceTo(result.Buffer.End);
				return bytes;
			}

			reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
		}
	}

	private static async IAsyncEnumerable<int> Range(int start, int count)
	{
		for (int i = 0; i < count; i++)
		{
			yield return start + i;
		}

		await Task.CompletedTask;
	}

	/// <summary>A target implementation shared by both libraries' servers.</summary>
	private sealed class InteropTarget : IInteropContract
	{
		public Task<int> AddNumbers(int a, int b, CancellationToken cancellationToken) => Task.FromResult(a + b);

		public Task Report(IProgress<int> progress, CancellationToken cancellationToken)
		{
			progress.Report(1);
			progress.Report(2);
			progress.Report(3);
			return Task.CompletedTask;
		}

		public Task Observe(IObserver<int> observer, CancellationToken cancellationToken)
		{
			observer.OnNext(1);
			observer.OnNext(2);
			observer.OnNext(3);
			observer.OnCompleted();
			return Task.CompletedTask;
		}

		public Task<ICounter> GetCounter(CancellationToken cancellationToken) => Task.FromResult<ICounter>(new Counter());

		public async Task<int> UseCounter(ICounter counter, CancellationToken cancellationToken)
		{
			await counter.Add(1, cancellationToken);
			return await counter.Add(2, cancellationToken);
		}

		public async IAsyncEnumerable<int> Produce([EnumeratorCancellation] CancellationToken cancellationToken)
		{
			for (int i = 0; i < 3; i++)
			{
				yield return i;
			}

			await Task.CompletedTask;
		}

		public async Task<int> Sum(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
		{
			int sum = 0;
			await foreach (int value in values.WithCancellation(cancellationToken))
			{
				sum += value;
			}

			return sum;
		}

		public async Task<int> CountBytes(PipeReader reader, CancellationToken cancellationToken)
		{
			int count = 0;
			while (true)
			{
				ReadResult result = await reader.ReadAsync(cancellationToken);
				count += (int)result.Buffer.Length;
				reader.AdvanceTo(result.Buffer.End);
				if (result.IsCompleted)
				{
					return count;
				}
			}
		}

		public async Task WriteBytes(PipeWriter writer, CancellationToken cancellationToken)
		{
			await writer.WriteAsync("hello"u8.ToArray(), cancellationToken);
			await writer.CompleteAsync();
		}

		public async Task<int> EchoDuplex(IDuplexPipe pipe, CancellationToken cancellationToken)
		{
			int count = await this.CountBytes(pipe.Input, cancellationToken);
			await pipe.Output.CompleteAsync();
			return count;
		}
	}

	private sealed class Counter : ICounter
	{
		private int value;

		internal int Value => Volatile.Read(ref this.value);

		public Task<int> Add(int amount, CancellationToken cancellationToken) => Task.FromResult(Interlocked.Add(ref this.value, amount));

		public void Dispose()
		{
		}
	}

	/// <summary>Records progress reports in arrival order, unlike <see cref="Progress{T}"/> which dispatches asynchronously.</summary>
	private sealed class RecordingProgress : IProgress<int>
	{
		internal AsyncCollector<int> Values { get; } = new();

		public void Report(int value) => this.Values.Add(value);
	}

	private sealed class RecordingObserver : IObserver<int>
	{
		private readonly TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal AsyncCollector<int> Values { get; } = new();

		internal Task Completed => this.completed.Task;

		public void OnCompleted() => this.completed.TrySetResult(true);

		public void OnError(Exception error) => this.completed.TrySetException(error);

		public void OnNext(int value) => this.Values.Add(value);
	}

	/// <summary>Collects values that may arrive after the originating RPC call completes.</summary>
	private sealed class AsyncCollector<T>
	{
		private readonly object sync = new();
		private readonly List<T> values = [];
		private TaskCompletionSource<bool> signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal void Add(T value)
		{
			lock (this.sync)
			{
				this.values.Add(value);
				this.signal.TrySetResult(true);
			}
		}

		internal async Task<T[]> WaitForAsync(int count, CancellationToken cancellationToken)
		{
			while (true)
			{
				Task signalTask;
				lock (this.sync)
				{
					if (this.values.Count >= count)
					{
						return [.. this.values];
					}

					if (this.signal.Task.IsCompleted)
					{
						this.signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
					}

					signalTask = this.signal.Task;
				}

				await signalTask.WithCancellation(cancellationToken);
			}
		}
	}

	/// <summary>
	/// Connects a Nerdbank.JsonRpc endpoint to a StreamJsonRpc endpoint over newline-delimited JSON,
	/// with both configured to use StreamJsonRpc's default (untransformed) method naming.
	/// </summary>
	private sealed class InteropFixture : IDisposable
	{
		/// <summary>Matches StreamJsonRpc's default (verbatim) naming for the top-level contract. Marshaled objects need no such configuration.</summary>
		private static readonly JsonRpcProxyOptions NerdbankProxyOptions = new() { MethodNameTransform = CommonMethodNameTransforms.Identity };

		/// <inheritdoc cref="NerdbankProxyOptions"/>
		private static readonly JsonRpcTargetOptions NerdbankTargetOptions = new() { MethodNameTransform = CommonMethodNameTransforms.Identity };

		private readonly JsonRpc nerdbankRpc;
		private readonly StreamRpc streamJsonRpc;
		private readonly IDisposable[] multiplexers;

		private InteropFixture(JsonRpc nerdbankRpc, StreamRpc streamJsonRpc, IInteropContract client, params IDisposable[] multiplexers)
		{
			this.nerdbankRpc = nerdbankRpc;
			this.streamJsonRpc = streamJsonRpc;
			this.multiplexers = multiplexers;
			this.NerdbankClient = client;
			this.StreamJsonRpcClient = client;
		}

		/// <summary>Gets a proxy generated by Nerdbank.JsonRpc that talks to a StreamJsonRpc server.</summary>
		internal IInteropContract NerdbankClient { get; }

		/// <summary>Gets a proxy generated by StreamJsonRpc that talks to a Nerdbank.JsonRpc server.</summary>
		internal IInteropContract StreamJsonRpcClient { get; }

		public void Dispose()
		{
			this.streamJsonRpc.Dispose();
			this.nerdbankRpc.Dispose();
			foreach (IDisposable multiplexer in this.multiplexers)
			{
				multiplexer.Dispose();
			}
		}

		internal static InteropFixture StreamJsonRpcServer()
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
			StreamRpc streamJsonRpc = new(CreateHandler(serverPipe));
			streamJsonRpc.AddLocalRpcTarget<IInteropContract>(new InteropTarget(), null);
			streamJsonRpc.StartListening();

			JsonRpc nerdbankRpc = CreateNerdbankRpc(clientPipe);
			nerdbankRpc.Start();
			return new(nerdbankRpc, streamJsonRpc, nerdbankRpc.Attach<IInteropContract>(NerdbankProxyOptions));
		}

		internal static InteropFixture NerdbankServer()
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
			JsonRpc nerdbankRpc = CreateNerdbankRpc(serverPipe);
			nerdbankRpc.AddRpcTarget<IInteropContract>(new InteropTarget(), NerdbankTargetOptions);
			nerdbankRpc.Start();

			StreamRpc streamJsonRpc = new(CreateHandler(clientPipe));
			IInteropContract client = streamJsonRpc.Attach<IInteropContract>();
			streamJsonRpc.StartListening();
			return new(nerdbankRpc, streamJsonRpc, client);
		}

		/// <summary>Creates a fixture whose endpoints are multiplexed, enabling out-of-band streams.</summary>
		internal static async Task<InteropFixture> StreamJsonRpcServerAsync()
		{
			(MultiplexingStream nerdbankMx, MultiplexingStream streamMx, IDuplexPipe nerdbankPipe, IDuplexPipe streamPipe) = await CreateMultiplexedPairAsync();
			StreamRpc streamJsonRpc = new(CreateHandler(streamPipe, streamMx));
			streamJsonRpc.AddLocalRpcTarget<IInteropContract>(new InteropTarget(), null);
			streamJsonRpc.StartListening();

			JsonRpc nerdbankRpc = CreateNerdbankRpc(nerdbankPipe, nerdbankMx);
			nerdbankRpc.Start();
			return new(nerdbankRpc, streamJsonRpc, nerdbankRpc.Attach<IInteropContract>(NerdbankProxyOptions), nerdbankMx, streamMx);
		}

		/// <summary>Creates a fixture whose endpoints are multiplexed, enabling out-of-band streams.</summary>
		internal static async Task<InteropFixture> NerdbankServerAsync()
		{
			(MultiplexingStream nerdbankMx, MultiplexingStream streamMx, IDuplexPipe nerdbankPipe, IDuplexPipe streamPipe) = await CreateMultiplexedPairAsync();
			JsonRpc nerdbankRpc = CreateNerdbankRpc(nerdbankPipe, nerdbankMx);
			nerdbankRpc.AddRpcTarget<IInteropContract>(new InteropTarget(), NerdbankTargetOptions);
			nerdbankRpc.Start();

			StreamRpc streamJsonRpc = new(CreateHandler(streamPipe, streamMx));
			IInteropContract client = streamJsonRpc.Attach<IInteropContract>();
			streamJsonRpc.StartListening();
			return new(nerdbankRpc, streamJsonRpc, client, nerdbankMx, streamMx);
		}

		/// <summary>Establishes two multiplexing streams and the dedicated channel each endpoint uses for JSON-RPC itself.</summary>
		private static async Task<(MultiplexingStream NerdbankMx, MultiplexingStream StreamMx, IDuplexPipe NerdbankPipe, IDuplexPipe StreamPipe)> CreateMultiplexedPairAsync()
		{
			(IDuplexPipe nerdbankTransport, IDuplexPipe streamTransport) = FullDuplexStream.CreatePipePair();
			Task<MultiplexingStream> nerdbankMxTask = MultiplexingStream.CreateAsync(nerdbankTransport.AsStream());
			Task<MultiplexingStream> streamMxTask = MultiplexingStream.CreateAsync(streamTransport.AsStream());
			await Task.WhenAll(nerdbankMxTask, streamMxTask);
			MultiplexingStream nerdbankMx = await nerdbankMxTask;
			MultiplexingStream streamMx = await streamMxTask;
			Task<MultiplexingStream.Channel> streamChannelTask = streamMx.AcceptChannelAsync(string.Empty, CancellationToken.None);
			MultiplexingStream.Channel nerdbankChannel = nerdbankMx.CreateChannel();
			MultiplexingStream.Channel streamChannel = await streamChannelTask;
			return (nerdbankMx, streamMx, nerdbankChannel, streamChannel);
		}

		private static JsonRpc CreateNerdbankRpc(IDuplexPipe pipe, MultiplexingStream? multiplexingStream = null) => new(
			new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance))
		{
			MultiplexingStream = multiplexingStream,
			MarshaledProxyOptions = new() { MethodNameTransform = CommonMethodNameTransforms.Identity },
			MarshaledTargetOptions = new() { MethodNameTransform = CommonMethodNameTransforms.Identity },
		};

		private static StreamJsonRpc.IJsonRpcMessageHandler CreateHandler(IDuplexPipe pipe, MultiplexingStream? multiplexingStream = null)
			=> new StreamJsonRpc.NewLineDelimitedMessageHandler(pipe, new StreamJsonRpc.JsonMessageFormatter { MultiplexingStream = multiplexingStream });
	}
}
