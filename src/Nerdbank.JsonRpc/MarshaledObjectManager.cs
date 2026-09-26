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
	private const string ReleaseMethod = "$/releaseMarshaledObject";
	private static readonly ConditionalWeakTable<object, RemoteObjectHandle> RemoteHandles = new();
	private readonly object sync = new();
	private readonly Dictionary<long, MarshaledLocalObject> localObjects = [];
	private readonly Dictionary<object, LocalObjectLease> localLeases = new(ReferenceEqualityComparer<object>.Instance);
	private readonly AsyncLocal<HandleScope?> activeScope = new();
	private readonly AsyncLocal<InboundCallScope?> activeInboundCall = new();
	private long nextHandle;

	internal JsonRpcValue Marshal(IDisposable value, JsonRpcEncoding encoding)
	{
		if (value is null)
		{
			throw new ArgumentNullException(nameof(value));
		}

		return this.Marshal(value, registration: null, callScopedLifetime: false, encoding: encoding);
	}

	internal JsonRpcValue MarshalMarshalable<T>(T value, ITypeShape<T> shape, JsonRpcEncoding encoding)
	{
		if (value is null)
		{
			throw new ArgumentNullException(nameof(value));
		}

		RpcMarshalableAttribute attribute = shape.Type.GetCustomAttribute<RpcMarshalableAttribute>()
			?? throw new InvalidOperationException($"The interface '{shape.Type}' is not marked with {nameof(RpcMarshalableAttribute)}.");
		object target = value;
		if (!attribute.CallScopedLifetime && target is not IDisposable)
		{
			throw new InvalidOperationException($"Explicit-lifetime marshalable values of '{shape.Type}' must implement IDisposable.");
		}

		TargetRegistration registration = (TargetRegistration)shape.Accept(RpcTargetVisitor.Instance, new JsonRpcTargetOptions())!;
		return this.Marshal(target, registration, attribute.CallScopedLifetime, encoding);
	}

	internal IDisposable Unmarshal(JsonRpcValue value)
	{
		(long handle, int direction, bool callScopedLifetime) = ReadMarker(value);
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

		if (this.activeInboundCall.Value is { HasResponse: false })
		{
			throw new FormatException("Marshaled objects cannot be received in notifications.");
		}

		return new RemoteDisposable(this, handle, this.RegisterIncomingProxy(callScopedLifetime: false));
	}

	internal T UnmarshalMarshalable<T>(JsonRpcValue value, ITypeShape<T> shape)
	{
		(long handle, int direction, bool callScopedLifetime) = ReadMarker(value);
		RpcMarshalableAttribute attribute = shape.Type.GetCustomAttribute<RpcMarshalableAttribute>()
			?? throw new InvalidOperationException($"The interface '{shape.Type}' is not marked with {nameof(RpcMarshalableAttribute)}.");
		if (attribute.CallScopedLifetime != callScopedLifetime)
		{
			throw new FormatException($"The marshaled lifetime does not match the {nameof(RpcMarshalableAttribute)} on '{shape.Type}'.");
		}

		if (this.activeInboundCall.Value is { HasResponse: false })
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

		CallScopedHandle callScopedHandle = this.RegisterIncomingProxy(callScopedLifetime);
		object proxy = JsonRpc.AttachCore(new MarshaledObjectProxyClient(owner, handle, callScopedHandle), typeof(T));
		RemoteHandles.Add(proxy, new(this, handle, callScopedLifetime, callScopedHandle));
		return (T)proxy;
	}

	internal bool TryGetMethodInvoker(JsonRpcRequest request, out object? target, out MethodInvoker invoker)
	{
		target = null;
		invoker = null!;
		const string prefix = "$/invokeProxy/";
		if (!request.Method.StartsWith(prefix, StringComparison.Ordinal))
		{
			return false;
		}

		int separator = request.Method.IndexOf('/', prefix.Length);
		if (separator < 0 || !long.TryParse(request.Method.Substring(prefix.Length, separator - prefix.Length), out long handle))
		{
			return false;
		}

		string method = request.Method[(separator + 1)..];
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

	internal bool TryHandleNotification(JsonRpcRequest request)
	{
		if (request.Method == ReleaseMethod)
		{
			(long handle, bool ownedBySender) = ReadReleaseArguments(request.Arguments);
			if (!ownedBySender)
			{
				this.ReleaseLocal(handle);
			}

			return true;
		}

		return false;
	}

	internal void Release(long handle) => owner.PostMarshaledNotification(ReleaseMethod, owner.MarshalReleaseArguments(handle));

	internal HandleScope TrackMarshaledObjects(bool allowCallScopedLifetime = true) => new(this, allowCallScopedLifetime);

	internal InboundCallScope TrackInboundCall(bool hasResponse) => new(this, hasResponse);

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
		lock (this.sync)
		{
			values = [.. this.localLeases.Values.Where(static lease => lease.DisposeTarget).Select(static lease => lease.Value).OfType<IDisposable>()];
			this.localObjects.Clear();
			this.localLeases.Clear();
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

	private static JsonRpcValue WriteJson(long handle, int direction, bool callScopedLifetime = false)
	{
		using Sequence<byte> buffer = new();
		using Utf8JsonWriter writer = new(buffer);
		writer.WriteStartObject();
		writer.WriteNumber(Marker, direction);
		writer.WriteNumber(Handle, handle);
		writer.WriteString(Lifetime, callScopedLifetime ? "call" : "explicit");
		writer.WriteEndObject();
		writer.Flush();
		return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
	}

	private static JsonRpcValue WriteMessagePack(long handle, int direction, bool callScopedLifetime = false)
	{
		using Sequence<byte> buffer = new();
		MessagePackWriter writer = new(buffer);
		writer.WriteMapHeader(3);
		writer.Write(Marker);
		writer.Write(direction);
		writer.Write(Handle);
		writer.Write(handle);
		writer.Write(Lifetime);
		writer.Write(callScopedLifetime ? "call" : "explicit");
		writer.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)buffer.AsReadOnlySequence.ToArray());
	}

	private static (long Handle, int Direction, bool CallScopedLifetime) ReadMarker(JsonRpcValue value)
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

			return (jsonHandle, jsonDirection, jsonLifetime == "call");
		}

		MessagePackReader reader = new(value.AsMessagePack());
		SerializationContext context = new();
		int count = reader.ReadMapHeader();
		long handle = 0;
		bool hasHandle = false;
		int direction = -1;
		string? lifetime = null;
		bool hasLifetime = false;
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
			else
			{
				reader.Skip(context);
			}
		}

		if (!hasHandle || direction is not (0 or 1) || (hasLifetime && lifetime is not ("call" or "explicit")))
		{
			throw new FormatException("The marshaled object marker contains an invalid direction or lifetime.");
		}

		return (handle, direction, lifetime == "call");
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

		MessagePackReader reader = new(value.AsMessagePack());
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

	private static JsonRpcValue WriteMarker(long handle, int direction, bool callScopedLifetime, JsonRpcEncoding encoding)
		=> encoding == JsonRpcEncoding.Json
			? WriteJson(handle, direction, callScopedLifetime)
			: WriteMessagePack(handle, direction, callScopedLifetime);

	private JsonRpcValue Marshal(object value, TargetRegistration? registration, bool callScopedLifetime, JsonRpcEncoding encoding)
	{
		if (callScopedLifetime && this.activeScope.Value is not { AllowCallScopedLifetime: true })
		{
			throw new InvalidOperationException("Call-scoped marshalable objects may only be sent in RPC request arguments, not in return values.");
		}

		if (value is RemoteDisposable { Owner: var remoteOwner, Handle: long remoteHandle } && ReferenceEquals(remoteOwner, this))
		{
			this.activeScope.Value?.MarkMarshaledObject();
			return WriteMarker(remoteHandle, direction: 0, callScopedLifetime: callScopedLifetime, encoding: encoding);
		}

		if (RemoteHandles.TryGetValue(value, out RemoteObjectHandle? remoteObject) && ReferenceEquals(remoteObject.Manager, this))
		{
			remoteObject.CallScopedHandle.ThrowIfExpired();
			if (remoteObject.CallScopedLifetime != callScopedLifetime)
			{
				throw new InvalidOperationException("A marshaled proxy cannot be serialized under an interface with a different lifetime setting.");
			}

			this.activeScope.Value?.MarkMarshaledObject();
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

			if (!callScopedLifetime)
			{
				lease.DisposeTarget = true;
			}

			MarshaledLocalObject marshaledObject = new(lease);
			if (registration is not null)
			{
				marshaledObject.AddRegistration(registration);
			}

			lease.Handles.Add(handle);
			this.localObjects.Add(handle, marshaledObject);
		}

		this.activeScope.Value?.Add(handle, callScopedLifetime);

		return WriteMarker(handle, direction: 1, callScopedLifetime: callScopedLifetime, encoding: encoding);
	}

	private CallScopedHandle RegisterIncomingProxy(bool callScopedLifetime)
	{
		CallScopedHandle handle = new() { IsCallScoped = callScopedLifetime };
		if (this.activeInboundCall.Value is InboundCallScope scope)
		{
			if (!scope.HasResponse)
			{
				throw new FormatException("Marshaled objects cannot be received in notifications.");
			}

			scope.Add(handle, callScopedLifetime);
		}
		else if (callScopedLifetime)
		{
			throw new FormatException("Call-scoped marshaled objects may not be received as RPC return values.");
		}

		return handle;
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

	internal sealed class HandleScope : IDisposable
	{
		private readonly MarshaledObjectManager manager;
		private readonly HandleScope? priorScope;
		private readonly List<(long Handle, bool CallScopedLifetime)> handles = [];
		private int marshaledObjectCount;
		private bool committed;

		internal HandleScope(MarshaledObjectManager manager, bool allowCallScopedLifetime)
		{
			this.manager = manager;
			this.AllowCallScopedLifetime = allowCallScopedLifetime;
			this.priorScope = manager.activeScope.Value;
			manager.activeScope.Value = this;
		}

		internal bool AllowCallScopedLifetime { get; }

		internal bool HasMarshaledObjects => this.marshaledObjectCount > 0;

		public void Dispose()
		{
			this.manager.activeScope.Value = this.priorScope;
			if (!this.committed)
			{
				foreach ((long handle, _) in this.handles)
				{
					this.manager.ReleaseLocal(handle);
				}
			}
		}

		internal void Add(long handle, bool callScopedLifetime)
		{
			this.handles.Add((handle, callScopedLifetime));
			this.marshaledObjectCount++;
		}

		internal void MarkMarshaledObject() => this.marshaledObjectCount++;

		internal HandleSet Commit()
		{
			this.committed = true;
			return new(this.manager, [.. this.handles], this.HasMarshaledObjects);
		}
	}

	internal sealed class HandleSet(MarshaledObjectManager manager, (long Handle, bool CallScopedLifetime)[] handles, bool hasMarshaledObjects)
	{
		private const int CallScopedReleased = 1;
		private const int AllReleased = 2;
		private int releaseState;

		internal bool HasMarshaledObjects => hasMarshaledObjects;

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
							manager.ReleaseLocal(handle);
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
							manager.ReleaseLocal(handle);
						}
					}

					return;
				}
			}
		}
	}

	internal sealed class InboundCallScope : IDisposable
	{
		private readonly MarshaledObjectManager manager;
		private readonly InboundCallScope? priorScope;
		private readonly List<(CallScopedHandle Handle, bool CallScopedLifetime)> proxies = [];
		private bool succeeded;

		internal InboundCallScope(MarshaledObjectManager manager, bool hasResponse)
		{
			this.manager = manager;
			this.HasResponse = hasResponse;
			this.priorScope = manager.activeInboundCall.Value;
			manager.activeInboundCall.Value = this;
		}

		internal bool HasResponse { get; }

		public void Dispose()
		{
			this.manager.activeInboundCall.Value = this.priorScope;
			foreach ((CallScopedHandle handle, bool callScopedLifetime) in this.proxies)
			{
				if (callScopedLifetime || !this.succeeded)
				{
					handle.Invalidate();
				}
			}
		}

		internal void Add(CallScopedHandle proxy, bool callScopedLifetime) => this.proxies.Add((proxy, callScopedLifetime));

		internal void Complete(bool succeeded) => this.succeeded = succeeded;
	}

	internal sealed class CallScopedHandle
	{
		private int active = 1;

		internal bool IsCallScoped { get; set; }

		internal void ThrowIfExpired()
		{
			if (Volatile.Read(ref this.active) == 0)
			{
				throw new ObjectDisposedException("call-scoped marshaled proxy", "The RPC call that supplied this proxy has completed.");
			}
		}

		internal void Invalidate() => Interlocked.Exchange(ref this.active, 0);
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

		internal void AddRegistration(TargetRegistration registration)
		{
			foreach ((string name, MethodInvoker invoker) in registration.MethodInvokers)
			{
				if (!this.MethodInvokers.TryAdd(name, invoker))
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

		public void Dispose()
		{
			callScopedHandle.ThrowIfExpired();
			if (Interlocked.Exchange(ref this.disposed, 1) == 0)
			{
				manager.Release(handle);
			}
		}
	}
}
