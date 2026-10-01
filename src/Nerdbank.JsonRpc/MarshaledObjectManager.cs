// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

internal class MarshaledObjectManager(JsonRpc owner)
{
	private const string Marker = "__jsonrpc_marshaled";
	private const string Handle = "handle";
	private const string Lifetime = "lifetime";
	private const string OptionalInterfaces = "optionalInterfaces";
	private const string ReleaseMethod = "$/releaseMarshaledObject";
	private static readonly ConditionalWeakTable<object, RemoteObjectHandle> RemoteHandles = new();

	private readonly object sync = new();
	private readonly Dictionary<long, MarshaledLocalObject> localObjects = [];
	private readonly Dictionary<object, LocalObjectLease> localLeases = new(ReferenceEqualityComparer<object>.Instance);
	private readonly Dictionary<long, List<WeakReference<CallScopedHandle>>> remoteProxies = [];
	private readonly HashSet<long> revokedRemoteHandles = [];
	private long nextHandle;

	internal JsonRpcValue Marshal(IDisposable value, JsonRpcEncoding encoding, RpcCallState? callState)
	{
		if (value is null)
		{
			throw new ArgumentNullException(nameof(value));
		}

		return this.Marshal(value, registration: null, callScopedLifetime: false, encoding: encoding, callState: callState);
	}

	internal JsonRpcValue MarshalMarshalable<T>(T value, ITypeShape<T> shape, JsonRpcEncoding encoding, RpcCallState? callState)
	{
		if (value is null)
		{
			throw new ArgumentNullException(nameof(value));
		}

		RpcMarshalableAttribute attribute = MarshalableCache<T>.Marshalable
			?? throw new InvalidOperationException($"The interface '{shape.Type}' is not marked with {nameof(RpcMarshalableAttribute)}.");
		object target = value;
		if (!attribute.CallScopedLifetime && target is not IDisposable)
		{
			throw new InvalidOperationException($"Explicit-lifetime marshalable values of '{shape.Type}' must implement IDisposable.");
		}

		TargetRegistration registration = (TargetRegistration)shape.Accept(RpcTargetVisitor.Instance, owner.MarshaledTargetOptions)!;
		List<(int InterfaceId, TargetRegistration Registration)> optionalRegistrations = [];
		HashSet<int> interfaceIds = [];
		foreach (RpcMarshalableOptionalInterfaceAttribute optionalInterface in MarshalableCache<T>.OptionalInterfaces)
		{
			if (!interfaceIds.Add(optionalInterface.InterfaceId))
			{
				throw new InvalidOperationException($"Optional interface ID {optionalInterface.InterfaceId} is declared more than once on '{shape.Type}'.");
			}

			if (optionalInterface.OptionalInterface.IsInstanceOfType(target))
			{
				ITypeShape optionalShape = shape.Provider.GetTypeShape(optionalInterface.OptionalInterface)
					?? throw new NotSupportedException($"A PolyType shape is required for optional interface '{optionalInterface.OptionalInterface}'.");
				TargetRegistration optionalRegistration = (TargetRegistration)optionalShape.Accept(RpcTargetVisitor.Instance, owner.MarshaledTargetOptions)!;
				optionalRegistrations.Add((optionalInterface.InterfaceId, optionalRegistration));
			}
		}

		return this.Marshal(target, registration, attribute.CallScopedLifetime, encoding, callState: callState, optionalRegistrations: optionalRegistrations);
	}

	internal IDisposable Unmarshal(JsonRpcValue value, RpcCallState? callState)
	{
		(long handle, int direction, bool callScopedLifetime, int[] optionalInterfaceIds) = ReadMarker(value);
		if (callScopedLifetime)
		{
			throw new FormatException("The IDisposable marshaling contract does not support call-scoped lifetimes.");
		}

		if (direction == 0)
		{
			lock (this.sync)
			{
				if (this.localObjects.TryGetValue(handle, out MarshaledLocalObject? local) && local.Lease.Value is IDisposable disposable)
				{
					return disposable;
				}
			}

			throw new InvalidOperationException($"Marshaled object handle {handle} is not available.");
		}

		if (callState?.IsDeclaredForNotification(RpcCallState.Scopes.InboundCall) is true)
		{
			throw new FormatException("Marshaled objects cannot be received in notifications.");
		}

		return new RemoteDisposable(this, handle, this.RegisterIncomingProxy(handle, callScopedLifetime: false, callState));
	}

	internal T UnmarshalMarshalable<T>(JsonRpcValue value, ITypeShape<T> shape, RpcCallState? callState)
	{
		(long handle, int direction, bool callScopedLifetime, int[] optionalInterfaceIds) = ReadMarker(value);
		RpcMarshalableAttribute attribute = MarshalableCache<T>.Marshalable
			?? throw new InvalidOperationException($"The interface '{shape.Type}' is not marked with {nameof(RpcMarshalableAttribute)}.");
		if (attribute.CallScopedLifetime != callScopedLifetime)
		{
			throw new FormatException($"The marshaled lifetime does not match the {nameof(RpcMarshalableAttribute)} on '{shape.Type}'.");
		}

		if (callState?.IsDeclaredForNotification(RpcCallState.Scopes.InboundCall) is true)
		{
			throw new FormatException("Marshaled objects cannot be received in notifications.");
		}

		if (direction == 0)
		{
			lock (this.sync)
			{
				if (this.localObjects.TryGetValue(handle, out MarshaledLocalObject? local) && local.Lease.Value is T target)
				{
					return target;
				}
			}

			throw new InvalidOperationException($"Marshaled object handle {handle} is not available or does not implement '{shape.Type}'.");
		}

		CallScopedHandle callScopedHandle = this.RegisterIncomingProxy(handle, callScopedLifetime, callState);
		HashSet<int> advertisedInterfaces = [.. optionalInterfaceIds];
		HashSet<int> knownAdvertisedInterfaces = [.. MarshalableCache<T>.OptionalInterfaces
			.Where(attribute => advertisedInterfaces.Contains(attribute.InterfaceId))
			.Select(attribute => attribute.InterfaceId)];
		JsonRpcOptionalProxyFactoryAttribute? optionalProxyFactory = knownAdvertisedInterfaces.Count == 0 ? null : MarshalableCache<T>.OptionalProxyFactories
			.SingleOrDefault(attribute => attribute.InterfaceIds.Count == knownAdvertisedInterfaces.Count && attribute.InterfaceIds.All(knownAdvertisedInterfaces.Contains));
#pragma warning disable CS0618 // Support proxy metadata emitted by previous versions of the source generator.
		JsonRpcOptionalProxyImplementationAttribute? legacyOptionalProxy = knownAdvertisedInterfaces.Count == 0 ? null : MarshalableCache<T>.LegacyOptionalProxyImplementations
			.SingleOrDefault(attribute => attribute.InterfaceIds.Count == knownAdvertisedInterfaces.Count && attribute.InterfaceIds.All(knownAdvertisedInterfaces.Contains));
#pragma warning restore CS0618
		if (knownAdvertisedInterfaces.Count > 0 && optionalProxyFactory is null && legacyOptionalProxy is null)
		{
			throw new NotSupportedException($"No generated proxy supports the advertised optional interfaces on '{shape.Type}'.");
		}

		MarshaledObjectProxyClient client = new(owner, handle, callScopedHandle);

		// Prefer explicit legacy metadata when present. Old assemblies have no factory attributes, but this also makes the compatibility behavior deterministic if both are applied.
		T proxy = legacyOptionalProxy is not null
			? JsonRpc.CastProxy<T>(JsonRpc.CreateLegacyProxy(legacyOptionalProxy.ProxyType, client, owner.MarshaledProxyOptions))
			: optionalProxyFactory is not null
				? JsonRpc.CastProxy<T>(optionalProxyFactory.CreateProxy(client, owner.MarshaledProxyOptions))
				: JsonRpc.AttachCore<T>(client, owner.MarshaledProxyOptions);
		RemoteHandles.Add(proxy!, new(this, handle, callScopedLifetime, callScopedHandle));
		return proxy;
	}

	internal JsonRpcValue MarshalObserver<T>(IObserver<T> observer, ITypeShape<T> valueShape, JsonRpcEncoding encoding, RpcCallState? callState)
	{
		TargetRegistration registration = ObserverMarshaler.CreateRegistration(valueShape);
		return this.Marshal(observer, registration, callScopedLifetime: false, encoding, callState, disposeTarget: false);
	}

	internal IObserver<T> UnmarshalObserver<T>(JsonRpcValue value, ITypeShape<T> valueShape, RpcCallState? callState)
	{
		(long handle, int direction, bool callScopedLifetime, int[] optionalInterfaceIds) = ReadMarker(value);
		if (callScopedLifetime)
		{
			throw new FormatException("IObserver<T> requires an explicit lifetime.");
		}

		if (callState?.IsDeclaredForNotification(RpcCallState.Scopes.InboundCall) is true)
		{
			throw new FormatException("Marshaled observers cannot be received in notifications.");
		}

		if (direction == 0)
		{
			lock (this.sync)
			{
				if (this.localObjects.TryGetValue(handle, out MarshaledLocalObject? local) && local.Lease.Value is IObserver<T> observer)
				{
					return observer;
				}
			}

			throw new InvalidOperationException($"Marshaled observer handle {handle} is not available.");
		}

		CallScopedHandle state = this.RegisterIncomingProxy(handle, callScopedLifetime: false, callState);
		IObserver<T> proxy = new ObserverMarshaler.Proxy<T>(owner, this, handle, valueShape, state);
		RemoteHandles.Add(proxy, new(this, handle, false, state));
		return proxy;
	}

	internal bool TryGetMethodInvoker(JsonRpcRequest request, out object? target, out MethodInvoker invoker)
	{
		target = null;
		invoker = null!;
		if (!this.TryParseInvocation(request.Method, out long handle, out string method))
		{
			return false;
		}

		lock (this.sync)
		{
			if (this.localObjects.TryGetValue(handle, out MarshaledLocalObject? local) && local.MethodInvokers.TryGetValue(method, out MethodInvoker? foundInvoker))
			{
				target = local.Lease.Value;
				invoker = foundInvoker;
				return true;
			}
		}

		return false;
	}

	internal bool TryParseInvocation(string requestMethod, out long handle, out string method)
	{
		const string Prefix = "$/invokeProxy/";
		handle = 0;
		method = string.Empty;
		if (!requestMethod.StartsWith(Prefix, StringComparison.Ordinal))
		{
			return false;
		}

		int separator = requestMethod.IndexOf('/', Prefix.Length);
		if (separator < 0 || !long.TryParse(requestMethod.Substring(Prefix.Length, separator - Prefix.Length), out handle))
		{
			return false;
		}

		method = requestMethod[(separator + 1)..];
		return true;
	}

	internal bool TryHandleNotification(JsonRpcRequest request)
	{
		if (request.Method == ReleaseMethod)
		{
			(long handle, bool ownedBySender) = ReadReleaseArguments(request.Arguments);
			if (ownedBySender)
			{
				this.InvalidateRemote(handle);
			}
			else
			{
				this.ReleaseLocal(handle);
			}

			return true;
		}

		return false;
	}

	internal void Release(long handle) => owner.PostMarshaledNotification(ReleaseMethod, owner.MarshalReleaseArguments(handle));

	internal int Revoke(object target)
	{
		long[] handles;
		lock (this.sync)
		{
			if (!this.localLeases.TryGetValue(target, out LocalObjectLease? lease))
			{
				return 0;
			}

			handles = [.. lease.Handles];
			foreach (long handle in handles)
			{
				this.localObjects.Remove(handle);
			}

			lease.Handles.Clear();
			this.localLeases.Remove(target);
		}

		foreach (long handle in handles)
		{
			owner.PostMarshaledNotification(ReleaseMethod, owner.MarshalReleaseArguments(handle, ownedBySender: true));
		}

		return handles.Length;
	}

	internal bool IsMissingHandleInvocation(JsonRpcRequest request, out long handle)
	{
		if (!this.TryParseInvocation(request.Method, out handle, out _))
		{
			return false;
		}

		lock (this.sync)
		{
			return !this.localObjects.ContainsKey(handle);
		}
	}

	internal HandleScope TrackMarshaledObjects(RpcCallState callState, bool allowCallScopedLifetime = true)
	{
		callState.Declare(allowCallScopedLifetime ? RpcCallState.Scopes.MarshaledObjects | RpcCallState.Scopes.CallScopedLifetimeAllowed : RpcCallState.Scopes.MarshaledObjects);
		return new(callState);
	}

	internal InboundCallScope TrackInboundCall(bool hasResponse, RpcCallState callState)
	{
		callState.DeclareInbound(RpcCallState.Scopes.InboundCall, hasResponse);
		return new(callState);
	}

	internal void ReleaseLocalObjects(JsonRpcValue value) => value.MarshaledHandles?.ReleaseAll();

	internal void ReleaseCallScopedObjects(JsonRpcValue value) => value.MarshaledHandles?.ReleaseCallScoped();

	internal void EnsureNoMarshaledObjects(JsonRpcValue arguments)
	{
		if (arguments.MarshaledHandles is { HasMarshaledObjects: true } handles)
		{
			handles.ReleaseAll();
			throw new InvalidOperationException("Marshaled objects cannot be sent in notifications because the sender cannot know whether the receiver accepted them.");
		}
	}

	internal void DisposeAll()
	{
		IDisposable[] values;
		CallScopedHandle[] remoteProxyStates;
		lock (this.sync)
		{
			values = [.. this.localLeases.Values.Where(static lease => lease.DisposeTarget).Select(static lease => lease.Value).OfType<IDisposable>()];
			remoteProxyStates = [.. this.remoteProxies.Values.SelectMany(static proxies => proxies).Select(static reference => reference.TryGetTarget(out CallScopedHandle? state) ? state : null).OfType<CallScopedHandle>()];
			this.localObjects.Clear();
			this.localLeases.Clear();
			this.remoteProxies.Clear();
			this.revokedRemoteHandles.Clear();
		}

		foreach (CallScopedHandle state in remoteProxyStates)
		{
			state.Invalidate("The JSON-RPC connection closed.");
		}

		List<Exception>? exceptions = null;
		foreach (IDisposable value in values)
		{
			try
			{
				value.Dispose();
			}
			catch (Exception ex)
			{
				(exceptions ??= []).Add(ex);
			}
		}

		if (exceptions is not null)
		{
			throw new AggregateException("One or more marshaled objects failed to dispose.", exceptions);
		}
	}

	private static JsonRpcValue WriteJson(long handle, int direction, bool callScopedLifetime = false, IReadOnlyList<int>? optionalInterfaceIds = null)
	{
		using Sequence<byte> buffer = new();
		using Utf8JsonWriter writer = new(buffer);
		writer.WriteStartObject();
		writer.WriteNumber(Marker, direction);
		writer.WriteNumber(Handle, handle);
		if (callScopedLifetime)
		{
			writer.WriteString(Lifetime, "call");
		}

		if (optionalInterfaceIds is { Count: > 0 })
		{
			writer.WriteStartArray(OptionalInterfaces);
			foreach (int interfaceId in optionalInterfaceIds)
			{
				writer.WriteNumberValue(interfaceId);
			}

			writer.WriteEndArray();
		}

		writer.WriteEndObject();
		writer.Flush();
		return JsonRpcValue.FromOwnedBytes(buffer.AsReadOnlySequence.ToArray(), JsonRpcEncoding.Json);
	}

	private static JsonRpcValue WriteMessagePack(long handle, int direction, bool callScopedLifetime = false, IReadOnlyList<int>? optionalInterfaceIds = null)
	{
		using Sequence<byte> buffer = new();
		MessagePackWriter writer = new(buffer);
		writer.WriteMapHeader(2 + (callScopedLifetime ? 1 : 0) + (optionalInterfaceIds is { Count: > 0 } ? 1 : 0));
		writer.Write(Marker);
		writer.Write(direction);
		writer.Write(Handle);
		writer.Write(handle);
		if (callScopedLifetime)
		{
			writer.Write(Lifetime);
			writer.Write("call");
		}

		if (optionalInterfaceIds is { Count: > 0 })
		{
			writer.Write(OptionalInterfaces);
			writer.WriteArrayHeader(optionalInterfaceIds.Count);
			foreach (int interfaceId in optionalInterfaceIds)
			{
				writer.Write(interfaceId);
			}
		}

		writer.Flush();
		return JsonRpcValue.FromOwnedBytes(buffer.AsReadOnlySequence.ToArray(), JsonRpcEncoding.MessagePack);
	}

	private static (long Handle, int Direction, bool CallScopedLifetime, int[] OptionalInterfaceIds) ReadMarker(JsonRpcValue value)
	{
		if (value.Encoding == JsonRpcEncoding.Json)
		{
			using JsonDocument document = JsonDocument.Parse(value.OwnedBytes);
			JsonElement root = document.RootElement;
			long jsonHandle = root.GetProperty(Handle).GetInt64();
			int jsonDirection = root.GetProperty(Marker).GetInt32();
			string? jsonLifetime = "explicit";
			if (root.TryGetProperty(Lifetime, out JsonElement lifetimeElement))
			{
				if (lifetimeElement.ValueKind != JsonValueKind.String)
				{
					throw new FormatException("The marshaled object lifetime must be a string.");
				}

				jsonLifetime = lifetimeElement.GetString();
			}

			if (jsonDirection is not (0 or 1) || jsonLifetime is not ("call" or "explicit"))
			{
				throw new FormatException("The marshaled object marker contains an invalid direction or lifetime.");
			}

			int[] jsonOptionalInterfaceIds = root.TryGetProperty(OptionalInterfaces, out JsonElement optionalInterfacesElement)
				? optionalInterfacesElement.EnumerateArray().Select(static element => element.GetInt32()).ToArray()
				: [];
			return (jsonHandle, jsonDirection, jsonLifetime == "call", jsonOptionalInterfaceIds);
		}

		MessagePackReader reader = new(value.AsOwnedMessagePack());
		SerializationContext context = new();
		int count = reader.ReadMapHeader();
		long handle = 0;
		bool hasHandle = false;
		int direction = -1;
		string? lifetime = null;
		bool hasLifetime = false;
		int[] optionalInterfaceIds = [];
		for (int i = 0; i < count; i++)
		{
			string key = reader.ReadString() ?? throw new FormatException("Expected a marshaled object property name.");
			if (key == Handle)
			{
				handle = reader.ReadInt64();
				hasHandle = true;
			}
			else if (key == Marker)
			{
				direction = reader.ReadInt32();
			}
			else if (key == Lifetime)
			{
				lifetime = reader.ReadString();
				hasLifetime = true;
				if (lifetime is null)
				{
					throw new FormatException("The marshaled object lifetime must be a string.");
				}
			}
			else if (key == OptionalInterfaces)
			{
				int optionalInterfaceCount = reader.ReadArrayHeader();
				optionalInterfaceIds = new int[optionalInterfaceCount];
				for (int j = 0; j < optionalInterfaceCount; j++)
				{
					optionalInterfaceIds[j] = reader.ReadInt32();
				}
			}
			else
			{
				reader.Skip(context);
			}
		}

		if (!hasHandle || direction is not (0 or 1) || (hasLifetime && lifetime is not ("call" or "explicit")))
		{
			throw new FormatException("The marshaled object marker contains an invalid direction or lifetime.");
		}

		return (handle, direction, lifetime == "call", optionalInterfaceIds);
	}

	private static (long Handle, bool OwnedBySender) ReadReleaseArguments(JsonRpcValue value)
	{
		if (value.Encoding == JsonRpcEncoding.Json)
		{
			using JsonDocument document = JsonDocument.Parse(value.OwnedBytes);
			JsonElement root = document.RootElement;
			return root.ValueKind == JsonValueKind.Object
				? (root.GetProperty(Handle).GetInt64(), root.TryGetProperty("ownedBySender", out JsonElement ownedBySender) && ownedBySender.GetBoolean())
				: (root[0].GetInt64(), root.GetArrayLength() > 1 && root[1].GetBoolean());
		}

		MessagePackReader reader = new(value.AsOwnedMessagePack());
		SerializationContext context = new();
		if (reader.NextMessagePackType == MessagePackType.Map)
		{
			int propertyCount = reader.ReadMapHeader();
			long handle = 0;
			bool owned = false;
			for (int i = 0; i < propertyCount; i++)
			{
				if (reader.NextMessagePackType != MessagePackType.String)
				{
					reader.Skip(context);
					reader.Skip(context);
					continue;
				}

				string? key = reader.ReadString();
				if (key == Handle)
				{
					handle = reader.ReadInt64();
				}
				else if (key == "ownedBySender")
				{
					owned = reader.ReadBoolean();
				}
				else
				{
					reader.Skip(context);
				}
			}

			return (handle, owned);
		}

		int count = reader.ReadArrayHeader();
		long positionalHandle = reader.ReadInt64();
		bool positionalOwned = count > 1 && reader.ReadBoolean();
		for (int i = 2; i < count; i++)
		{
			reader.Skip(context);
		}

		return (positionalHandle, positionalOwned);
	}

	private static JsonRpcValue WriteMarker(long handle, int direction, bool callScopedLifetime, JsonRpcEncoding encoding, IReadOnlyList<int>? optionalInterfaceIds = null)
		=> encoding == JsonRpcEncoding.Json
			? WriteJson(handle, direction, callScopedLifetime, optionalInterfaceIds)
			: WriteMessagePack(handle, direction, callScopedLifetime, optionalInterfaceIds);

	private JsonRpcValue Marshal(object value, TargetRegistration? registration, bool callScopedLifetime, JsonRpcEncoding encoding, RpcCallState? callState, bool disposeTarget = true, IReadOnlyList<(int InterfaceId, TargetRegistration Registration)>? optionalRegistrations = null)
	{
		if (callScopedLifetime && callState?.IsDeclared(RpcCallState.Scopes.MarshaledObjects | RpcCallState.Scopes.CallScopedLifetimeAllowed) is not true)
		{
			throw new InvalidOperationException("Call-scoped marshalable objects may only be sent in RPC request arguments, not in return values.");
		}

		if (value is RemoteDisposable { Owner: var remoteOwner, Handle: long remoteHandle, CallScopedHandle: var remoteState })
		{
			if (!ReferenceEquals(remoteOwner, this))
			{
				throw new NotSupportedException("Marshaled proxies cannot be forwarded over a different JSON-RPC connection.");
			}

			remoteState.ThrowIfExpired();
			this.GetHandleScope(callState)?.MarkMarshaledObject();
			return WriteMarker(remoteHandle, direction: 0, callScopedLifetime: callScopedLifetime, encoding: encoding);
		}

		if (RemoteHandles.TryGetValue(value, out RemoteObjectHandle? remoteObject))
		{
			if (!ReferenceEquals(remoteObject.Manager, this))
			{
				throw new NotSupportedException("Marshaled proxies cannot be forwarded over a different JSON-RPC connection.");
			}

			remoteObject.CallScopedHandle.ThrowIfExpired();
			if (remoteObject.CallScopedLifetime != callScopedLifetime)
			{
				throw new InvalidOperationException("A marshaled proxy cannot be serialized under an interface with a different lifetime setting.");
			}

			this.GetHandleScope(callState)?.MarkMarshaledObject();
			return WriteMarker(remoteObject.Handle, direction: 0, callScopedLifetime: callScopedLifetime, encoding: encoding);
		}

		long handle = Interlocked.Increment(ref this.nextHandle);
		lock (this.sync)
		{
			if (!this.localLeases.TryGetValue(value, out LocalObjectLease? lease))
			{
				lease = new(value);
				this.localLeases.Add(value, lease);
			}

			if (!callScopedLifetime && disposeTarget)
			{
				lease.DisposeTarget = true;
			}

			MarshaledLocalObject marshaledObject = new(lease);
			if (registration is not null)
			{
				marshaledObject.AddRegistration(registration);
			}

			if (optionalRegistrations is not null)
			{
				foreach ((int interfaceId, TargetRegistration optionalRegistration) in optionalRegistrations)
				{
					marshaledObject.AddRegistration(optionalRegistration, interfaceId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
				}
			}

			lease.Handles.Add(handle);
			this.localObjects.Add(handle, marshaledObject);
		}

		this.GetHandleScope(callState)?.Add(handle, callScopedLifetime);

		return WriteMarker(handle, direction: 1, callScopedLifetime: callScopedLifetime, encoding: encoding, optionalRegistrations?.Select(static registration => registration.InterfaceId).ToArray());
	}

	/// <summary>Gets the state collecting objects marshaled into the message being written, creating it on first use.</summary>
	/// <param name="callState">The state of the call being serialized, if any.</param>
	/// <returns>The state, or <see langword="null"/> if the call does not track marshaled objects.</returns>
	private HandleScopeState? GetHandleScope(RpcCallState? callState)
		=> callState?.IsDeclared(RpcCallState.Scopes.MarshaledObjects) is true ? callState.MarshaledObjects ??= new(this) : null;

	private CallScopedHandle RegisterIncomingProxy(long remoteHandle, bool callScopedLifetime, RpcCallState? callState)
	{
		CallScopedHandle handle = new() { IsCallScoped = callScopedLifetime };
		lock (this.sync)
		{
			if (this.revokedRemoteHandles.Contains(remoteHandle))
			{
				handle.Invalidate("The owner revoked this marshaled proxy.");
			}
			else
			{
				if (!this.remoteProxies.TryGetValue(remoteHandle, out List<WeakReference<CallScopedHandle>>? proxies))
				{
					proxies = [];
					this.remoteProxies.Add(remoteHandle, proxies);
				}

				proxies.RemoveAll(static reference => !reference.TryGetTarget(out _));
				proxies.Add(new(handle));
			}
		}

		if (callState?.IsDeclared(RpcCallState.Scopes.InboundCall) is true)
		{
			if (!callState.HasResponse)
			{
				throw new FormatException("Marshaled objects cannot be received in notifications.");
			}

			(callState.InboundCall ??= new()).Add(handle, callScopedLifetime);
		}
		else if (callScopedLifetime)
		{
			throw new FormatException("Call-scoped marshaled objects may not be received as RPC return values.");
		}

		return handle;
	}

	private void InvalidateRemote(long handle)
	{
		List<WeakReference<CallScopedHandle>>? proxies;
		lock (this.sync)
		{
			this.revokedRemoteHandles.Add(handle);
			if (!this.remoteProxies.TryGetValue(handle, out proxies))
			{
				return;
			}

			this.remoteProxies.Remove(handle);
		}

		foreach (WeakReference<CallScopedHandle> reference in proxies)
		{
			if (reference.TryGetTarget(out CallScopedHandle? proxy))
			{
				proxy.Invalidate("The owner revoked this marshaled proxy.");
			}
		}
	}

	private void ReleaseLocal(long handle)
	{
		IDisposable? value = null;
		lock (this.sync)
		{
			if (this.localObjects.TryGetValue(handle, out MarshaledLocalObject? marshaledObject))
			{
				LocalObjectLease lease = marshaledObject.Lease;
				this.localObjects.Remove(handle);
				lease.Handles.Remove(handle);
				if (lease.Handles.Count == 0)
				{
					this.localLeases.Remove(lease.Value);
					value = lease.DisposeTarget ? lease.Value as IDisposable : null;
				}
			}
		}

		value?.Dispose();
	}

	/// <summary>Declares that marshaled objects may be received in one inbound message.</summary>
	/// <remarks>State is created only when a proxy is actually received.</remarks>
	internal readonly struct InboundCallScope(RpcCallState callState) : IDisposable
	{
		public void Dispose()
		{
			callState.InboundCall?.Dispose();
			callState.InboundCall = null;
			callState.Undeclare(RpcCallState.Scopes.InboundCall);
		}

		internal void Complete(bool succeeded) => callState.InboundCall?.Complete(succeeded);
	}

	/// <summary>Declares that objects may be marshaled into one outbound message.</summary>
	/// <remarks>State is created only when an object is actually marshaled.</remarks>
	internal readonly struct HandleScope(RpcCallState? callState) : IDisposable
	{
		internal bool HasMarshaledObjects => callState?.MarshaledObjects?.HasMarshaledObjects ?? false;

		public void Dispose()
		{
			if (callState is not null)
			{
				callState.MarshaledObjects?.Dispose();
				callState.MarshaledObjects = null;
				callState.Undeclare(RpcCallState.Scopes.MarshaledObjects | RpcCallState.Scopes.CallScopedLifetimeAllowed);
			}
		}

		internal HandleSet Commit() => callState?.MarshaledObjects?.Commit() ?? HandleSet.Empty;
	}

	internal sealed class HandleScopeState(MarshaledObjectManager manager)
	{
		private List<(long Handle, bool CallScopedLifetime)>? handles;
		private int marshaledObjectCount;
		private bool committed;

		internal bool HasMarshaledObjects => this.marshaledObjectCount > 0;

		internal void Dispose()
		{
			if (!this.committed && this.handles is { } handles)
			{
				foreach ((long handle, _) in handles)
				{
					manager.ReleaseLocal(handle);
				}
			}
		}

		internal void Add(long handle, bool callScopedLifetime)
		{
			(this.handles ??= []).Add((handle, callScopedLifetime));
			this.marshaledObjectCount++;
		}

		internal void MarkMarshaledObject() => this.marshaledObjectCount++;

		internal HandleSet Commit()
		{
			this.committed = true;
			if (this.handles is { Count: > 0 } handles)
			{
				return new(manager, [.. handles], this.HasMarshaledObjects);
			}

			return this.HasMarshaledObjects ? new(manager, [], hasMarshaledObjects: true) : HandleSet.Empty;
		}
	}

	internal sealed class HandleSet(MarshaledObjectManager? manager, (long Handle, bool CallScopedLifetime)[] handles, bool hasMarshaledObjects)
	{
		internal static readonly HandleSet Empty = new(null, [], false);

		private const int CallScopedReleased = 1;
		private const int AllReleased = 2;
		private int releaseState;

		internal bool HasMarshaledObjects => hasMarshaledObjects;

		/// <summary>Gets a value indicating whether this value owns any call-scoped handles.</summary>
		internal bool HasCallScopedObjects => handles.Any(static handle => handle.CallScopedLifetime);

		internal void ReleaseCallScoped()
		{
			while (true)
			{
				int state = Volatile.Read(ref this.releaseState);
				if ((state & (CallScopedReleased | AllReleased)) != 0)
				{
					return;
				}

				if (Interlocked.CompareExchange(ref this.releaseState, state | CallScopedReleased, state) == state)
				{
					foreach ((long handle, bool callScopedLifetime) in handles)
					{
						if (callScopedLifetime)
						{
							manager!.ReleaseLocal(handle);
						}
					}

					return;
				}
			}
		}

		internal void ReleaseAll()
		{
			while (true)
			{
				int state = Volatile.Read(ref this.releaseState);
				if ((state & AllReleased) != 0)
				{
					return;
				}

				if (Interlocked.CompareExchange(ref this.releaseState, state | AllReleased | CallScopedReleased, state) == state)
				{
					foreach ((long handle, bool callScopedLifetime) in handles)
					{
						if ((state & CallScopedReleased) == 0 || !callScopedLifetime)
						{
							manager!.ReleaseLocal(handle);
						}
					}

					return;
				}
			}
		}
	}

	internal sealed class InboundCallScopeState
	{
		private List<(CallScopedHandle Handle, bool CallScopedLifetime)>? proxies;
		private bool succeeded;

		/// <summary>Gets the shared lifetime of the call-scoped proxies.</summary>
		internal CallScopedLifetime? Lifetime { get; private set; }

		internal void Dispose()
		{
			if (!this.succeeded && this.proxies is { } proxies)
			{
				foreach ((CallScopedHandle handle, _) in proxies)
				{
					handle.Invalidate();
				}
			}

			this.Lifetime?.Dispose();
		}

		internal void Add(CallScopedHandle proxy, bool callScopedLifetime)
		{
			(this.proxies ??= []).Add((proxy, callScopedLifetime));
			if (callScopedLifetime)
			{
				this.Lifetime ??= new(() =>
				{
					foreach ((CallScopedHandle handle, bool scoped) in this.proxies)
					{
						if (scoped)
						{
							handle.Invalidate();
						}
					}
				});
			}
		}

		internal void Complete(bool succeeded) => this.succeeded = succeeded;
	}

	internal sealed class CallScopedHandle
	{
		private int active = 1;
		private string expirationMessage = "The RPC call that supplied this proxy has completed.";

		internal bool IsActive => Volatile.Read(ref this.active) != 0;

		internal bool IsCallScoped { get; set; }

		internal void ThrowIfExpired()
		{
			if (!this.IsActive)
			{
				throw new ObjectDisposedException("marshaled proxy", this.expirationMessage);
			}
		}

		internal void Invalidate(string? message = null)
		{
			if (message is not null)
			{
				this.expirationMessage = message;
			}

			Interlocked.Exchange(ref this.active, 0);
		}
	}

	/// <summary>
	/// Caches the RPC-marshaling attributes declared on an interface, so they are read once per interface.
	/// </summary>
	/// <typeparam name="T">The RPC-marshalable interface.</typeparam>
	private static class MarshalableCache<T>
	{
		/// <summary>The <see cref="RpcMarshalableAttribute"/> on <typeparamref name="T"/>, if any.</summary>
		internal static readonly RpcMarshalableAttribute? Marshalable = typeof(T).GetCustomAttribute<RpcMarshalableAttribute>();

		/// <summary>The optional interfaces declared on <typeparamref name="T"/>.</summary>
		internal static readonly RpcMarshalableOptionalInterfaceAttribute[] OptionalInterfaces = [.. typeof(T).GetCustomAttributes<RpcMarshalableOptionalInterfaceAttribute>()];

		/// <summary>The generated factories for proxies of <typeparamref name="T"/> that also implement optional interfaces.</summary>
		internal static readonly JsonRpcOptionalProxyFactoryAttribute[] OptionalProxyFactories = [.. typeof(T).GetCustomAttributes<JsonRpcOptionalProxyFactoryAttribute>()];

#pragma warning disable CS0618 // Support proxy metadata emitted by previous versions of the source generator.
		/// <summary>Legacy proxy metadata for optional-interface variants.</summary>
		internal static readonly JsonRpcOptionalProxyImplementationAttribute[] LegacyOptionalProxyImplementations = [.. typeof(T).GetCustomAttributes<JsonRpcOptionalProxyImplementationAttribute>()];
#pragma warning restore CS0618
	}

	private sealed class LocalObjectLease(object value)
	{
		internal object Value => value;

		internal HashSet<long> Handles { get; } = [];

		internal bool DisposeTarget { get; set; }
	}

	private sealed class MarshaledLocalObject(LocalObjectLease lease)
	{
		internal LocalObjectLease Lease => lease;

		internal Dictionary<string, MethodInvoker> MethodInvokers { get; } = new(StringComparer.Ordinal);

		internal void AddRegistration(TargetRegistration registration, string prefix = "")
		{
			foreach ((string name, MethodInvoker invoker) in registration.MethodInvokers)
			{
				if (!this.MethodInvokers.TryAdd(prefix + name, invoker))
				{
					throw new InvalidOperationException($"Multiple marshalable methods map to '{name}'.");
				}
			}
		}
	}

	private sealed record RemoteObjectHandle(MarshaledObjectManager Manager, long Handle, bool CallScopedLifetime, CallScopedHandle CallScopedHandle);

	private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
		where T : class
	{
		internal static readonly ReferenceEqualityComparer<T> Instance = new();

		private ReferenceEqualityComparer()
		{
		}

		public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

		public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}

	private sealed class RemoteDisposable(MarshaledObjectManager manager, long handle, CallScopedHandle callScopedHandle) : IDisposable
	{
		private int disposed;

		internal MarshaledObjectManager Owner => manager;

		internal long Handle => handle;

		internal CallScopedHandle CallScopedHandle => callScopedHandle;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref this.disposed, 1) == 0 && callScopedHandle.IsActive)
			{
				manager.Release(handle);
			}
		}
	}
}
