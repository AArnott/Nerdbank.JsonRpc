// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Text.Json;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

/// <summary>Builds positional or named request parameters using the selected serializer.</summary>
public sealed class JsonRpcArgumentsBuilder
{
	private readonly JsonRpcSerializer serializer;
	private readonly bool named;
	private readonly List<(string? Name, JsonRpcValue Value)> values = new();

	/// <summary>Initializes a new instance of the <see cref="JsonRpcArgumentsBuilder"/> class.</summary>
	/// <param name="serializer">The selected serializer.</param>
	/// <param name="named">Whether to encode named parameters.</param>
	internal JsonRpcArgumentsBuilder(JsonRpcSerializer serializer, bool named)
	{
		this.serializer = serializer;
		this.named = named;
	}

	/// <summary>Adds a typed parameter.</summary>
	/// <typeparam name="T">The parameter type.</typeparam>
	/// <param name="name">The name for a named parameter, or null for positional parameters.</param>
	/// <param name="value">The parameter value.</param>
	/// <param name="shape">The parameter type shape.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public void Add<T>(string? name, in T value, ITypeShape<T> shape, CancellationToken cancellationToken = default)
	{
		if (this.named && name is null)
		{
			throw new ArgumentNullException(nameof(name));
		}

		this.values.Add((name, this.serializer.Serialize(value, shape, cancellationToken)));
	}

	/// <summary>Builds an owned JSON-RPC params value.</summary>
	/// <returns>An encoded array or object.</returns>
	public JsonRpcValue Build()
	{
		using Sequence<byte> buffer = new();
		if (this.serializer.Encoding == JsonRpcEncoding.Json)
		{
			using Utf8JsonWriter writer = new(buffer);
			if (this.named)
			{
				writer.WriteStartObject();
			}
			else
			{
				writer.WriteStartArray();
			}

			foreach ((string? name, JsonRpcValue value) in this.values)
			{
				if (this.named)
				{
					writer.WritePropertyName(name!);
				}

				writer.WriteRawValue(value.OwnedBytes.Span, skipInputValidation: false);
			}

			if (this.named)
			{
				writer.WriteEndObject();
			}
			else
			{
				writer.WriteEndArray();
			}

			writer.Flush();
			return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
		}

		MessagePackWriter mpWriter = new(buffer);
		if (this.named)
		{
			mpWriter.WriteMapHeader(this.values.Count);
		}
		else
		{
			mpWriter.WriteArrayHeader(this.values.Count);
		}

		foreach ((string? name, JsonRpcValue value) in this.values)
		{
			if (this.named)
			{
				mpWriter.Write(name);
			}

			mpWriter.WriteRaw(value.OwnedBytes.Span);
		}

		mpWriter.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)buffer.AsReadOnlySequence.ToArray());
	}
}
