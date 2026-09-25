// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
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
	private readonly object sync = new();
	private readonly Dictionary<long, LocalObjectLease> localObjects = [];
	private readonly Dictionary<IDisposable, long> localHandles = new(ReferenceEqualityComparer<IDisposable>.Instance);
	private readonly AsyncLocal<HandleScope?> activeScope = new();
	private long nextHandle;

	internal JsonRpcValue Marshal(IDisposable value, JsonRpcEncoding encoding)
	{
		if (value is null)
		{
			throw new ArgumentNullException(nameof(value));
		}

		if (value is RemoteDisposable { Owner: var remoteOwner, Handle: long remoteHandle } && ReferenceEquals(remoteOwner, this))
		{
			return encoding == JsonRpcEncoding.Json ? WriteJson(remoteHandle, direction: 0) : WriteMessagePack(remoteHandle, direction: 0);
		}

		long handle;
		lock (this.sync)
		{
			if (this.localHandles.TryGetValue(value, out handle))
			{
				this.localObjects[handle].AddRef();
			}
			else
			{
				handle = Interlocked.Increment(ref this.nextHandle);
				this.localObjects.Add(handle, new(value));
				this.localHandles.Add(value, handle);
			}
		}

		this.activeScope.Value?.Add(handle);
		return encoding == JsonRpcEncoding.Json ? WriteJson(handle, direction: 1) : WriteMessagePack(handle, direction: 1);
	}

	internal IDisposable Unmarshal(JsonRpcValue value)
	{
		(long handle, int direction) = ReadMarker(value);
		if (direction == 0)
		{
			lock (this.sync)
			{
				if (this.localObjects.TryGetValue(handle, out LocalObjectLease? local))
				{
					return local.Value;
				}
			}

			throw new InvalidOperationException($"Marshaled object handle {handle} is not available.");
		}

		return new RemoteDisposable(this, handle);
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

		const string prefix = "$/invokeProxy/";
		if (request.Method.StartsWith(prefix, StringComparison.Ordinal))
		{
			string[] parts = request.Method.Split('/');
			if (parts.Length == 4 && parts[3] == "Dispose" && long.TryParse(parts[2], out long handle))
			{
				this.ReleaseLocal(handle);
				return true;
			}
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
			values = [.. this.localObjects.Values.Select(static lease => lease.Value)];
			this.localObjects.Clear();
			this.localHandles.Clear();
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

	private void ReleaseLocal(long handle)
	{
		IDisposable? value = null;
		lock (this.sync)
		{
			if (this.localObjects.TryGetValue(handle, out LocalObjectLease? lease) && lease.Release())
			{
				this.localObjects.Remove(handle);
				this.localHandles.Remove(lease.Value);
				value = lease.Value;
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
		private int count = 1;

		internal IDisposable Value => value;

		internal void AddRef() => this.count++;

		internal bool Release() => --this.count == 0;
	}

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
