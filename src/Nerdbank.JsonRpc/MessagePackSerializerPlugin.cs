// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

/// <summary>Adapts a configured MessagePack serializer.</summary>
public sealed class MessagePackSerializerPlugin : JsonRpcSerializer
{
	/// <summary>Initializes a new instance of the <see cref="MessagePackSerializerPlugin"/> class.</summary>
	/// <param name="serializer">The configured serializer.</param>
	public MessagePackSerializerPlugin(MessagePackSerializer serializer) => this.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

	/// <summary>Gets the configured serializer.</summary>
	public MessagePackSerializer Serializer { get; }

	/// <inheritdoc/>
	public override JsonRpcEncoding Encoding => JsonRpcEncoding.MessagePack;

	/// <inheritdoc/>
	internal override JsonRpcValue Serialize<T>(in T value, ITypeShape<T> shape, RpcCallState? callState, CancellationToken cancellationToken)
	{
		using RpcCallState.Frame frame = RpcCallState.Enter(callState);
		using ScratchSequence scratch = ScratchSequence.Rent();
		MessagePackWriter writer = new(scratch.Sequence);
		this.Serializer.Serialize(ref writer, value, shape, cancellationToken);
		writer.Flush();
		return JsonRpcValue.FromOwnedBytes(scratch.Sequence.AsReadOnlySequence.ToArray(), JsonRpcEncoding.MessagePack);
	}

	/// <inheritdoc/>
	internal override T Deserialize<T>(JsonRpcValue value, ITypeShape<T> shape, RpcCallState? callState, CancellationToken cancellationToken)
	{
		using RpcCallState.Frame frame = RpcCallState.Enter(callState);
		MessagePackReader reader = new(value.AsOwnedMessagePack());
		return this.Serializer.Deserialize(ref reader, shape, cancellationToken)!;
	}

	/// <inheritdoc/>
	internal override JsonRpcSerializer WithMarshaledObjectManager(MarshaledObjectManager manager, ProgressManager progress, OutOfBandStreamManager outOfBandStreams, AsyncEnumerableManager asyncEnumerables)
		=> new MessagePackSerializerPlugin(this.Serializer with
		{
			Converters = ConverterCollection.Create([new MarshaledDisposableMessagePackConverter(manager), .. this.Serializer.Converters]),
			ConverterFactories = [new AsyncEnumerableMessagePackConverterFactory(asyncEnumerables), new OutOfBandStreamMessagePackConverterFactory(outOfBandStreams), new MarshaledInterfaceMessagePackConverterFactory(manager, progress), .. this.Serializer.ConverterFactories],
		});

	/// <inheritdoc/>
	internal override void SerializeTo<T>(IBufferWriter<byte> buffer, in T value, ITypeShape<T> shape, RpcCallState? callState, CancellationToken cancellationToken)
	{
		using RpcCallState.Frame frame = RpcCallState.Enter(callState);
		MessagePackWriter writer = new(buffer);
		this.Serializer.Serialize(ref writer, value, shape, cancellationToken);
		writer.Flush();
	}

	/// <inheritdoc/>
	internal override void WriteArgumentName(IBufferWriter<byte> buffer, string name)
	{
		MessagePackWriter writer = new(buffer);
		writer.Write(name);
		writer.Flush();
	}

	/// <inheritdoc/>
	internal override bool IsParameterCollection(JsonRpcValue value)
	{
		MessagePackType type = new MessagePackReader(value.AsOwnedMessagePack()).NextMessagePackType;
		return type is MessagePackType.Map or MessagePackType.Array;
	}

	/// <inheritdoc/>
	internal override (bool Named, ArgumentList Values) ReadArguments(JsonRpcValue arguments)
	{
		MessagePackReader reader = new(arguments.AsOwnedMessagePack());
		SerializationContext context = new();
		bool named = reader.NextMessagePackType switch
		{
			MessagePackType.Map => true,
			MessagePackType.Array => false,
			_ => throw new FormatException("Parameters must be an object or array."),
		};
		int count = named ? reader.ReadMapHeader() : reader.ReadArrayHeader();
		(string? Name, JsonRpcValue Value)[] values = ArgumentList.Rent(count);
		for (int i = 0; i < count; i++)
		{
			string? name = named ? reader.ReadString() : null;
			values[i] = (name, JsonRpcValue.FromOwnedMessagePack(arguments, reader.ReadRaw(context)));
		}

		if (!reader.End)
		{
			throw new FormatException("Trailing bytes in parameters.");
		}

		return (named, new(values, count));
	}

	internal override JsonRpcValue SerializeCancellation(RequestId id, CancellationToken cancellationToken)
		=> this.Serialize(new JsonRpc.CancelRequestParams(id), PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc.Default.CancelRequestParams, cancellationToken);

	/// <summary>Builds the context for one (de)serialization job.</summary>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The starting context for the job.</returns>
	private SerializationContext CreateStartingContext(CancellationToken cancellationToken)
		=> this.Serializer.StartingContext with { CancellationToken = cancellationToken };
}
