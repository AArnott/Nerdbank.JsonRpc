// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

internal static class ProgressMarshaler
{
	internal static JsonRpcValue Marshal<T>(ProgressManager manager, T progress, ITypeShape<T> shape, JsonRpcEncoding encoding, RpcCallState? callState)
		=> ((ProgressContract)GetProgressContract(shape)).Marshal(manager, progress!, encoding, callState);

	/// <summary>Constructs a progress proxy matching the supplied contract shape.</summary>
	/// <typeparam name="T">The progress contract.</typeparam>
	/// <param name="manager">The connection's progress manager.</param>
	/// <param name="token">The remote progress reference.</param>
	/// <param name="shape">The progress contract shape.</param>
	/// <param name="callState">The receiving call's lifetime scopes.</param>
	/// <returns>The typed progress proxy.</returns>
	internal static T CreateProxy<T>(ProgressManager manager, JsonRpcValue token, ITypeShape<T> shape, RpcCallState? callState)
		=> (T)GetProgressContract(shape).Unmarshal(manager, token, callState);

	private static ProgressContract GetProgressContract<T>(ITypeShape<T> shape)
	{
		Type valueType = shape.Type.GetGenericArguments()[0];
		ITypeShape valueShape = shape.Provider.GetTypeShape(valueType)
			?? throw new NotSupportedException($"A PolyType shape for progress value type '{valueType}' is required.");
		return (ProgressContract)valueShape.Invoke(ProgressShapeFunc.Instance)!;
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
