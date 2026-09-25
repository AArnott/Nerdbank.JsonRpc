// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Text.Json;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

/// <summary>Adapts a configured Nerdbank.Json serializer.</summary>
public sealed class JsonSerializerPlugin : JsonRpcSerializer
{
	/// <summary>Initializes a new instance of the <see cref="JsonSerializerPlugin"/> class.</summary>
	/// <param name="serializer">The configured serializer.</param>
	public JsonSerializerPlugin(Nerdbank.Json.JsonSerializer serializer) => this.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

	/// <summary>Gets the configured serializer.</summary>
	public Nerdbank.Json.JsonSerializer Serializer { get; }

	/// <inheritdoc/>
	public override JsonRpcEncoding Encoding => JsonRpcEncoding.Json;

	/// <inheritdoc/>
	public override JsonRpcValue Serialize<T>(in T value, ITypeShape<T> shape, CancellationToken cancellationToken = default)
	{
		using Sequence<byte> buffer = new();
		this.Serializer.Serialize(buffer, value, shape, cancellationToken);
		return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
	}

	/// <inheritdoc/>
	public override T Deserialize<T>(JsonRpcValue value, ITypeShape<T> shape, CancellationToken cancellationToken = default) => this.Serializer.Deserialize(RequireJson(value), shape, cancellationToken)!;

	/// <inheritdoc/>
	internal override JsonRpcSerializer WithMarshaledObjectManager(MarshaledObjectManager manager)
		=> new JsonSerializerPlugin(this.Serializer with
		{
			Converters = new Nerdbank.Json.ConverterCollection([new MarshaledDisposableJsonConverter(manager), .. this.Serializer.Converters]),
		});

	/// <inheritdoc/>
	internal override void SerializeTo<T>(IBufferWriter<byte> buffer, in T value, ITypeShape<T> shape, CancellationToken cancellationToken)
		=> this.Serializer.Serialize(buffer, value, shape, cancellationToken);

	/// <inheritdoc/>
	internal override void WriteArgumentName(IBufferWriter<byte> buffer, string name)
	{
		Nerdbank.Json.JsonWriter writer = new(buffer);
		writer.WriteStringValue(name);
		writer.Flush();
	}

	/// <inheritdoc/>
	internal override bool IsParameterCollection(JsonRpcValue value)
	{
		using JsonDocument document = JsonDocument.Parse(RequireJson(value));
		return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
	}

	/// <inheritdoc/>
	internal override (bool Named, List<(string? Name, JsonRpcValue Value)> Values) ReadArguments(JsonRpcValue arguments)
	{
		using JsonDocument document = JsonDocument.Parse(RequireJson(arguments));
		JsonElement root = document.RootElement;
		bool named = root.ValueKind switch
		{
			JsonValueKind.Object => true,
			JsonValueKind.Array => false,
			_ => throw new FormatException("Parameters must be an object or array."),
		};
		List<(string?, JsonRpcValue)> values = new();
		if (named)
		{
			foreach (JsonProperty property in root.EnumerateObject())
			{
				values.Add((property.Name, JsonRpcValue.FromJson(System.Text.Encoding.UTF8.GetBytes(property.Value.GetRawText()))));
			}
		}
		else
		{
			foreach (JsonElement element in root.EnumerateArray())
			{
				values.Add((null, JsonRpcValue.FromJson(System.Text.Encoding.UTF8.GetBytes(element.GetRawText()))));
			}
		}

		return (named, values);
	}

	internal override JsonRpcValue SerializeCancellation(RequestId id, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		using Sequence<byte> buffer = new();
		using Utf8JsonWriter writer = new(buffer);
		writer.WriteStartObject();
		writer.WritePropertyName("id");
		JsonRpcJsonCodec.WriteId(writer, id);
		writer.WriteEndObject();
		writer.Flush();
		return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
	}

	internal override object? DeserializeObject(JsonRpcValue value, ITypeShape shape, CancellationToken cancellationToken)
	{
		if (shape.Type == typeof(RequestId))
		{
			using JsonDocument document = JsonDocument.Parse(RequireJson(value));
			return JsonRpcJsonCodec.ReadId(document.RootElement);
		}

		return this.Serializer.DeserializeObject(RequireJson(value), shape, cancellationToken);
	}

	private static ReadOnlyMemory<byte> RequireJson(JsonRpcValue value) => value.HasValue && value.Encoding == JsonRpcEncoding.Json ? value.OwnedBytes : throw new InvalidOperationException("Expected a JSON value.");
}
