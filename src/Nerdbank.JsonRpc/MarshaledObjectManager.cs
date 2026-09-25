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
	private readonly Dictionary<long, IDisposable> localObjects = [];
	private readonly AsyncLocal<HandleScope?> activeScope = new();
	private long nextHandle;

	internal JsonRpcValue Marshal(IDisposable value, JsonRpcEncoding encoding)
	{
		if (value is null)
		{
			throw new ArgumentNullException(nameof(value));
		}

		long handle = Interlocked.Increment(ref this.nextHandle);
		lock (this.sync)
		{
			this.localObjects.Add(handle, value);
		}

		this.activeScope.Value?.Add(handle);
		return encoding == JsonRpcEncoding.Json ? WriteJson(handle) : WriteMessagePack(handle);
	}

	internal IDisposable Unmarshal(JsonRpcValue value)
	{
		(long handle, int direction) = ReadMarker(value);
		if (direction == 0)
		{
			lock (this.sync)
			{
				if (this.localObjects.TryGetValue(handle, out IDisposable? local))
				{
					return local;
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
			(long handle, _) = ReadReleaseArguments(request.Arguments);
			this.ReleaseLocal(handle);
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

	internal void ReleaseLocalObjects(JsonRpcValue value)
	{
		if (!value.HasValue)
		{
			return;
		}

		if (value.Encoding == JsonRpcEncoding.Json)
		{
			using JsonDocument document = JsonDocument.Parse(value.OwnedBytes);
			this.ReleaseJsonMarkers(document.RootElement);
		}
		else
		{
			MessagePackReader reader = new(value.AsMessagePack());
			this.ReleaseMessagePackMarkers(ref reader, new SerializationContext());
		}
	}

	internal void DisposeAll()
	{
		IDisposable[] values;
		lock (this.sync)
		{
			values = [.. this.localObjects.Values];
			this.localObjects.Clear();
		}

		foreach (IDisposable value in values)
		{
			value.Dispose();
		}
	}

	private static JsonRpcValue WriteJson(long handle)
	{
		using Sequence<byte> buffer = new();
		using Utf8JsonWriter writer = new(buffer);
		writer.WriteStartObject();
		writer.WriteNumber(Marker, 1);
		writer.WriteNumber(Handle, handle);
		writer.WriteString(Lifetime, "explicit");
		writer.WriteEndObject();
		writer.Flush();
		return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
	}

	private static JsonRpcValue WriteMessagePack(long handle)
	{
		using Sequence<byte> buffer = new();
		MessagePackWriter writer = new(buffer);
		writer.WriteMapHeader(3);
		writer.Write(Marker);
		writer.Write(1);
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

	private void ReleaseJsonMarkers(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			if (element.TryGetProperty(Marker, out JsonElement marker) && marker.ValueKind == JsonValueKind.Number && marker.GetInt32() == 1 &&
				element.TryGetProperty(Handle, out JsonElement handle) && handle.ValueKind == JsonValueKind.Number)
			{
				this.ReleaseLocal(handle.GetInt64());
			}

			foreach (JsonProperty property in element.EnumerateObject())
			{
				this.ReleaseJsonMarkers(property.Value);
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in element.EnumerateArray())
			{
				this.ReleaseJsonMarkers(item);
			}
		}
	}

	private void ReleaseMessagePackMarkers(ref MessagePackReader reader, SerializationContext context)
	{
		switch (reader.NextMessagePackType)
		{
			case MessagePackType.Array:
				int itemCount = reader.ReadArrayHeader();
				for (int i = 0; i < itemCount; i++)
				{
					this.ReleaseMessagePackMarkers(ref reader, context);
				}

				break;
			case MessagePackType.Map:
				int propertyCount = reader.ReadMapHeader();
				long? handle = null;
				int? marker = null;
				for (int i = 0; i < propertyCount; i++)
				{
					if (reader.NextMessagePackType != MessagePackType.String)
					{
						reader.Skip(context);
						this.ReleaseMessagePackMarkers(ref reader, context);
						continue;
					}

					string? key = reader.ReadString();
					if (key == Handle && reader.NextMessagePackType == MessagePackType.Integer)
					{
						handle = reader.ReadInt64();
					}
					else if (key == Marker && reader.NextMessagePackType == MessagePackType.Integer)
					{
						marker = reader.ReadInt32();
					}
					else
					{
						this.ReleaseMessagePackMarkers(ref reader, context);
					}
				}

				if (marker == 1 && handle.HasValue)
				{
					this.ReleaseLocal(handle.Value);
				}

				break;
			default:
				reader.Skip(context);
				break;
		}
	}

	private void ReleaseLocal(long handle)
	{
		IDisposable? value = null;
		lock (this.sync)
		{
			if (this.localObjects.TryGetValue(handle, out IDisposable? removed))
			{
				this.localObjects.Remove(handle);
				value = removed;
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

		internal void Commit() => this.committed = true;
	}

	private sealed class RemoteDisposable(MarshaledObjectManager manager, long handle) : IDisposable
	{
		private int disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref this.disposed, 1) == 0)
			{
				manager.Release(handle);
			}
		}
	}
}
