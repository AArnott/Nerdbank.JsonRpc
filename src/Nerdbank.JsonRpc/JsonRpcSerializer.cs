// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

/// <summary>Encodes and decodes JSON-RPC application values using one selected serializer.</summary>
public abstract class JsonRpcSerializer
{
	/// <summary>Initializes a new instance of the <see cref="JsonRpcSerializer"/> class.</summary>
	internal JsonRpcSerializer()
	{
	}

	/// <summary>Gets the wire encoding.</summary>
	public abstract JsonRpcEncoding Encoding { get; }

	/// <summary>Wraps a user-configured MessagePack serializer.</summary>
	/// <param name="serializer">The serializer to retain.</param>
	public static implicit operator JsonRpcSerializer(MessagePackSerializer serializer) => new MessagePackSerializerPlugin(serializer);

	/// <summary>Wraps a user-configured JSON serializer.</summary>
	/// <param name="serializer">The serializer to retain.</param>
	public static implicit operator JsonRpcSerializer(Nerdbank.Json.JsonSerializer serializer) => new JsonSerializerPlugin(serializer);

	/// <summary>Serializes a typed value with its type shape.</summary>
	/// <typeparam name="T">The type of the value.</typeparam>
	/// <param name="value">The value.</param>
	/// <param name="shape">The type shape.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>An owned encoded value.</returns>
	public abstract JsonRpcValue Serialize<T>(in T value, ITypeShape<T> shape, CancellationToken cancellationToken = default);

	/// <summary>Deserializes a typed value with its type shape.</summary>
	/// <typeparam name="T">The result type.</typeparam>
	/// <param name="value">An encoded value.</param>
	/// <param name="shape">The result shape.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The deserialized value.</returns>
	public abstract T Deserialize<T>(JsonRpcValue value, ITypeShape<T> shape, CancellationToken cancellationToken = default);

	/// <summary>Serializes a parameter into an existing argument buffer.</summary>
	/// <typeparam name="T">The parameter type.</typeparam>
	/// <param name="buffer">The output buffer.</param>
	/// <param name="value">The parameter value.</param>
	/// <param name="shape">The parameter type shape.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	internal abstract void SerializeTo<T>(IBufferWriter<byte> buffer, in T value, ITypeShape<T> shape, CancellationToken cancellationToken);

	/// <summary>Writes an encoded argument name into an existing buffer.</summary>
	/// <param name="buffer">The output buffer.</param>
	/// <param name="name">The argument name.</param>
	internal abstract void WriteArgumentName(IBufferWriter<byte> buffer, string name);

	/// <summary>Checks whether two plugin wrappers retain the same concrete serializer.</summary>
	/// <param name="other">The transport's serializer plugin.</param>
	/// <returns>Whether both plugins retain the same instance.</returns>
	internal abstract bool UsesSameSerializer(JsonRpcSerializer other);

	/// <summary>Rejects a mismatched or invalid application value before queuing a message.</summary>
	/// <param name="message">The message to check.</param>
	internal void ValidateMessage(JsonRpcMessage message)
	{
		switch (message)
		{
			case JsonRpcRequest request:
				this.ValidateValue(request.Arguments);
				if (request.Arguments.HasValue && !this.IsParameterCollection(request.Arguments))
				{
					throw new ArgumentException("JSON-RPC params must be an array or object.", nameof(message));
				}

				break;
			case JsonRpcResult result:
				if (!result.Result.HasValue)
				{
					throw new ArgumentException("A result must have a value.", nameof(message));
				}

				this.ValidateValue(result.Result);
				break;
			case JsonRpcError error:
				if (error.Error.Data is JsonRpcValue data)
				{
					this.ValidateValue(data);
				}

				break;
			case JsonRpcMessageBatch batch:
				if (batch.Messages.IsEmpty || batch.Messages.Any(static entry => entry is JsonRpcMessageBatch or JsonRpcInvalidMessage))
				{
					throw new ArgumentException("A batch must not be empty.", nameof(message));
				}

				foreach (JsonRpcMessage entry in batch.Messages)
				{
					this.ValidateMessage(entry);
				}

				break;
			case JsonRpcInvalidMessage:
				throw new ArgumentException("An invalid protocol marker cannot be sent.", nameof(message));
			default:
				throw new ArgumentException("Unrecognized JSON-RPC message type.", nameof(message));
		}
	}

	/// <summary>Gets a value indicating whether a present raw value is a params array or object.</summary>
	/// <param name="value">The encoded value to inspect.</param>
	/// <returns>Whether the value represents an array or object.</returns>
	internal abstract bool IsParameterCollection(JsonRpcValue value);

	/// <summary>Extracts owned parameter values without converting user DTOs.</summary>
	/// <param name="arguments">The encoded parameter collection.</param>
	/// <returns>The named or positional entries.</returns>
	internal abstract (bool Named, List<(string? Name, JsonRpcValue Value)> Values) ReadArguments(JsonRpcValue arguments);

	/// <summary>Encodes a cancellation notification using the protocol ID token.</summary>
	/// <param name="id">The ID to cancel.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>Encoded cancellation parameters.</returns>
	internal abstract JsonRpcValue SerializeCancellation(RequestId id, CancellationToken cancellationToken);

	/// <summary>Decodes an application parameter with its runtime shape.</summary>
	/// <param name="value">The encoded application value.</param>
	/// <param name="shape">The application type shape.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The deserialized application value.</returns>
	internal abstract object? DeserializeObject(JsonRpcValue value, ITypeShape shape, CancellationToken cancellationToken);

	private void ValidateValue(JsonRpcValue value)
	{
		if (value.HasValue && value.Encoding != this.Encoding)
		{
			throw new ArgumentException($"Value encoding mismatch: expected {this.Encoding}, actual {value.Encoding}.");
		}
	}
}
