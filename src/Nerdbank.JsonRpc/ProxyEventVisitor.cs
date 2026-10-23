// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

/// <summary>Builds proxy event receivers using the ordinary RPC argument binding pipeline.</summary>
internal sealed class ProxyEventVisitor : TypeShapeVisitor
{
	/// <summary>The shared visitor instance.</summary>
	internal static readonly ProxyEventVisitor Instance = new();

	/// <summary>The visitor for serialized event parameters.</summary>
	internal static readonly TypeShapeVisitor ParameterVisitor = new EventParameterVisitor();

	public override object? VisitFunction<TFunction, TArgumentState, TResult>(IFunctionTypeShape<TFunction, TArgumentState, TResult> functionShape, object? state = null)
	{
		if (!functionShape.IsVoidLike || functionShape.IsAsync)
		{
			throw new NotSupportedException($"Only synchronous, void-returning event handlers are supported, but '{typeof(TFunction)}' does not qualify.");
		}

		bool omitSender = RpcTargetVisitor.IsBclEventHandlerDelegate(typeof(TFunction));
		int offset = omitSender ? 1 : 0;
		Func<TArgumentState> constructor = functionShape.GetArgumentStateConstructor();
		Setter<TArgumentState, object?>? setSender = omitSender ? (Setter<TArgumentState, object?>)functionShape.Parameters[0].Accept(ParameterVisitor, true)! : null;
		return RpcTargetVisitor.Instance.CreateMethodInvoker(
			functionShape.Parameters.Skip(offset).ToArray(),
			() =>
			{
				TArgumentState arguments = constructor();
				setSender?.Invoke(ref arguments, null);
				return arguments;
			},
			functionShape.GetFunctionInvoker(),
			functionShape.ReturnType,
			isEvent: true,
			parameterOffset: offset);
	}

	/// <summary>Creates setters without treating cancellation tokens as RPC control parameters.</summary>
	private sealed class EventParameterVisitor : TypeShapeVisitor
	{
		public override object? VisitParameter<TArgumentState, TParameterType>(IParameterShape<TArgumentState, TParameterType> parameterShape, object? state = null)
		{
			Setter<TArgumentState, TParameterType> setter = parameterShape.GetSetter();
			if (state is true)
			{
				return setter;
			}

			return new RpcTargetVisitor.ParameterSetter<TArgumentState>((DispatchRequest dispatch, JsonRpcValue value, ref TArgumentState arguments) =>
			{
				TParameterType parameter = dispatch.UserDataSerializer.Deserialize(value, parameterShape.ParameterType, dispatch.CallState, dispatch.CancellationToken);
				setter(ref arguments, parameter);
			});
		}
	}
}
