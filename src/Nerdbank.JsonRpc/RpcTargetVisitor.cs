// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Runtime.CompilerServices;
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

	/// <summary>Deserializes one wire argument into invocation state.</summary>
	/// <typeparam name="TArgumentState">The invocation state type.</typeparam>
	/// <param name="request">The request being dispatched.</param>
	/// <param name="value">The serialized argument.</param>
	/// <param name="state">The state to update.</param>
	internal delegate void ParameterSetter<TArgumentState>(DispatchRequest request, JsonRpcValue value, ref TArgumentState state);

	private delegate void SpecialParameterSetter<TParameterType, TArgumentState>(in TParameterType reader, ref TArgumentState state);

	public override object? VisitObject<T>(IObjectTypeShape<T> objectShape, object? state = null)
	{
		JsonRpcTargetOptions options = state as JsonRpcTargetOptions ?? JsonRpcTargetOptions.Default;
		Dictionary<string, MethodInvoker> methodInvokers = new(StringComparer.Ordinal);
		List<JsonRpcMethodDescription> methods = [];
		foreach (IMethodShape method in objectShape.Methods)
		{
			string rpcMethodName = GetRpcMethodName(method, options);
			MethodInvoker invoker = (MethodInvoker)method.Accept(this)!;
			if (!methodInvokers.TryAdd(rpcMethodName, invoker))
			{
				throw new InvalidOperationException($"Multiple methods on '{typeof(T)}' map to the JSON-RPC method name '{rpcMethodName}'. Assign each an explicit name via [MethodShape(Name = \"...\")] or configure a different {nameof(JsonRpcTargetOptions)}.{nameof(JsonRpcTargetOptions.MethodNameTransform)}.");
			}

			methods.Add(JsonRpcMethodDescription.Create(method, rpcMethodName));
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

		return new TargetRegistration(methodInvokers, methods, eventRegistrations);
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
		=> this.CreateMethodInvoker(methodShape.Parameters, methodShape.GetArgumentStateConstructor(), methodShape.GetMethodInvoker(), methodShape.ReturnType);

	public override object? VisitParameter<TArgumentState, TParameterType>(IParameterShape<TArgumentState, TParameterType> parameterShape, object? state = null)
	{
		Setter<TArgumentState, TParameterType> setter = parameterShape.GetSetter();

		if (typeof(TParameterType) == typeof(CancellationToken) || typeof(TParameterType) == typeof(DispatchRequest))
		{
			return new SpecialParameterSetter<TParameterType, TArgumentState>((in TParameterType argument, ref TArgumentState argState) => setter(ref argState, argument));
		}

		return new ParameterSetter<TArgumentState>((DispatchRequest request, JsonRpcValue argument, ref TArgumentState argState) =>
		{
			TParameterType value = request.UserDataSerializer.Deserialize(argument, parameterShape.ParameterType, request.CallState, request.CancellationToken);
			setter(ref argState, value);
		});
	}

	/// <summary>
	/// Determines whether a delegate type is exactly <see cref="EventHandler"/> or the generic <see cref="EventHandler{TEventArgs}"/>,
	/// as opposed to some other delegate that merely happens to share their (object? sender, TEventArgs e) parameter shape.
	/// </summary>
	/// <param name="delegateType">The delegate type to examine.</param>
	/// <returns>Whether the delegate is a BCL event handler.</returns>
	internal static bool IsBclEventHandlerDelegate(Type delegateType)
	{
		if (delegateType == typeof(EventHandler))
		{
			return true;
		}

		return delegateType.IsGenericType && delegateType.GetGenericTypeDefinition() == typeof(EventHandler<>);
	}

	/// <summary>Builds the common argument binding and dispatch pipeline for methods and proxy events.</summary>
	/// <typeparam name="TDeclaringType">The invocation target type.</typeparam>
	/// <typeparam name="TArgumentState">The argument state type.</typeparam>
	/// <typeparam name="TResult">The invocation result type.</typeparam>
	/// <param name="parameters">The wire parameters.</param>
	/// <param name="argStateCtor">Creates initialized argument state.</param>
	/// <param name="invoker">Invokes the target.</param>
	/// <param name="returnType">The result shape.</param>
	/// <param name="isEvent">Whether all parameters are ordinary serialized values.</param>
	/// <param name="parameterOffset">The number of omitted leading parameters.</param>
	/// <returns>The dispatch callback.</returns>
	internal MethodInvoker CreateMethodInvoker<TDeclaringType, TArgumentState, TResult>(IReadOnlyList<IParameterShape> parameters, Func<TArgumentState> argStateCtor, MethodInvoker<TDeclaringType, TArgumentState, TResult> invoker, ITypeShape<TResult> returnType, bool isEvent = false, int parameterOffset = 0)
		where TArgumentState : IArgumentState
	{
		SpecialParameterSetter<CancellationToken, TArgumentState>? setCancellationToken = null;
		SpecialParameterSetter<DispatchRequest, TArgumentState>? setDispatchRequest = null;
		Memory<ParameterSetter<TArgumentState>> parameterSetters = new ParameterSetter<TArgumentState>[parameters.Count];
		for (int i = 0; i < parameters.Count; i++)
		{
			IParameterShape parameter = parameters[i];
			switch (parameter.Accept(isEvent ? ProxyEventVisitor.ParameterVisitor : this))
			{
				case SpecialParameterSetter<CancellationToken, TArgumentState> ctSetter when i == parameters.Count - 1:
					setCancellationToken = ctSetter;
					parameterSetters = parameterSetters[..^1]; // trim the last one because it's special.
					break;
				case SpecialParameterSetter<DispatchRequest, TArgumentState> dispatchSetter when i == parameters.Count - 1:
					setDispatchRequest = dispatchSetter;
					parameterSetters = parameterSetters[..^1];
					break;
				case ParameterSetter<TArgumentState> paramSetter:
					parameterSetters.Span[i] = paramSetter;
					break;
				default:
					throw new NotSupportedException();
			}
		}

		// Build up a parameter name to index lookup table.
		SpanDictionary<byte, IParameterShape> parameterNameToIndex = parameters
			.Where(p => isEvent || (p.ParameterType.Type != typeof(CancellationToken) && p.ParameterType.Type != typeof(DispatchRequest)))
			.ToSpanDictionary(
			p =>
			{
				StringEncoding.GetEncodedStringBytes(p.Name, out ReadOnlyMemory<byte> utf8Name, out _);
				return utf8Name;
			},
			ByteSpanEqualityComparer.Ordinal);

		return new MethodInvoker(InvokeAsync);

		// Pooled because the dispatcher awaits each invocation exactly once.
#if NET
		[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
		async ValueTask<DispatchResponse> InvokeAsync(DispatchRequest dispatch)
		{
			bool isError = false;
			TArgumentState argState;
			ArgumentList values = default;
			try
			{
				try
				{
					argState = argStateCtor();

					if (dispatch.Request.Arguments.HasValue)
					{
						(bool named, values) = dispatch.UserDataSerializer.ReadArguments(dispatch.Request);
						if (!named && values.Count > parameterSetters.Length)
						{
							return new DispatchResponse
							{
								Response = dispatch.Request.Id is RequestId id
								? new JsonRpcError { Id = id, Error = new() { Code = JsonRpcErrorCode.InvalidParams, Message = $"Expected at most {parameterSetters.Length} arguments but received {values.Count}." } }
								: null,
								IsError = true,
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
										IsError = true,
									};
								}

								index = parameterShape.Position - parameterOffset;
							}

							parameterSetters.Span[index](dispatch, values[i].Value, ref argState);
						}
					}

					setCancellationToken?.Invoke(dispatch.CancellationToken, ref argState);
					setDispatchRequest?.Invoke(dispatch, ref argState);

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
							IsError = true,
						};
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					dispatch.JsonRpc.LogApplicationError(ex);
					return new DispatchResponse
					{
						Response = dispatch.Request.Id is RequestId id
							? new JsonRpcError { Id = id, Error = dispatch.JsonRpc.CreateApplicationError(ex, JsonRpcErrorCode.InvalidParams) }
							: null,
						IsError = true,
					};
				}
			}
			finally
			{
				// Every argument has been materialized (or rejected), so the pooled request buffer is no longer needed.
				dispatch.Request.Arguments.Release();
				dispatch.Request.SplitArguments = default;
				values.Return();
			}

			var target = (TDeclaringType)dispatch.TargetInstance!;
			JsonRpcResponse? response;
			try
			{
				TResult result = await invoker(ref target, ref argState).ConfigureAwait(false);
				if (typeof(TResult) == typeof(DispatchResponse))
				{
					return (DispatchResponse)(object)result!;
				}

				if (result is IRpcEnumerable prefetchable && dispatch.Request.Id is not null)
				{
					// Prefetching only matters if the result will actually be serialized and sent back; a
					// notification has no response to carry it, and an unbounded sequence could block forever.
					await prefetchable.PrefetchAsync(dispatch.CancellationToken).ConfigureAwait(false);
				}

				if (dispatch.Request.Id is RequestId id)
				{
					response = new JsonRpcResult
					{
						Id = id,
						Result = dispatch.JsonRpc.SerializeMarshaledResult(result, returnType, dispatch.CallState, dispatch.JsonRpc.DisposalToken),
					};
				}
				else
				{
					response = null;
				}
			}
			catch (OperationCanceledException ex) when (dispatch.CancellationToken.IsCancellationRequested && dispatch.Request.Id is RequestId id)
			{
				dispatch.JsonRpc.LogApplicationError(ex);
				response = new JsonRpcError
				{
					Id = id,
					Error = dispatch.JsonRpc.CreateApplicationError(ex, JsonRpcErrorCode.RequestCancelled),
				};
			}
			catch (Exception ex)
			{
				isError = true;
				dispatch.JsonRpc.LogApplicationError(ex);
				if (dispatch.Request.Id is RequestId id)
				{
					response = new JsonRpcError
					{
						Id = id,
						Error = dispatch.JsonRpc.CreateApplicationError(ex, JsonRpcErrorCode.InternalError),
					};
				}
				else
				{
					response = null;
				}
			}

			return new DispatchResponse { Response = response, IsError = isError || response is JsonRpcError };
		}
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
