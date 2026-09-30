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
		return JsonRpcValue.FromOwnedBytes(scratch.Sequence.AsReadOnlySequence.ToArray(), JsonRpcEncoding.Json);
	}

	/// <inheritdoc/>
	internal override T Deserialize<T>(JsonRpcValue value, ITypeShape<T> shape, RpcCallState? callState, CancellationToken cancellationToken)
	{
		if (typeof(T) == typeof(RequestId))
		{
			Utf8JsonReader idReader = new(RequireJson(value).Span);
			idReader.Read();
			RequestId id = JsonRpcJsonCodec.ReadId(ref idReader);
			if (idReader.Read())
			{
				throw new JsonException("Unexpected content after the request ID.");
			}

			return (T)(object)id;
		}

		using RpcCallState.Frame frame = RpcCallState.Enter(callState);
		Nerdbank.Json.JsonReader reader = new(RequireJson(value).Span);
		return this.Serializer.Deserialize(ref reader, shape, cancellationToken)!;
	}

	/// <inheritdoc/>
	internal override JsonRpcSerializer WithMarshaledObjectManager(MarshaledObjectManager manager, ProgressManager progress, OutOfBandStreamManager outOfBandStreams, AsyncEnumerableManager asyncEnumerables)
		=> new JsonSerializerPlugin(this.Serializer with
		{
			Converters = new Nerdbank.Json.ConverterCollection([new MarshaledDisposableJsonConverter(manager), .. this.Serializer.Converters]),
#if NETWASM // NetWasm: out-of-band streams are unavailable.
			ConverterFactories = [new AsyncEnumerableJsonConverterFactory(asyncEnumerables), new MarshaledInterfaceJsonConverterFactory(manager, progress), .. this.Serializer.ConverterFactories],
#else
			ConverterFactories = [new AsyncEnumerableJsonConverterFactory(asyncEnumerables), new OutOfBandStreamJsonConverterFactory(outOfBandStreams), new MarshaledInterfaceJsonConverterFactory(manager, progress), .. this.Serializer.ConverterFactories],
#endif
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
		if (!reader.Read() || reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
		{
			return false;
		}

		reader.Skip();
		reader.Read();
		return true;
	}

	/// <inheritdoc/>
	internal override (bool Named, ArgumentList Values) ReadArguments(JsonRpcValue arguments)
	{
		// Scan rather than parse into a document, and share the arguments' buffer rather than copying each argument out of it.
		Utf8JsonReader reader = new(RequireJson(arguments).Span);
		reader.Read();
		bool named = reader.TokenType switch
		{
			JsonTokenType.StartObject => true,
			JsonTokenType.StartArray => false,
			_ => throw new FormatException("Parameters must be an object or array."),
		};
		(string? Name, JsonRpcValue Value)[] values = ArgumentList.Rent(4);
		int count = 0;
		try
		{
			while (reader.Read() && reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray))
			{
				if (count == JsonRpcMessageConverter.MaximumCollectionCount)
				{
					throw new FormatException("The JSON params contain too many entries.");
				}

				string? name = null;
				if (named)
				{
					name = Utf8StringCache.ReadString(ref reader);
					reader.Read();
				}

				int start = checked((int)reader.TokenStartIndex);
				reader.Skip();
				if (count == values.Length)
				{
					ArgumentList.Grow(ref values, count);
				}

				values[count++] = (name, arguments.Slice(start, checked((int)reader.BytesConsumed) - start));
			}

			// Reject trailing content, as parsing the whole value would.
			reader.Read();

			return (named, new(values, count, named));
		}
		catch
		{
			new ArgumentList(values, count, named).Return();
			throw;
		}
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
		return JsonRpcValue.FromOwnedBytes(buffer.AsReadOnlySequence.ToArray(), JsonRpcEncoding.Json);
	}

	private static ReadOnlyMemory<byte> RequireJson(JsonRpcValue value) => value.HasValue && value.Encoding == JsonRpcEncoding.Json ? value.OwnedBytes : throw new InvalidOperationException("Expected a JSON value.");

	/// <summary>Builds the context for one (de)serialization job.</summary>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The starting context for the job.</returns>
	private Nerdbank.Json.SerializationContext CreateStartingContext(CancellationToken cancellationToken)
		=> this.Serializer.StartingContext with { CancellationToken = cancellationToken };
}
