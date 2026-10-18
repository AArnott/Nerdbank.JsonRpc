// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Nerdbank.Streams;
using PolyType;
using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Benchmarks;

namespace Benchmarks;

/// <summary>Isolates notification API completion overhead with synchronous or deliberately suspended queue acceptance.</summary>
/// <remarks>No wire serialization or server dispatch is measured. Every accepted notification is counted.</remarks>
[MemoryDiagnoser]
public class RpcNotificationSubmissionBenchmarks
{
	private SubmissionChannel channel = null!;
	private JsonRpc rpc = null!;
	private INotificationBenchmarkContract proxy = null!;
	private JsonRpcValue arguments;

	/// <summary>Gets or sets the notification argument encoding.</summary>
	[Params(RpcEncoding.Json, RpcEncoding.MessagePack)]
	public RpcEncoding Encoding { get; set; }

	/// <summary>Gets or sets a value indicating whether acceptance suspends, modeling outbound backpressure.</summary>
	[Params(false, true)]
	public bool Suspend { get; set; }

	/// <summary>Creates the client and validates each submission path.</summary>
	[GlobalSetup]
	public async Task SetupAsync()
	{
		this.channel = new(this.Encoding, this.Suspend);
		this.rpc = new(this.channel);
		this.rpc.Start();
		this.proxy = this.rpc.Attach<INotificationBenchmarkContract>();
		using (JsonRpcArgumentsBuilder builder = this.rpc.CreateArguments(named: false, count: 1))
		{
			builder.Add(null, "value", ShapeProvider.Default.String);
			this.arguments = builder.Build();
		}

		long before = this.channel.Submissions.Accepted;
		await this.DirectEncoded();
		await this.DirectSerialized();
		await this.ProxyNotification();
		if (this.channel.Submissions.Accepted - before != 96)
		{
			throw new InvalidOperationException("Not all notifications were accepted.");
		}
	}

	/// <summary>Releases the connection and channel loops.</summary>
	[GlobalCleanup]
	public async Task CleanupAsync()
	{
		this.rpc.Dispose();
		await this.channel.DisposeAsync();
	}

	/// <summary>Measures direct notification submission with pre-encoded arguments.</summary>
	/// <returns>The loop completion.</returns>
	[Benchmark(OperationsPerInvoke = 32)]
	public async Task DirectEncoded()
	{
		for (int i = 0; i < 32; i++)
		{
			await this.rpc.NotifyAsync("notify", this.arguments, CancellationToken.None).ConfigureAwait(false);
		}
	}

	/// <summary>Measures direct notification submission with argument serialization.</summary>
	/// <returns>The loop completion.</returns>
	[Benchmark(OperationsPerInvoke = 32)]
	public async Task DirectSerialized()
	{
		for (int i = 0; i < 32; i++)
		{
			await this.rpc.NotifyAsync("notify", new NotificationBenchmarkArguments { Value = "value" }, ShapeProvider.Default.NotificationBenchmarkArguments, CancellationToken.None).ConfigureAwait(false);
		}
	}

	/// <summary>Measures void proxy notifications, including their required completion observation.</summary>
	/// <returns>The loop completion.</returns>
	[Benchmark(OperationsPerInvoke = 32)]
	public async Task ProxyNotification()
	{
		for (int i = 0; i < 32; i++)
		{
			long expected = this.channel.Submissions.Accepted + 1;
			this.proxy.Notify("value", CancellationToken.None);
			while (this.channel.Submissions.Accepted < expected)
			{
				await Task.Yield();
			}
		}
	}

	private sealed class SubmissionChannel : JsonRpcPipeChannel
	{
		private readonly Channel<JsonRpcMessage> inbound = Channel.CreateUnbounded<JsonRpcMessage>();

		/// <summary>Initializes a new instance of the <see cref="SubmissionChannel"/> class.</summary>
		/// <param name="encoding">The argument encoding.</param>
		/// <param name="suspend">Whether submission suspends.</param>
		internal SubmissionChannel(RpcEncoding encoding, bool suspend)
			: base(FullDuplexStream.CreatePipePair().Item1, CreateInboundChannel(null), CreateOutboundChannel(null))
		{
			this.Serializer = encoding == RpcEncoding.Json
				? new JsonSerializerPlugin(new Nerdbank.Json.JsonSerializer())
				: new MessagePackSerializerPlugin(JsonRpcMessagePackChannel.DefaultSerializer);
			this.Submissions = new(this.Writer, suspend);
			this.Writer = this.Submissions;
		}

		/// <inheritdoc/>
		public override JsonRpcEncoding Encoding => this.Serializer.Encoding;

		/// <inheritdoc/>
		public override JsonRpcSerializer Serializer { get; }

		/// <summary>Gets the notification acceptance counter.</summary>
		internal SubmissionWriter Submissions { get; }

		/// <inheritdoc/>
		protected override async IAsyncEnumerable<JsonRpcMessage> ReceiveMessagesAsync(PipeReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
		{
			await foreach (JsonRpcMessage message in this.inbound.Reader.ReadAllAsync(cancellationToken))
			{
				yield return message;
			}
		}

		/// <inheritdoc/>
		protected override ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken) => default;
	}

	private sealed class SubmissionWriter(ChannelWriter<JsonRpcMessage> original, bool suspend) : ChannelWriter<JsonRpcMessage>
	{
		private long accepted;

		/// <summary>Gets the number of accepted notifications.</summary>
		internal long Accepted => Interlocked.Read(ref this.accepted);

		/// <inheritdoc/>
		public override bool TryComplete(Exception? error = null) => original.TryComplete(error);

		/// <inheritdoc/>
		public override bool TryWrite(JsonRpcMessage item) => throw new NotSupportedException();

		/// <inheritdoc/>
		public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

		/// <inheritdoc/>
		public override async ValueTask WriteAsync(JsonRpcMessage item, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (suspend)
			{
				await Task.Yield();
			}

			if (item is not JsonRpcRequest { Id: null, Method: "notify" })
			{
				throw new InvalidOperationException("Expected a notification.");
			}

			await original.WriteAsync(item, cancellationToken).ConfigureAwait(false);
			Interlocked.Increment(ref this.accepted);
		}
	}
}
