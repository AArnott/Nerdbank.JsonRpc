// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Reflection;
using System.Text;
using PolyType;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

internal sealed class MarshaledInterfaceJsonConverterFactory(MarshaledObjectManager manager) : Nerdbank.Json.IJsonConverterFactory, ITypeShapeFunc
{
	public Nerdbank.Json.JsonConverter? CreateConverter(Type type, ITypeShape? shape, in Nerdbank.Json.JsonConverterFactoryContext context)
	{
		if (!type.IsInterface || !type.IsDefined(typeof(RpcMarshalableAttribute), inherit: false))
		{
			return null;
		}

		return shape is not null
			? (Nerdbank.Json.JsonConverter?)shape.Invoke(this, manager)
			: throw new NotSupportedException($"A PolyType shape is required to marshal interface '{type}'.");
	}

	public object? Invoke<T>(ITypeShape<T> shape, object? state) => new Converter<T>(manager, shape);

	private sealed class Converter<T>(MarshaledObjectManager manager, ITypeShape<T> shape) : Nerdbank.Json.JsonConverter<T>
	{
		public override T? Read(ref Nerdbank.Json.JsonReader reader, Nerdbank.Json.SerializationContext context)
		{
			string rawValue = reader.ReadRawValue();
			return rawValue == "null" ? default : manager.UnmarshalMarshalable<T>(JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(rawValue)), shape);
		}

		public override void Write(ref Nerdbank.Json.JsonWriter writer, T? value, Nerdbank.Json.SerializationContext context)
		{
			if (value is null)
			{
				writer.WriteNullValue();
				return;
			}

			JsonRpcValue marker = manager.MarshalMarshalable(value, shape, JsonRpcEncoding.Json);
			writer.WriteRawValue(Encoding.UTF8.GetString(marker.OwnedBytes.Span));
		}
	}
}
