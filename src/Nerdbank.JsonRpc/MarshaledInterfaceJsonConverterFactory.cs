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
#if NETWASM
		// NetWasm: Type has no IsGenericType/IsInterface/IsDefined. Detect [RpcMarshalable] via the PolyType shape's attributes.
		// IObserver<T> and IProgress<T> marshaling is not supported on NetWasm.
		if (shape is null || !shape.AttributeProvider.IsDefined<RpcMarshalableAttribute>(inherit: false))
		{
			return null;
		}
#else
		bool specialInterface = type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IObserver<>) || type.GetGenericTypeDefinition() == typeof(IProgress<>));
		if (!type.IsInterface || (!type.IsDefined(typeof(RpcMarshalableAttribute), inherit: false) && !specialInterface))
		{
			return null;
		}
#endif

		return shape is not null
			? (Nerdbank.Json.JsonConverter?)shape.Invoke(this, manager)
			: throw new NotSupportedException($"A PolyType shape is required to marshal interface '{type}'.");
	}

	public object? Invoke<T>(ITypeShape<T> shape, object? state) => new Converter<T>(manager, progress, shape);

	private sealed class Converter<T>(MarshaledObjectManager manager, ProgressManager progress, ITypeShape<T> shape) : Nerdbank.Json.JsonConverter<T>
	{
#if NETWASM
		private readonly bool typeIsObserver = false;
		private readonly bool typeIsProgress = false;
#else
		private readonly bool typeIsObserver = typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(IObserver<>);
		private readonly bool typeIsProgress = typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(IProgress<>);
#endif

		public override T? Read(ref Nerdbank.Json.JsonReader reader, Nerdbank.Json.SerializationContext context)
		{
			string rawValue = reader.ReadRawValue();
			return rawValue == "null" ? default : this.typeIsObserver ? (T)ObserverMarshaler.CreateProxy(manager, JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(rawValue)), shape, RpcCallState.Current) : this.typeIsProgress ? (T)ProgressMarshaler.CreateProxy(progress, JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(rawValue)), shape, RpcCallState.Current) : manager.UnmarshalMarshalable<T>(JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(rawValue)), shape, RpcCallState.Current);
		}

		public override void Write(ref Nerdbank.Json.JsonWriter writer, T? value, Nerdbank.Json.SerializationContext context)
		{
			if (value is null)
			{
				writer.WriteNullValue();
				return;
			}

			JsonRpcValue marker = this.typeIsObserver ? ObserverMarshaler.Marshal(manager, value, shape, JsonRpcEncoding.Json, RpcCallState.Current) : this.typeIsProgress ? ProgressMarshaler.Marshal(progress, value, shape, JsonRpcEncoding.Json, RpcCallState.Current) : manager.MarshalMarshalable(value, shape, JsonRpcEncoding.Json, RpcCallState.Current);
			writer.WriteRawValue(Encoding.UTF8.GetString(marker.OwnedBytes.Span));
		}
	}
}
