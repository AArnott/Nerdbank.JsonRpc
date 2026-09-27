// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Reflection;
using System.Text;
using PolyType;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

internal sealed class MarshaledInterfaceJsonConverterFactory(MarshaledObjectManager manager, ProgressManager progress) : Nerdbank.Json.IJsonConverterFactory, ITypeShapeFunc
{
	public Nerdbank.Json.JsonConverter? CreateConverter(Type type, ITypeShape? shape, in Nerdbank.Json.JsonConverterFactoryContext context)
	{
		bool specialInterface = type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IObserver<>) || type.GetGenericTypeDefinition() == typeof(IProgress<>));
		if (!type.IsInterface || (!type.IsDefined(typeof(RpcMarshalableAttribute), inherit: false) && !specialInterface))
		{
			return null;
		}

		return shape is not null
			? (Nerdbank.Json.JsonConverter?)shape.Invoke(this, manager)
			: throw new NotSupportedException($"A PolyType shape is required to marshal interface '{type}'.");
	}

	public object? Invoke<T>(ITypeShape<T> shape, object? state) => new Converter<T>(manager, progress, shape);

	private sealed class Converter<T>(MarshaledObjectManager manager, ProgressManager progress, ITypeShape<T> shape) : Nerdbank.Json.JsonConverter<T>
	{
		private readonly bool typeIsObserver = typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(IObserver<>);
		private readonly bool typeIsProgress = typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(IProgress<>);

		public override T? Read(ref Nerdbank.Json.JsonReader reader, Nerdbank.Json.SerializationContext context)
		{
			string rawValue = reader.ReadRawValue();
			return rawValue == "null" ? default : this.typeIsObserver ? (T)ObserverMarshaler.CreateProxy(manager, JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(rawValue)), shape) : this.typeIsProgress ? (T)ProgressMarshaler.CreateProxy(progress, JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(rawValue)), shape) : manager.UnmarshalMarshalable<T>(JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(rawValue)), shape);
		}

		public override void Write(ref Nerdbank.Json.JsonWriter writer, T? value, Nerdbank.Json.SerializationContext context)
		{
			if (value is null)
			{
				writer.WriteNullValue();
				return;
			}

			JsonRpcValue marker = this.typeIsObserver ? ObserverMarshaler.Marshal(manager, value, shape, JsonRpcEncoding.Json) : this.typeIsProgress ? ProgressMarshaler.Marshal(progress, value, shape, JsonRpcEncoding.Json) : manager.MarshalMarshalable(value, shape, JsonRpcEncoding.Json);
			writer.WriteRawValue(Encoding.UTF8.GetString(marker.OwnedBytes.Span));
		}
	}
}
