// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using Nerdbank.MessagePack;

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
	public override JsonRpcValue Serialize<T>(in T value, ITypeShape<T> shape, CancellationToken cancellationToken = default) => JsonRpcValue.FromMessagePack((RawMessagePack)this.Serializer.Serialize(value, shape, cancellationToken));

	/// <inheritdoc/>
	public override T Deserialize<T>(JsonRpcValue value, ITypeShape<T> shape, CancellationToken cancellationToken = default) => this.Serializer.Deserialize(value.AsMessagePack(), shape, cancellationToken)!;

	/// <inheritdoc/>
	internal override void SerializeTo<T>(IBufferWriter<byte> buffer, in T value, ITypeShape<T> shape, CancellationToken cancellationToken)
		=> this.Serializer.Serialize(buffer, value, shape, cancellationToken);

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
		MessagePackType type = new MessagePackReader(value.AsMessagePack()).NextMessagePackType;
		return type is MessagePackType.Map or MessagePackType.Array;
	}

	/// <inheritdoc/>
	internal override (bool Named, List<(string? Name, JsonRpcValue Value)> Values) ReadArguments(JsonRpcValue arguments)
	{
		MessagePackReader reader = new(arguments.AsMessagePack());
		SerializationContext context = new();
		bool named = reader.NextMessagePackType switch
		{
			MessagePackType.Map => true,
			MessagePackType.Array => false,
			_ => throw new FormatException("Parameters must be an object or array."),
		};
		int count = named ? reader.ReadMapHeader() : reader.ReadArrayHeader();
		List<(string?, JsonRpcValue)> values = new(count);
		for (int i = 0; i < count; i++)
		{
			string? name = named ? reader.ReadString() : null;
			values.Add((name, JsonRpcValue.FromMessagePack(reader.ReadRaw(context))));
		}

		if (!reader.End)
		{
			throw new FormatException("Trailing bytes in parameters.");
		}

		return (named, values);
	}

	internal override JsonRpcValue SerializeCancellation(RequestId id, CancellationToken cancellationToken)
		=> this.Serialize(new JsonRpc.CancelRequestParams(id), PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc.Default.CancelRequestParams, cancellationToken);

	internal override object? DeserializeObject(JsonRpcValue value, ITypeShape shape, CancellationToken cancellationToken) => this.Serializer.DeserializeObject(value.AsMessagePack().MsgPack.ToArray(), shape, cancellationToken);
}
