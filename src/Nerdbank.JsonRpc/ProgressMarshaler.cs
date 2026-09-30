// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

internal static class ProgressMarshaler
{
	internal static JsonRpcValue Marshal<T>(ProgressManager manager, T progress, ITypeShape<T> shape, JsonRpcEncoding encoding, RpcCallState? callState)
		=> ((ProgressContract)GetProgressContract(shape)).Marshal(manager, progress!, encoding, callState);

	internal static object CreateProxy<T>(ProgressManager manager, JsonRpcValue token, ITypeShape<T> shape, RpcCallState? callState)
		=> ((ProgressContract)GetProgressContract(shape)).Unmarshal(manager, token, callState);

	private static ProgressContract GetProgressContract<T>(ITypeShape<T> shape)
	{
#if NETWASM
		throw new PlatformNotSupportedException("IObserver<T>/IProgress<T> marshaling requires reflection over generic type arguments, which NetWasm does not support.");
#else
		Type valueType = shape.Type.GetGenericArguments()[0];
		ITypeShape valueShape = shape.Provider.GetTypeShape(valueType)
			?? throw new NotSupportedException($"A PolyType shape for progress value type '{valueType}' is required.");
		return (ProgressContract)valueShape.Invoke(ProgressShapeFunc.Instance)!;
#endif
	}

	private abstract class ProgressContract
	{
		internal abstract JsonRpcValue Marshal(ProgressManager manager, object progress, JsonRpcEncoding encoding, RpcCallState? callState);

		internal abstract object Unmarshal(ProgressManager manager, JsonRpcValue token, RpcCallState? callState);
	}

	private sealed class Contract<T>(ITypeShape<T> valueShape) : ProgressContract
	{
		internal override JsonRpcValue Marshal(ProgressManager manager, object progress, JsonRpcEncoding encoding, RpcCallState? callState)
			=> manager.Marshal((IProgress<T>)progress, valueShape, encoding, callState);

		internal override object Unmarshal(ProgressManager manager, JsonRpcValue token, RpcCallState? callState)
			=> manager.Unmarshal(token, valueShape, callState);
	}

	private sealed class ProgressShapeFunc : ITypeShapeFunc
	{
		internal static readonly ProgressShapeFunc Instance = new();

		public object? Invoke<T>(ITypeShape<T> shape, object? state = null) => new Contract<T>(shape);
	}
}
