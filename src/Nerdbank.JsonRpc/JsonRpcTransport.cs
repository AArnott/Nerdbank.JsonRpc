// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Net;
using System.Threading.Channels;
using Microsoft;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

public abstract class JsonRpcTransport : Channel<JsonRpcMessage>
{
	protected static readonly MessagePackSerializer Serializer = new()
	{
		InternStrings = true,
	};

	protected JsonRpcTransport(Channel<JsonRpcMessage> inboundChannel, Channel<JsonRpcMessage> outboundChannel)
	{
		Requires.NotNull(inboundChannel);
		Requires.NotNull(outboundChannel);

		(this.Reader, this.InboundMessageWriter) = (inboundChannel.Reader, inboundChannel.Writer);
		(this.Writer, this.OutboundMessageReader) = (outboundChannel.Writer, outboundChannel.Reader);
	}

	protected ChannelWriter<JsonRpcMessage> InboundMessageWriter { get; }

	protected ChannelReader<JsonRpcMessage> OutboundMessageReader { get; }

	protected static ValueTask SerializeAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken)
	{
		return message switch
		{
			JsonRpcRequest request => Serializer.SerializeAsync(writer, request, cancellationToken),
			JsonRpcResult result => Serializer.SerializeAsync(writer, result, cancellationToken),
			JsonRpcError error => Serializer.SerializeAsync(writer, error, cancellationToken),
			_ => throw new ArgumentException($"Unrecognized JSON-RPC message type: {message.GetType().FullName}", nameof(message)),
		};
	}

	protected static async ValueTask<JsonRpcMessage?> DeserializeAsync(PipeReader reader, CancellationToken cancellationToken)
	{
		Requires.NotNull(reader);

		// Read just to verify that we're not at the end of the stream.
		// Then undo the read and ask the deserializer to take over.
		ReadResult readResult = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		if (readResult.Buffer.IsEmpty && readResult.IsCompleted)
		{
			return null;
		}

		reader.AdvanceTo(readResult.Buffer.Start);

		return await Serializer.DeserializeAsync<JsonRpcMessage>(reader, cancellationToken).ConfigureAwait(false) ?? throw new ProtocolViolationException("Unexpected null value was received instead of JSON-RPC message.");
	}

	protected static Channel<JsonRpcMessage> CreateInboundChannel(int? capacity) => capacity is null
		? Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleWriter = true })
		: Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(capacity.Value) { SingleWriter = true });

	protected static Channel<JsonRpcMessage> CreateOutboundChannel(int? capacity) => capacity is null
		? Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true })
		: Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(capacity.Value) { SingleReader = true });
}
