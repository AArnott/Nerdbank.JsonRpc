// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
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
	private readonly Dictionary<IDisposable, LocalObjectLease> localLeases = new(ReferenceEqualityComparer<IDisposable>.Instance);
	private readonly AsyncLocal<HandleScope?> activeScope = new();
	private long nextHandle;

	internal JsonRpcValue Marshal(IDisposable value, JsonRpcEncoding encoding)
	{
		if (value is null)
		{
			throw new ArgumentNullException(nameof(value));
		}

		return this.Marshal(value, typeof(IDisposable), registration: null, encoding);
	}

	internal JsonRpcValue MarshalMarshalable<T>(T value, ITypeShape<T> shape, JsonRpcEncoding encoding)
	{
		if (value is not IDisposable disposable)
		{
			throw new InvalidOperationException($"Values marshaled as '{shape.Type}' must implement IDisposable.");
		}

		TargetRegistration registration = (TargetRegistration)shape.Accept(RpcTargetVisitor.Instance, new JsonRpcTargetOptions())!;
		return this.Marshal(disposable, shape.Type, registration, encoding);
	}

	internal IDisposable Unmarshal(JsonRpcValue value)
	{
		(long handle, int direction) = ReadMarker(value);
		if (direction == 0)
		{
			lock (this.sync)
			{
				if (this.localObjects.TryGetValue(handle, out MarshaledLocalObject? local))
				{
					return local.Lease.Value;
				}
			}

			throw new InvalidOperationException($"Marshaled object handle {handle} is not available.");
		}

		return new RemoteDisposable(this, handle);
	}

	internal T UnmarshalMarshalable<T>(JsonRpcValue value, ITypeShape<T> shape)
	{
		(long handle, int direction) = ReadMarker(value);
		if (direction == 0)
		{
			lock (this.sync)
			{
				if (this.localObjects.TryGetValue(handle, out MarshaledLocalObject? local))
				{
					return (T)(object)local.Lease.Value;
				}
			}

			throw new InvalidOperationException($"Marshaled object handle {handle} is not available.");
		}

		object proxy = JsonRpc.AttachCore(new MarshaledObjectProxyClient(owner, handle), typeof(T));
		RemoteHandles.Add(proxy, new(this, handle));
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

	internal HandleScope TrackMarshaledObjects() => new(this);

	internal void ReleaseLocalObjects(JsonRpcValue value) => value.MarshaledHandles?.ReleaseAll();

	internal void DisposeAll()
	{
		IDisposable[] values;
		lock (this.sync)
		{
			values = [.. this.localLeases.Keys];
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

	private static JsonRpcValue WriteJson(long handle, int direction)
	{
		using Sequence<byte> buffer = new();
		using Utf8JsonWriter writer = new(buffer);
		writer.WriteStartObject();
		writer.WriteNumber(Marker, direction);
		writer.WriteNumber(Handle, handle);
		writer.WriteString(Lifetime, "explicit");
		writer.WriteEndObject();
		writer.Flush();
		return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
	}

	private static JsonRpcValue WriteMessagePack(long handle, int direction)
	{
		using Sequence<byte> buffer = new();
		MessagePackWriter writer = new(buffer);
		writer.WriteMapHeader(3);
		writer.Write(Marker);
		writer.Write(direction);
		writer.Write(Handle);
		writer.Write(handle);
		writer.Write(Lifetime);
		writer.Write("explicit");
		writer.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)buffer.AsReadOnlySequence.ToArray());
	}

	private static (long Handle, int Direction) ReadMarker(JsonRpcValue value)
	{
		if (value.Encoding == JsonRpcEncoding.Json)
		{
			using JsonDocument document = JsonDocument.Parse(value.OwnedBytes);
			JsonElement root = document.RootElement;
			return (root.GetProperty(Handle).GetInt64(), root.GetProperty(Marker).GetInt32());
		}

		MessagePackReader reader = new(value.AsMessagePack());
		SerializationContext context = new();
		int count = reader.ReadMapHeader();
		long handle = 0;
		int direction = -1;
		for (int i = 0; i < count; i++)
		{
			string key = reader.ReadString() ?? throw new FormatException("Expected a marshaled object property name.");
			if (key == Handle)
			{
				handle = reader.ReadInt64();
			}
			else if (key == Marker)
			{
				direction = reader.ReadInt32();
			}
			else
			{
				reader.Skip(context);
			}
		}

		return direction < 0 ? throw new FormatException("The marshaled object marker is incomplete.") : (handle, direction);
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

	private JsonRpcValue Marshal(IDisposable value, Type contractType, TargetRegistration? registration, JsonRpcEncoding encoding)
	{
		if (value is RemoteDisposable { Owner: var remoteOwner, Handle: long remoteHandle } && ReferenceEquals(remoteOwner, this))
		{
			return encoding == JsonRpcEncoding.Json ? WriteJson(remoteHandle, direction: 0) : WriteMessagePack(remoteHandle, direction: 0);
		}

		if (RemoteHandles.TryGetValue(value, out RemoteObjectHandle? remoteObject) && ReferenceEquals(remoteObject.Manager, this))
		{
			return encoding == JsonRpcEncoding.Json ? WriteJson(remoteObject.Handle, direction: 0) : WriteMessagePack(remoteObject.Handle, direction: 0);
		}

		long handle;
		lock (this.sync)
		{
			if (!this.localLeases.TryGetValue(value, out LocalObjectLease? lease))
			{
				lease = new(value);
				this.localLeases.Add(value, lease);
			}

			if (lease.Handles.TryGetValue(contractType, out handle))
			{
				this.localObjects[handle].AddRef();
			}
			else
			{
				handle = Interlocked.Increment(ref this.nextHandle);
				MarshaledLocalObject marshaledObject = new(lease, contractType);
				if (registration is not null)
				{
					marshaledObject.AddRegistration(registration);
				}

				lease.Handles.Add(contractType, handle);
				this.localObjects.Add(handle, marshaledObject);
			}
		}

		this.activeScope.Value?.Add(handle);
		return encoding == JsonRpcEncoding.Json ? WriteJson(handle, direction: 1) : WriteMessagePack(handle, direction: 1);
	}

	private void ReleaseLocal(long handle)
	{
		IDisposable? value = null;
		lock (this.sync)
		{
			if (this.localObjects.TryGetValue(handle, out MarshaledLocalObject? marshaledObject) && marshaledObject.Release())
			{
				LocalObjectLease lease = marshaledObject.Lease;
				this.localObjects.Remove(handle);
				lease.Handles.Remove(marshaledObject.ContractType!);
				if (lease.Handles.Count == 0)
				{
					this.localLeases.Remove(lease.Value);
					value = lease.Value;
				}
			}
		}

		value?.Dispose();
	}

	internal sealed class HandleScope : IDisposable
	{
		private readonly MarshaledObjectManager manager;
		private readonly HandleScope? priorScope;
		private readonly List<long> handles = [];
		private bool committed;

		internal HandleScope(MarshaledObjectManager manager)
		{
			this.manager = manager;
			this.priorScope = manager.activeScope.Value;
			manager.activeScope.Value = this;
		}

		public void Dispose()
		{
			this.manager.activeScope.Value = this.priorScope;
			if (!this.committed)
			{
				foreach (long handle in this.handles)
				{
					this.manager.ReleaseLocal(handle);
				}
			}
		}

		internal void Add(long handle) => this.handles.Add(handle);

		internal HandleSet Commit()
		{
			this.committed = true;
			return new(this.manager, [.. this.handles]);
		}
	}

	internal sealed class HandleSet(MarshaledObjectManager manager, long[] handles)
	{
		private int released;

		internal void ReleaseAll()
		{
			if (Interlocked.Exchange(ref this.released, 1) == 0)
			{
				foreach (long handle in handles)
				{
					manager.ReleaseLocal(handle);
				}
			}
		}
	}

	private sealed class LocalObjectLease(IDisposable value)
	{
		internal IDisposable Value => value;

		internal Dictionary<Type, long> Handles { get; } = [];
	}

	private sealed class MarshaledLocalObject(LocalObjectLease lease, Type contractType)
	{
		private int referenceCount = 1;

		internal LocalObjectLease Lease => lease;

		internal Type ContractType => contractType;

		internal Dictionary<string, MethodInvoker> MethodInvokers { get; } = new(StringComparer.Ordinal);

		internal void AddRef() => this.referenceCount++;

		internal bool Release() => --this.referenceCount == 0;

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

	private sealed record RemoteObjectHandle(MarshaledObjectManager Manager, long Handle);

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

	private sealed class RemoteDisposable(MarshaledObjectManager manager, long handle) : IDisposable
	{
		private int disposed;

		internal MarshaledObjectManager Owner => manager;

		internal long Handle => handle;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref this.disposed, 1) == 0)
			{
				manager.Release(handle);
			}
		}
	}
}
