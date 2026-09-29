// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Text.Json;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

internal static class ObserverMarshaler
{
	internal static JsonRpcValue Marshal<T>(MarshaledObjectManager manager, T observer, ITypeShape<T> shape, JsonRpcEncoding encoding, RpcCallState? callState)
		=> ((ObserverContract)GetObserverContract(shape)).Marshal(manager, observer!, encoding, callState);

	internal static object CreateProxy<T>(MarshaledObjectManager manager, JsonRpcValue marker, ITypeShape<T> shape, RpcCallState? callState)
		=> ((ObserverContract)GetObserverContract(shape)).Unmarshal(manager, marker, callState);

	internal static TargetRegistration CreateRegistration<T>(ITypeShape<T> valueShape)
	{
		Dictionary<string, MethodInvoker> methods = new(StringComparer.Ordinal)
		{
			["onNext"] = dispatch => DispatchAsync(dispatch, "onNext", valueShape),
			["onCompleted"] = dispatch => DispatchAsync(dispatch, "onCompleted", valueShape),
			["onError"] = dispatch => DispatchAsync(dispatch, "onError", valueShape),
		};
		return new(methods, []);
	}

	private static ValueTask<DispatchResponse> DispatchAsync<T>(DispatchRequest dispatch, string method, ITypeShape<T> shape)
	{
		try
		{
			IObserver<T> observer = (IObserver<T>)dispatch.TargetInstance!;
			(bool named, List<(string? Name, JsonRpcValue Value)> values) = dispatch.UserDataSerializer.ReadArguments(dispatch.Request);
			if (named || values.Count != (method == "onCompleted" ? 0 : 1))
			{
				throw new FormatException($"Invalid arguments for observer method '{method}'.");
			}

			switch (method)
			{
				case "onNext":
					observer.OnNext(dispatch.UserDataSerializer.Deserialize(values[0].Value, shape, dispatch.CancellationToken));
					break;
				case "onError":
					observer.OnError(new Exception(ReadErrorMessage(values[0].Value)));
					break;
				case "onCompleted":
					observer.OnCompleted();
					break;
			}
		}
		catch (Exception ex)
		{
			dispatch.JsonRpc.LogApplicationError(ex);
		}

		return new(default(DispatchResponse));
	}

	private static ObserverContract GetObserverContract<T>(ITypeShape<T> shape)
	{
		Type valueType = shape.Type.GetGenericArguments()[0];
		ITypeShape valueShape = shape.Provider.GetTypeShape(valueType)
			?? throw new NotSupportedException($"A PolyType shape for observer value type '{valueType}' is required.");
		return (ObserverContract)valueShape.Invoke(ObserverShapeFunc.Instance)!;
	}

	private static string ReadErrorMessage(JsonRpcValue value)
	{
		if (value.Encoding == JsonRpcEncoding.Json)
		{
			using JsonDocument document = JsonDocument.Parse(value.OwnedBytes);
			JsonElement error = document.RootElement;
			if (error.TryGetProperty("Message", out JsonElement message) || error.TryGetProperty("message", out message))
			{
				return message.GetString() ?? throw new FormatException("Observer error message is null.");
			}

			throw new FormatException("Observer error message is missing.");
		}

		MessagePackReader reader = new(value.AsOwnedMessagePack());
		SerializationContext context = new();
		int count = reader.ReadMapHeader();
		for (int i = 0; i < count; i++)
		{
			string? key = reader.ReadString();
			if (key is "Message" or "message")
			{
				return reader.ReadString() ?? throw new FormatException("Observer error message is null.");
			}

			reader.Skip(context);
		}

		throw new FormatException("Observer error message is missing.");
	}

	internal sealed class Proxy<T>(JsonRpc owner, MarshaledObjectManager manager, long handle, ITypeShape<T> valueShape, MarshaledObjectManager.CallScopedHandle state) : IObserver<T>
	{
		private readonly object sync = new();
		private int terminated;

		public void OnNext(T value)
		{
			lock (this.sync)
			{
				this.ThrowIfTerminated();
				JsonRpcValue arguments;
				using (JsonRpcArgumentsBuilder builder = owner.CreateArguments(named: false, 1))
				{
					builder.Add(null, value, valueShape);
					arguments = builder.Build();
				}

				manager.EnsureNoMarshaledObjects(arguments);
				owner.PostMarshaledNotification($"$/invokeProxy/{handle}/onNext", arguments);
			}
		}

		public void OnError(Exception error)
		{
			if (error is null)
			{
				throw new ArgumentNullException(nameof(error));
			}

			this.ThrowIfTerminated();
			JsonRpcValue arguments;
			if (owner.UserDataSerializer.Encoding == JsonRpcEncoding.Json)
			{
				using Sequence<byte> buffer = new();
				using Utf8JsonWriter writer = new(buffer);
				writer.WriteStartArray();
				writer.WriteStartObject();
				writer.WriteString("Message", error.Message);
				writer.WriteEndObject();
				writer.WriteEndArray();
				writer.Flush();
				arguments = JsonRpcValue.FromOwnedBytes(buffer.AsReadOnlySequence.ToArray(), JsonRpcEncoding.Json);
			}
			else
			{
				using Sequence<byte> buffer = new();
				MessagePackWriter writer = new(buffer);
				writer.WriteArrayHeader(1);
				writer.WriteMapHeader(1);
				writer.Write("Message");
				writer.Write(error.Message);
				writer.Flush();
				arguments = JsonRpcValue.FromOwnedBytes(buffer.AsReadOnlySequence.ToArray(), JsonRpcEncoding.MessagePack);
			}

			this.Terminate("onError", arguments);
		}

		public void OnCompleted()
		{
			this.ThrowIfTerminated();
			JsonRpcValue arguments;
			using (JsonRpcArgumentsBuilder builder = owner.CreateArguments(named: false, 0))
			{
				arguments = builder.Build();
			}

			this.Terminate("onCompleted", arguments);
		}

		private void Terminate(string method, JsonRpcValue arguments)
		{
			lock (this.sync)
			{
				this.ThrowIfTerminated();
				Interlocked.Exchange(ref this.terminated, 1);
				try
				{
					owner.PostMarshaledNotification($"$/invokeProxy/{handle}/{method}", arguments);
				}
				finally
				{
					manager.Release(handle);
				}
			}
		}

		private void ThrowIfTerminated()
		{
			state.ThrowIfExpired();
			if (Volatile.Read(ref this.terminated) != 0)
			{
				throw new ObjectDisposedException("marshaled observer");
			}
		}
	}

	private abstract class ObserverContract
	{
		public abstract JsonRpcValue Marshal(MarshaledObjectManager manager, object observer, JsonRpcEncoding encoding, RpcCallState? callState);

		public abstract object Unmarshal(MarshaledObjectManager manager, JsonRpcValue marker, RpcCallState? callState);
	}

	private sealed class Contract<T>(ITypeShape<T> valueShape) : ObserverContract
	{
		public override JsonRpcValue Marshal(MarshaledObjectManager manager, object observer, JsonRpcEncoding encoding, RpcCallState? callState)
			=> manager.MarshalObserver((IObserver<T>)observer, valueShape, encoding, callState);

		public override object Unmarshal(MarshaledObjectManager manager, JsonRpcValue marker, RpcCallState? callState)
			=> manager.UnmarshalObserver(marker, valueShape, callState);
	}

	private sealed class ObserverShapeFunc : ITypeShapeFunc
	{
		internal static readonly ObserverShapeFunc Instance = new();

		public object? Invoke<T>(ITypeShape<T> shape, object? state = null) => new Contract<T>(shape);
	}
}
