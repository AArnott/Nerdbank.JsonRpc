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
	internal override JsonRpcValue Serialize<T>(in T value, ITypeShape<T> shape, RpcCallState? callState, CancellationToken cancellationToken)
	{
		using RpcCallState.Frame frame = RpcCallState.Enter(callState);
		using ScratchSequence scratch = ScratchSequence.Rent();
		Nerdbank.Json.JsonWriter writer = new(scratch.Sequence) { WriteIndented = this.Serializer.WriteIndented };
		this.Serializer.Serialize(ref writer, value, shape, cancellationToken);
		writer.Flush();
		return JsonRpcValue.FromJson(scratch.Sequence.AsReadOnlySequence.ToArray());
	}

	/// <inheritdoc/>
	internal override T Deserialize<T>(JsonRpcValue value, ITypeShape<T> shape, RpcCallState? callState, CancellationToken cancellationToken)
	{
		using RpcCallState.Frame frame = RpcCallState.Enter(callState);
		Nerdbank.Json.JsonReader reader = new(RequireJson(value).Span);
		return this.Serializer.Deserialize(ref reader, shape, cancellationToken)!;
	}

	/// <inheritdoc/>
	internal override JsonRpcSerializer WithMarshaledObjectManager(MarshaledObjectManager manager, ProgressManager progress, OutOfBandStreamManager outOfBandStreams, AsyncEnumerableManager asyncEnumerables)
		=> new JsonSerializerPlugin(this.Serializer with
		{
			Converters = new Nerdbank.Json.ConverterCollection([new MarshaledDisposableJsonConverter(manager), .. this.Serializer.Converters]),
			ConverterFactories = [new AsyncEnumerableJsonConverterFactory(asyncEnumerables), new OutOfBandStreamJsonConverterFactory(outOfBandStreams), new MarshaledInterfaceJsonConverterFactory(manager, progress), .. this.Serializer.ConverterFactories],
		});

	/// <inheritdoc/>
	internal override void SerializeTo<T>(IBufferWriter<byte> buffer, in T value, ITypeShape<T> shape, RpcCallState? callState, CancellationToken cancellationToken)
	{
		using RpcCallState.Frame frame = RpcCallState.Enter(callState);
		Nerdbank.Json.JsonWriter writer = new(buffer) { WriteIndented = this.Serializer.WriteIndented };
		this.Serializer.Serialize(ref writer, value, shape, cancellationToken);
		writer.Flush();
	}

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
		if (!value.HasValue || value.Encoding != JsonRpcEncoding.Json)
		{
			throw new InvalidOperationException("Expected a JSON value.");
		}

		Utf8JsonReader reader = new(value.OwnedBytes.Span);
		return reader.Read() && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray;
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
		using Sequence<byte> buffer = new(ArrayPool<byte>.Shared);
		using Utf8JsonWriter writer = new(buffer);
		writer.WriteStartObject();
		writer.WritePropertyName("id");
		JsonRpcJsonCodec.WriteId(writer, id);
		writer.WriteEndObject();
		writer.Flush();
		return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
	}

	internal override object? DeserializeObject(JsonRpcValue value, ITypeShape shape, RpcCallState? callState, CancellationToken cancellationToken)
	{
		if (shape.Type == typeof(RequestId))
		{
			using JsonDocument document = JsonDocument.Parse(RequireJson(value));
			return JsonRpcJsonCodec.ReadId(document.RootElement);
		}

		Nerdbank.Json.JsonReader reader = new(RequireJson(value).Span);
		using RpcCallState.Frame frame = RpcCallState.Enter(callState);
		return this.Serializer.DeserializeObject(ref reader, shape, cancellationToken);
	}

	private static ReadOnlyMemory<byte> RequireJson(JsonRpcValue value) => value.HasValue && value.Encoding == JsonRpcEncoding.Json ? value.OwnedBytes : throw new InvalidOperationException("Expected a JSON value.");

	/// <summary>Builds the context for one (de)serialization job.</summary>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The starting context for the job.</returns>
	private Nerdbank.Json.SerializationContext CreateStartingContext(CancellationToken cancellationToken)
		=> this.Serializer.StartingContext with { CancellationToken = cancellationToken };
}
