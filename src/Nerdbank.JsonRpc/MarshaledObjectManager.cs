// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Text.Json;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

internal sealed class MarshaledObjectManager(JsonRpc owner)
{
	private const string Marker = "__jsonrpc_marshaled";
	private const string Handle = "handle";
	private const string Lifetime = "lifetime";
	private const string ReleaseMethod = "$/releaseMarshaledObject";
	private readonly object sync = new();
	private readonly Dictionary<long, IDisposable> localObjects = [];
	private long nextHandle;

	internal JsonRpcValue Marshal(IDisposable value, JsonRpcEncoding encoding)
	{
		if (value is null) throw new ArgumentNullException(nameof(value));
		long handle = Interlocked.Increment(ref this.nextHandle);
		lock (this.sync) this.localObjects.Add(handle, value);
		return encoding == JsonRpcEncoding.Json ? WriteJson(handle) : WriteMessagePack(handle);
	}

	internal IDisposable Unmarshal(JsonRpcValue value)
	{
		(long handle, int direction) = ReadMarker(value);
		if (direction == 0)
		{
			lock (this.sync)
			{
				if (this.localObjects.TryGetValue(handle, out IDisposable? local)) return local;
			}
			throw new InvalidOperationException($"Marshaled object handle {handle} is not available.");
		}
		return new RemoteDisposable(this, handle);
	}

	internal bool TryHandleNotification(JsonRpcRequest request)
	{
		if (request.Method == ReleaseMethod)
		{
			(long handle, _) = ReadReleaseArguments(request.Arguments);
			ReleaseLocal(handle);
			return true;
		}
		const string prefix = "$/invokeProxy/";
		if (request.Method.StartsWith(prefix, StringComparison.Ordinal))
		{
			string[] parts = request.Method.Split('/');
			if (parts.Length == 4 && parts[3] == "Dispose" && long.TryParse(parts[2], out long handle))
			{
				ReleaseLocal(handle);
				return true;
			}
		}
		return false;
	}

	internal void Release(long handle) => owner.PostMarshaledNotification(ReleaseMethod, owner.MarshalReleaseArguments(handle));

	internal void DisposeAll()
	{
		IDisposable[] values;
		lock (this.sync) { values = [.. this.localObjects.Values]; this.localObjects.Clear(); }
		foreach (IDisposable value in values) value.Dispose();
	}

	private void ReleaseLocal(long handle)
	{
		IDisposable? value = null;
		lock (this.sync) { if (this.localObjects.TryGetValue(handle, out IDisposable? removed)) { this.localObjects.Remove(handle); value = removed; } }
		value?.Dispose();
	}

	private static JsonRpcValue WriteJson(long handle)
	{
		using Sequence<byte> buffer = new();
		using Utf8JsonWriter writer = new(buffer);
		writer.WriteStartObject(); writer.WriteNumber(Marker, 1); writer.WriteNumber(Handle, handle); writer.WriteString(Lifetime, "explicit"); writer.WriteEndObject(); writer.Flush();
		return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
	}

	private static JsonRpcValue WriteMessagePack(long handle)
	{
		using Sequence<byte> buffer = new(); MessagePackWriter writer = new(buffer); writer.WriteMapHeader(3);
		writer.Write(Marker); writer.Write(1); writer.Write(Handle); writer.Write(handle); writer.Write(Lifetime); writer.Write("explicit"); writer.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)buffer.AsReadOnlySequence.ToArray());
	}

	private static (long Handle, int Direction) ReadMarker(JsonRpcValue value)
	{
		if (value.Encoding == JsonRpcEncoding.Json)
		{
			using JsonDocument document = JsonDocument.Parse(value.OwnedBytes); JsonElement root = document.RootElement;
			return (root.GetProperty(Handle).GetInt64(), root.GetProperty(Marker).GetInt32());
		}
		MessagePackReader reader = new(value.AsMessagePack()); SerializationContext context = new(); int count = reader.ReadMapHeader(); long handle = 0; int direction = -1;
		for (int i = 0; i < count; i++) { string key = reader.ReadString() ?? throw new FormatException("Expected a marshaled object property name."); if (key == Handle) handle = reader.ReadInt64(); else if (key == Marker) direction = reader.ReadInt32(); else reader.Skip(context); }
		return direction < 0 ? throw new FormatException("The marshaled object marker is incomplete.") : (handle, direction);
	}

	private static (long Handle, bool OwnedBySender) ReadReleaseArguments(JsonRpcValue value)
	{
		if (value.Encoding == JsonRpcEncoding.Json) { using JsonDocument document = JsonDocument.Parse(value.OwnedBytes); JsonElement root = document.RootElement; return (root[0].GetInt64(), root.GetArrayLength() > 1 && root[1].GetBoolean()); }
		MessagePackReader reader = new(value.AsMessagePack()); SerializationContext context = new(); int count = reader.ReadArrayHeader(); long handle = reader.ReadInt64(); bool owned = count > 1 && reader.ReadBoolean(); for (int i = 2; i < count; i++) reader.Skip(context); return (handle, owned);
	}

	private sealed class RemoteDisposable(MarshaledObjectManager manager, long handle) : IDisposable
	{
		private int disposed;
		public void Dispose() { if (Interlocked.Exchange(ref this.disposed, 1) == 0) manager.Release(handle); }
	}
}
