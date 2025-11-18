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

	private delegate void ParameterSetter<TArgumentState>(DispatchRequest request, ref MessagePackReader reader, ref TArgumentState state);

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
				TArgumentState argState = argStateCtor();

				if (!dispatch.Request.Arguments.MsgPack.IsEmpty)
				{
					MessagePackReader reader = new(dispatch.Request.Arguments);
					switch (reader.NextMessagePackType)
					{
						case MessagePackType.Map:
							int argCount = reader.ReadMapHeader();
							for (int i = 0; i < argCount; i++)
							{
								// Read the key, without decoding it.
								ReadOnlySpan<byte> parameterNameUtf8 = reader.ReadStringSpan();
								if (!parameterNameToIndex.TryGetValue(parameterNameUtf8, out IParameterShape? parameterShape))
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
													Message = $"Unknown parameter name: '{StringEncoding.UTF8.GetString(parameterNameUtf8.ToArray())}'.",
												},
											}
											: null,
									};
								}

								parameterSetters.Span[parameterShape.Position](dispatch, ref reader, ref argState);
							}

							break;
						case MessagePackType.Array:
							argCount = reader.ReadArrayHeader();
							if (argCount > parameterSetters.Length)
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
												Message = $"Expected at most {parameterSetters.Length} arguments but received {argCount}.",
											},
										}
										: null,
								};
							}

							for (int i = 0; i < argCount; i++)
							{
								parameterSetters.Span[i](dispatch, ref reader, ref argState);
							}

							break;
						default:
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
												Message = "params must be either an object or an array.",
											},
										}
										: null,
									IsProtocolViolation = true,
								};
							}
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
							Result = (RawMessagePack)dispatch.UserDataSerializer.Serialize(result, methodShape.ReturnType, dispatch.JsonRpc.DisposalToken),
						};
					}
					else
					{
						response = null;
					}
				}
				catch (Exception ex)
				{
					if (dispatch.Request.Id is RequestId id)
					{
						response = new JsonRpcError
						{
							Id = id,
							Error = new JsonRpcErrorDetails
							{
								Message = ex.Message,
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

		return new ParameterSetter<TArgumentState>((DispatchRequest request, ref MessagePackReader argumentReader, ref TArgumentState argState) =>
		{
			TParameterType value = request.UserDataSerializer.Deserialize(ref argumentReader, parameterShape.ParameterType, request.CancellationToken)!;
			setter(ref argState, value);
		});
	}
}
