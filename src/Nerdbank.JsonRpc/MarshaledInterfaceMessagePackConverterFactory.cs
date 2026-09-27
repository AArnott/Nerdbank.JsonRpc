// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Reflection;
using Nerdbank.MessagePack;
using PolyType;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

internal sealed class MarshaledInterfaceMessagePackConverterFactory(MarshaledObjectManager manager) : IMessagePackConverterFactory, ITypeShapeFunc
{
	public MessagePackConverter? CreateConverter(Type type, ITypeShape? shape, in ConverterContext context)
	{
		if (!type.IsInterface || (!type.IsDefined(typeof(RpcMarshalableAttribute), inherit: false) && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IObserver<>))))
		{
			return null;
		}

		return shape is not null
			? MessagePackConverterFactoryExtensions.Invoke(this, shape, manager)
			: throw new NotSupportedException($"A PolyType shape is required to marshal interface '{type}'.");
	}

	public object? Invoke<T>(ITypeShape<T> shape, object? state) => new Converter<T>(manager, shape);

	private sealed class Converter<T>(MarshaledObjectManager manager, ITypeShape<T> shape) : MessagePackConverter<T>
	{
		private readonly bool typeIsObserver = typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(IObserver<>);

		public override T? Read(ref MessagePackReader reader, SerializationContext context)
			=> reader.TryReadNil() ? default : this.typeIsObserver ? (T)ObserverMarshaler.CreateProxy(manager, JsonRpcValue.FromMessagePack(reader.ReadRaw(context)), shape) : manager.UnmarshalMarshalable<T>(JsonRpcValue.FromMessagePack(reader.ReadRaw(context)), shape);

		public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
		{
			if (value is null)
			{
				writer.WriteNil();
				return;
			}

			JsonRpcValue marker = this.typeIsObserver ? ObserverMarshaler.Marshal(manager, value, shape, JsonRpcEncoding.MessagePack) : manager.MarshalMarshalable(value, shape, JsonRpcEncoding.MessagePack);
			writer.Write(marker.AsMessagePack());
		}
	}
}
