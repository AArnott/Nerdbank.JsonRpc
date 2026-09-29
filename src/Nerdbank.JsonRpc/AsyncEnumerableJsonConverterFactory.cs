// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;

namespace Nerdbank.JsonRpc;

/// <summary>Creates JSON converters that marshal <see cref="IAsyncEnumerable{T}"/> values over JSON-RPC.</summary>
/// <param name="manager">The manager that tracks generators for this connection.</param>
internal sealed class AsyncEnumerableJsonConverterFactory(AsyncEnumerableManager manager) : Nerdbank.Json.IJsonConverterFactory
{
	/// <inheritdoc/>
	public Nerdbank.Json.JsonConverter? CreateConverter(Type type, PolyType.ITypeShape? shape, in Nerdbank.Json.JsonConverterFactoryContext context)
		=> AsyncEnumerableMarshaler.IsAsyncEnumerable(type) && shape is not null ? (Nerdbank.Json.JsonConverter?)shape.Invoke(new Factory(manager)) : null;

	/// <summary>Creates a converter in a generic context for the sequence type.</summary>
	/// <param name="manager">The manager that tracks generators.</param>
	private sealed class Factory(AsyncEnumerableManager manager) : PolyType.Abstractions.ITypeShapeFunc
	{
		/// <inheritdoc/>
		public object? Invoke<T>(PolyType.ITypeShape<T> shape, object? state = null) => new Converter<T>(manager, shape);
	}

	/// <summary>Converts a single <see cref="IAsyncEnumerable{T}"/> type.</summary>
	/// <typeparam name="T">The sequence type.</typeparam>
	/// <param name="manager">The manager that tracks generators.</param>
	/// <param name="shape">The shape of the sequence type.</param>
	private sealed class Converter<T>(AsyncEnumerableManager manager, PolyType.ITypeShape<T> shape) : Nerdbank.Json.JsonConverter<T>
	{
		/// <inheritdoc/>
		public override T? Read(ref Nerdbank.Json.JsonReader reader, Nerdbank.Json.SerializationContext context)
		{
			string raw = reader.ReadRawValue();
			return raw == "null" ? default : (T)AsyncEnumerableMarshaler.Unmarshal(manager, JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(raw)), shape, RpcCallState.From(context));
		}

		/// <inheritdoc/>
		public override void Write(ref Nerdbank.Json.JsonWriter writer, T? value, Nerdbank.Json.SerializationContext context)
		{
			if (value is null)
			{
				writer.WriteNullValue();
				return;
			}

			JsonRpcValue encoded = AsyncEnumerableMarshaler.Marshal(manager, value, shape, JsonRpcEncoding.Json, RpcCallState.From(context));
			writer.WriteRawValue(Encoding.UTF8.GetString(encoded.OwnedBytes.Span));
		}
	}
}
