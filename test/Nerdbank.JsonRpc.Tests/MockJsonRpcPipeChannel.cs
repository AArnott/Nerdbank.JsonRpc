// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc.Tests;

internal sealed class MockJsonRpcPipeChannel : JsonRpcPipeChannel
{
	private readonly Channel<JsonRpcMessage> messages;

	/// <summary>Initializes a new instance of the <see cref="MockJsonRpcPipeChannel"/> class.</summary>
	/// <param name="messages">The in-memory message channel.</param>
	/// <param name="serializer">The serializer to bind to this channel.</param>
	/// <param name="writeDirectly">Whether writes should bypass the transport queue to observe writer failures synchronously.</param>
	internal MockJsonRpcPipeChannel(Channel<JsonRpcMessage> messages, JsonRpcSerializer? serializer = null, bool writeDirectly = false)
		: base(FullDuplexStream.CreatePipePair().Item1, CreateInboundChannel(null), CreateOutboundChannel(null), NullLogger.Instance)
	{
		this.messages = messages;
		if (writeDirectly)
		{
			this.Writer = messages.Writer;
		}

		this.Serializer = serializer ?? new MessagePackSerializerPlugin(JsonRpcMessagePackChannel.DefaultSerializer);
		this.StartTransport();
	}

	/// <inheritdoc/>
	public override JsonRpcEncoding Encoding => this.Serializer.Encoding;

	/// <inheritdoc/>
	public override JsonRpcSerializer Serializer { get; }

	/// <inheritdoc/>
	protected override async IAsyncEnumerable<JsonRpcMessage> ReceiveMessagesAsync(PipeReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await foreach (JsonRpcMessage message in this.messages.Reader.ReadAllAsync(cancellationToken))
		{
			yield return message;
		}
	}

	/// <inheritdoc/>
	protected override ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken)
		=> this.messages.Writer.WriteAsync(message, cancellationToken);
}
