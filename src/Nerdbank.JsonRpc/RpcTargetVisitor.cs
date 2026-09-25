// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

internal class RpcTargetVisitor : TypeShapeVisitor
{
	internal static readonly RpcTargetVisitor Instance = new RpcTargetVisitor();

	private RpcTargetVisitor()
	{
	}

	private delegate void ParameterSetter<TArgumentState>(DispatchRequest request, JsonRpcValue value, ref TArgumentState state);

	private delegate void SpecialParameterSetter<TParameterType, TArgumentState>(in TParameterType reader, ref TArgumentState state);

	public override object? VisitObject<T>(IObjectTypeShape<T> objectShape, object? state = null)
	{
		Dictionary<string, MethodInvoker> methodInvokers = new(StringComparer.Ordinal);
		foreach (IMethodShape method in objectShape.Methods)
		{
			methodInvokers.Add(method.Name, (MethodInvoker)method.Accept(this)!);
		}

		return methodInvokers;
	}

	public override object? VisitMethod<TDeclaringType, TArgumentState, TResult>(IMethodShape<TDeclaringType, TArgumentState, TResult> methodShape, object? state = null)
	{
		SpecialParameterSetter<CancellationToken, TArgumentState>? setCancellationToken = null;
		Memory<ParameterSetter<TArgumentState>> parameterSetters = new ParameterSetter<TArgumentState>[methodShape.Parameters.Count];
		for (int i = 0; i < methodShape.Parameters.Count; i++)
		{
			IParameterShape parameter = methodShape.Parameters[i];
			switch (parameter.Accept(this))
			{
				case SpecialParameterSetter<CancellationToken, TArgumentState> ctSetter when i == methodShape.Parameters.Count - 1:
					setCancellationToken = ctSetter;
					parameterSetters = parameterSetters[..^1]; // trim the last one because it's special.
					break;
				case ParameterSetter<TArgumentState> paramSetter:
					parameterSetters.Span[i] = paramSetter;
					break;
				default:
					throw new NotSupportedException();
			}
		}

		Func<TArgumentState> argStateCtor = methodShape.GetArgumentStateConstructor();
		MethodInvoker<TDeclaringType?, TArgumentState, TResult> invoker = methodShape.GetMethodInvoker();

		// Build up a parameter name to index lookup table.
		SpanDictionary<byte, IParameterShape> parameterNameToIndex = methodShape.Parameters
			.Where(p => p.ParameterType.Type != typeof(CancellationToken))
			.ToSpanDictionary(
			p =>
			{
				StringEncoding.GetEncodedStringBytes(p.Name, out ReadOnlyMemory<byte> utf8Name, out _);
				return utf8Name;
			},
			ByteSpanEqualityComparer.Ordinal);

		return new MethodInvoker(
			async dispatch =>
			{
				TArgumentState argState;
				try
				{
					argState = argStateCtor();

					if (dispatch.Request.Arguments.HasValue)
					{
						(bool named, List<(string? Name, JsonRpcValue Value)> values) = dispatch.UserDataSerializer.ReadArguments(dispatch.Request.Arguments);
						if (!named && values.Count > parameterSetters.Length)
						{
							return new DispatchResponse
							{
								Response = dispatch.Request.Id is RequestId id
								? new JsonRpcError { Id = id, Error = new() { Code = JsonRpcErrorCode.InvalidParams, Message = $"Expected at most {parameterSetters.Length} arguments but received {values.Count}." } }
								: null,
							};
						}

						for (int i = 0; i < values.Count; i++)
						{
							int index = i;
							if (named)
							{
								StringEncoding.GetEncodedStringBytes(values[i].Name!, out ReadOnlyMemory<byte> utf8Name, out _);
								if (!parameterNameToIndex.TryGetValue(utf8Name.Span, out IParameterShape? parameterShape))
								{
									return new DispatchResponse
									{
										Response = dispatch.Request.Id is RequestId id
										? new JsonRpcError { Id = id, Error = new() { Code = JsonRpcErrorCode.InvalidParams, Message = $"Unknown parameter name: '{values[i].Name}'." } }
										: null,
									};
								}

								index = parameterShape.Position;
							}

							parameterSetters.Span[index](dispatch, values[i].Value, ref argState);
						}
					}

					setCancellationToken?.Invoke(dispatch.CancellationToken, ref argState);

					if (!argState.AreRequiredArgumentsSet)
					{
						return new DispatchResponse
						{
							Response = dispatch.Request.Id is RequestId id
								? new JsonRpcError
								{
									Id = id,
									Error = new JsonRpcErrorDetails
									{
										Code = JsonRpcErrorCode.InvalidParams,
										Message = "Not all required parameters were provided.",
									},
								}
								: null,
						};
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					dispatch.JsonRpc.LogApplicationError(ex);
					return new DispatchResponse
					{
						Response = dispatch.Request.Id is RequestId id
							? new JsonRpcError { Id = id, Error = new JsonRpcErrorDetails { Code = JsonRpcErrorCode.InvalidParams, Message = "Could not deserialize request parameters." } }
							: null,
					};
				}

				var target = (TDeclaringType?)dispatch.TargetInstance;
				JsonRpcResponse? response;
				try
				{
					TResult result = await invoker(ref target, ref argState).ConfigureAwait(false);

					if (dispatch.Request.Id is RequestId id)
					{
						response = new JsonRpcResult
						{
							Id = id,
							Result = methodShape.ReturnType.Type == typeof(IDisposable) ? ((IJsonRpcClient)dispatch.JsonRpc).MarshalDisposable((IDisposable)(object)result!) : dispatch.UserDataSerializer.Serialize(result, methodShape.ReturnType, dispatch.JsonRpc.DisposalToken),
						};
					}
					else
					{
						response = null;
					}
				}
				catch (OperationCanceledException ex) when (dispatch.CancellationToken.IsCancellationRequested && dispatch.Request.Id is RequestId id)
				{
					response = new JsonRpcError
					{
						Id = id,
						Error = new JsonRpcErrorDetails
						{
							Message = ex.Message,
							Code = JsonRpcErrorCode.RequestCancelled,
						},
					};
				}
				catch (Exception ex)
				{
					dispatch.JsonRpc.LogApplicationError(ex);
					if (dispatch.Request.Id is RequestId id)
					{
						response = new JsonRpcError
						{
							Id = id,
							Error = new JsonRpcErrorDetails
							{
								Message = "The request could not be completed.",
								Code = JsonRpcErrorCode.InternalError,
							},
						};
					}
					else
					{
						response = null;
					}
				}

				return new DispatchResponse { Response = response };
			});
	}

	public override object? VisitParameter<TArgumentState, TParameterType>(IParameterShape<TArgumentState, TParameterType> parameterShape, object? state = null)
	{
		Setter<TArgumentState, TParameterType> setter = parameterShape.GetSetter();

		if (typeof(TParameterType) == typeof(CancellationToken))
		{
			return new SpecialParameterSetter<TParameterType, TArgumentState>((in TParameterType argument, ref TArgumentState argState) => setter(ref argState, argument));
		}

		if (typeof(TParameterType) == typeof(IDisposable))
		{
			return new ParameterSetter<TArgumentState>((DispatchRequest request, JsonRpcValue argument, ref TArgumentState argState) => setter(ref argState, (TParameterType)(object)((IJsonRpcClient)request.JsonRpc).UnmarshalDisposable(argument)));
		}

		return new ParameterSetter<TArgumentState>((DispatchRequest request, JsonRpcValue argument, ref TArgumentState argState) =>
		{
			TParameterType value = (TParameterType)request.UserDataSerializer.DeserializeObject(argument, parameterShape.ParameterType, request.CancellationToken)!;
			setter(ref argState, value);
		});
	}
}
