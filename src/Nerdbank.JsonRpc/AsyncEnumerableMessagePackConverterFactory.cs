// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

/// <summary>Creates MessagePack converters that marshal <see cref="IAsyncEnumerable{T}"/> values over JSON-RPC.</summary>
/// <param name="manager">The manager that tracks generators for this connection.</param>
internal sealed class AsyncEnumerableMessagePackConverterFactory(AsyncEnumerableManager manager) : IMessagePackConverterFactory
{
	/// <inheritdoc/>
	public MessagePackConverter? CreateConverter(Type type, PolyType.ITypeShape? shape, in ConverterContext context)
		=> AsyncEnumerableMarshaler.IsAsyncEnumerable(type) && shape is not null ? (MessagePackConverter?)shape.Invoke(new Factory(manager)) : null;

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
	private sealed class Converter<T>(AsyncEnumerableManager manager, PolyType.ITypeShape<T> shape) : MessagePackConverter<T>
	{
		/// <inheritdoc/>
		public override T? Read(ref MessagePackReader reader, SerializationContext context)
			=> reader.TryReadNil() ? default : (T)AsyncEnumerableMarshaler.Unmarshal(manager, JsonRpcValue.FromMessagePack(reader.ReadRaw(context)), shape, RpcCallState.From(context));

		/// <inheritdoc/>
		public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
		{
			if (value is null)
			{
				writer.WriteNil();
				return;
			}

			writer.Write(AsyncEnumerableMarshaler.Marshal(manager, value, shape, JsonRpcEncoding.MessagePack, RpcCallState.From(context)).AsOwnedMessagePack());
		}
	}
}
