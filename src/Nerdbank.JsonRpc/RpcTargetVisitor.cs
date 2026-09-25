// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.Threading;
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
		JsonRpcTargetOptions options = state as JsonRpcTargetOptions ?? new JsonRpcTargetOptions();
		Dictionary<string, MethodInvoker> methodInvokers = new(StringComparer.Ordinal);
		foreach (IMethodShape method in objectShape.Methods)
		{
			string rpcMethodName = GetRpcMethodName(method, options);
			MethodInvoker invoker = (MethodInvoker)method.Accept(this)!;
			if (!methodInvokers.TryAdd(rpcMethodName, invoker))
			{
				throw new InvalidOperationException($"Multiple methods on '{typeof(T)}' map to the JSON-RPC method name '{rpcMethodName}'. Assign each an explicit name via [MethodShape(Name = \"...\")] or configure a different {nameof(JsonRpcTargetOptions)}.{nameof(JsonRpcTargetOptions.MethodNameTransform)}.");
			}
		}

		List<IEventTargetRegistration> eventRegistrations = [];
		if (options.NotifyClientOfEvents)
		{
			foreach (IEventShape @event in objectShape.Events)
			{
				if (@event.Accept(this, options) is IEventTargetRegistration registration)
				{
					eventRegistrations.Add(registration);
				}
			}
		}

		return new TargetRegistration(methodInvokers, eventRegistrations);
	}

	public override object? VisitEvent<TDeclaringType, TEventHandler>(IEventShape<TDeclaringType, TEventHandler> eventShape, object? state = null)
	{
		if (eventShape.IsStatic)
		{
			// There's no single target instance to bind a static event's handler removal to; skip it.
			return null;
		}

		var options = (JsonRpcTargetOptions)state!;
		if (eventShape.HandlerType.Accept(this) is not CreateEventHandlerDelegate createHandler)
		{
			throw new NotSupportedException($"The event '{eventShape.DeclaringType.Type}.{eventShape.Name}' has an unsupported handler delegate type '{eventShape.HandlerType.Type}'. Only synchronous, void-returning delegates are supported for RPC event notifications.");
		}

		string rpcEventName = GetRpcEventName(eventShape, options);
		Setter<TDeclaringType?, TEventHandler> addHandler = eventShape.GetAddHandler();
		Setter<TDeclaringType?, TEventHandler> removeHandler = eventShape.GetRemoveHandler();

		return new EventRegistration<TDeclaringType, TEventHandler>(rpcEventName, createHandler, addHandler, removeHandler);
	}

	public override object? VisitFunction<TFunction, TArgumentState, TResult>(IFunctionTypeShape<TFunction, TArgumentState, TResult> functionShape, object? state = null)
	{
		if (!functionShape.IsVoidLike || functionShape.IsAsync)
		{
			throw new NotSupportedException($"Only synchronous, void-returning event handler delegates are supported for RPC event notifications, but '{typeof(TFunction)}' does not qualify.");
		}

		IReadOnlyList<IParameterShape> parameters = functionShape.Parameters;

		// Honor the BCL's EventHandler and EventHandler<T> delegates specifically (not merely delegates that happen to
		// share their (object? sender, TEventArgs e) shape) by excluding the sender from the notification payload.
		int firstForwardedParameter = IsBclEventHandlerDelegate(typeof(TFunction)) ? 1 : 0;

		var writers = new EventArgumentWriter<TArgumentState>[parameters.Count - firstForwardedParameter];
		for (int i = firstForwardedParameter; i < parameters.Count; i++)
		{
			writers[i - firstForwardedParameter] = (EventArgumentWriter<TArgumentState>)parameters[i].Accept(EventParameterVisitor.Instance)!;
		}

		return new CreateEventHandlerDelegate((jsonRpc, eventName) =>
		{
			return (Delegate)(object)functionShape.FromDelegate((ref TArgumentState argState) =>
			{
				NotifyEvent(jsonRpc, eventName, writers, ref argState);
				return default!;
			})!;
		});
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
							Result = dispatch.UserDataSerializer.Serialize(result, methodShape.ReturnType, dispatch.JsonRpc.DisposalToken),
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

		return new ParameterSetter<TArgumentState>((DispatchRequest request, JsonRpcValue argument, ref TArgumentState argState) =>
		{
			TParameterType value = (TParameterType)request.UserDataSerializer.DeserializeObject(argument, parameterShape.ParameterType, request.CancellationToken)!;
			setter(ref argState, value);
		});
	}

	/// <summary>
	/// Determines the JSON-RPC method name that dispatches to the given method, honoring an explicit
	/// <see cref="MethodShapeAttribute.Name"/> if present and otherwise applying the configured method name transform.
	/// </summary>
	/// <param name="method">The method whose RPC name is being resolved.</param>
	/// <param name="options">The options containing the method name transform to apply to implicitly named methods.</param>
	/// <returns>The JSON-RPC method name to register for dispatch.</returns>
	private static string GetRpcMethodName(IMethodShape method, JsonRpcTargetOptions options)
	{
		if (method.AttributeProvider?.GetCustomAttribute<MethodShapeAttribute>(inherit: false)?.Name is not null)
		{
			// An explicit name is authoritative and bypasses the configured transform.
			return method.Name;
		}

		string? transformed = options.MethodNameTransform(method.Name);
		if (string.IsNullOrEmpty(transformed))
		{
			throw new InvalidOperationException($"The {nameof(JsonRpcTargetOptions)}.{nameof(JsonRpcTargetOptions.MethodNameTransform)} delegate returned a null or empty value for method '{method.Name}'.");
		}

		return transformed;
	}

	/// <summary>
	/// Determines the JSON-RPC method name used in the notification raised for the given event, honoring an explicit
	/// <see cref="EventShapeAttribute.Name"/> if present and otherwise applying the configured event name transform.
	/// </summary>
	/// <param name="event">The event whose notification name is being resolved.</param>
	/// <param name="options">The options containing the event name transform to apply to implicitly named events.</param>
	/// <returns>The JSON-RPC method name to use when notifying the remote party that this event was raised.</returns>
	private static string GetRpcEventName(IEventShape @event, JsonRpcTargetOptions options)
	{
		if (@event.AttributeProvider?.GetCustomAttribute<EventShapeAttribute>(inherit: false)?.Name is not null)
		{
			// An explicit name is authoritative and bypasses the configured transform.
			return @event.Name;
		}

		string? transformed = options.EventNameTransform(@event.Name);
		if (string.IsNullOrEmpty(transformed))
		{
			throw new InvalidOperationException($"The {nameof(JsonRpcTargetOptions)}.{nameof(JsonRpcTargetOptions.EventNameTransform)} delegate returned a null or empty value for event '{@event.Name}'.");
		}

		return transformed;
	}

	/// <summary>
	/// Determines whether a delegate type is exactly <see cref="EventHandler"/> or the generic <see cref="EventHandler{TEventArgs}"/>,
	/// as opposed to some other delegate that merely happens to share their (object? sender, TEventArgs e) parameter shape.
	/// </summary>
	private static bool IsBclEventHandlerDelegate(Type delegateType)
	{
		if (delegateType == typeof(EventHandler))
		{
			return true;
		}

		return delegateType.IsGenericType && delegateType.GetGenericTypeDefinition() == typeof(EventHandler<>);
	}

	/// <summary>
	/// Serializes an event's arguments and sends them to the remote party as a JSON-RPC notification, logging (rather than throwing)
	/// any failure since this runs as a side effect of the target object raising a CLR event.
	/// </summary>
	private static void NotifyEvent<TArgumentState>(JsonRpc jsonRpc, string eventName, EventArgumentWriter<TArgumentState>[] writers, ref TArgumentState argState)
	{
		JsonRpcValue arguments;
		try
		{
			JsonRpcArgumentsBuilder builder = jsonRpc.CreateArguments(named: false, writers.Length, jsonRpc.DisposalToken);
			try
			{
				foreach (EventArgumentWriter<TArgumentState> writer in writers)
				{
					writer(ref argState, ref builder);
				}

				arguments = builder.Build();
			}
			finally
			{
				builder.Dispose();
			}
		}
		catch (Exception ex)
		{
			jsonRpc.LogApplicationError(ex);
			return;
		}

		NotifyEventCoreAsync(jsonRpc, eventName, arguments).Forget();
	}

	/// <summary>
	/// Sends a previously-built notification to the remote party, logging (rather than throwing) any failure.
	/// </summary>
	/// <param name="jsonRpc">The connection to send the notification over.</param>
	/// <param name="eventName">The RPC notification (method) name.</param>
	/// <param name="arguments">The already-serialized notification arguments.</param>
	/// <returns>A task that tracks completion of the send.</returns>
	private static async Task NotifyEventCoreAsync(JsonRpc jsonRpc, string eventName, JsonRpcValue arguments)
	{
		try
		{
			await jsonRpc.NotifyAsync(eventName, arguments, jsonRpc.DisposalToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			jsonRpc.LogApplicationError(ex);
		}
	}

	/// <summary>Visits the parameters of an event handler delegate shape, producing getters instead of the setters used for inbound method dispatch.</summary>
	private sealed class EventParameterVisitor : TypeShapeVisitor
	{
		internal static readonly EventParameterVisitor Instance = new();

		private EventParameterVisitor()
		{
		}

		public override object? VisitParameter<TArgumentState, TParameterType>(IParameterShape<TArgumentState, TParameterType> parameterShape, object? state = null)
		{
			Getter<TArgumentState, TParameterType> getter = parameterShape.GetGetter();
			ITypeShape<TParameterType> parameterType = parameterShape.ParameterType;
			string parameterName = parameterShape.Name;

			return new EventArgumentWriter<TArgumentState>((ref TArgumentState argState, ref JsonRpcArgumentsBuilder builder) =>
			{
				TParameterType value = getter(ref argState);
				builder.Add(parameterName, value, parameterType);
			});
		}
	}
}
