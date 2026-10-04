// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

/// <summary>Describes exception details reported by a remote JSON-RPC peer.</summary>
public sealed class RemoteExceptionData
{
	private const int MaximumDepth = 32;
	private const int MaximumNodes = 64;
	private const int MaximumTextLength = 8192;
	private const int MaximumTypedExceptionPayloadLength = 256 * 1024;

	private RemoteExceptionData(string typeName, string message, int hResult, string? stackTrace, string? parameterName, RemoteExceptionData? inner, IReadOnlyList<RemoteExceptionData> children, IReadOnlyDictionary<string, string> data, JsonRpcValue? typedException = null)
	{
		this.TypeName = typeName;
		this.Message = message;
		this.HResult = hResult;
		this.StackTrace = stackTrace;
		this.ParameterName = parameterName;
		this.Inner = inner;
		this.Children = children;
		this.Data = data;
		this.TypedException = typedException;
	}

	/// <summary>Gets the remote exception's assembly-independent CLR full name.</summary>
	public string TypeName { get; }

	/// <summary>Gets the remote exception message.</summary>
	public string Message { get; }

	/// <summary>Gets the remote exception HRESULT.</summary>
	public int HResult { get; }

	/// <summary>Gets the remote stack text, which is diagnostic data and not a local stack trace.</summary>
	public string? StackTrace { get; }

	/// <summary>Gets the argument name for an <see cref="ArgumentException"/>, if present.</summary>
	public string? ParameterName { get; }

	/// <summary>Gets the first causal exception, if present.</summary>
	public RemoteExceptionData? Inner { get; }

	/// <summary>Gets all aggregate children, if the exception is an <see cref="AggregateException"/>.</summary>
	public IReadOnlyList<RemoteExceptionData> Children { get; }

	/// <summary>Gets string-valued entries from <see cref="Exception.Data"/>.</summary>
	public IReadOnlyDictionary<string, string> Data { get; }

	/// <summary>Gets the optional serializer-produced payload for an explicitly allowlisted exception type.</summary>
	internal JsonRpcValue? TypedException { get; }

	/// <summary>Captures a bounded, data-only snapshot of a local exception.</summary>
	/// <param name="exception">The exception to capture.</param>
	/// <param name="serializer">The serializer selected by the connection, used for allowlisted typed exception payloads.</param>
	/// <param name="allowedTypes">The exception types permitted for typed serialization.</param>
	/// <param name="logFailure">The callback that records failures while reading custom exception properties.</param>
	/// <returns>The captured snapshot.</returns>
	internal static RemoteExceptionData Capture(Exception exception, JsonRpcSerializer serializer, RemoteExceptionTypeMapping allowedTypes, Action<Exception> logFailure)
	{
		int remainingNodes = MaximumNodes;
		return Capture(exception, serializer, allowedTypes, 0, ref remainingNodes, logFailure);
	}

	/// <summary>Decodes a bounded snapshot from an untrusted wire value.</summary>
	/// <param name="value">The encoded JSON or MessagePack value.</param>
	/// <param name="logFailure">The callback that records malformed input, or <see langword="null"/>.</param>
	/// <returns>The decoded snapshot, or <see langword="null"/> when the value is absent or malformed.</returns>
	internal static RemoteExceptionData? TryRead(JsonRpcValue value, Action<Exception>? logFailure)
	{
		if (!value.HasValue)
		{
			return null;
		}

		try
		{
			switch (value.Encoding)
			{
				case JsonRpcEncoding.Json:
				{
					using JsonDocument document = JsonDocument.Parse(value.OwnedBytes);
					int remainingNodes = MaximumNodes;
					return ReadJson(document.RootElement, 0, ref remainingNodes);
				}

				case JsonRpcEncoding.MessagePack:
				{
					MessagePackReader reader = new(value.AsOwnedMessagePack());
					int remainingNodes = MaximumNodes;
					return ReadMessagePack(ref reader, 0, ref remainingNodes);
				}
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
		{
			logFailure?.Invoke(ex);
			return null;
		}

		return null;
	}

	/// <summary>Reconstructs only built-in or explicitly allowed exception types.</summary>
	/// <param name="data">The untrusted diagnostic snapshot.</param>
	/// <param name="serializer">The serializer selected by the connection.</param>
	/// <param name="allowedTypes">The frozen exception allowlist.</param>
	/// <returns>The reconstructed exception, or a diagnostic placeholder for an unknown type.</returns>
	internal static Exception Reconstruct(RemoteExceptionData data, JsonRpcSerializer serializer, RemoteExceptionTypeMapping allowedTypes)
	{
		if (data.TypedException is JsonRpcValue typedException && allowedTypes.TryGet(data.TypeName, out RemoteExceptionTypeMapping.Entry? entry) && entry is not null)
		{
			if (typedException.Encoding != serializer.Encoding)
			{
				throw new FormatException("The typed exception payload encoding does not match the connection.");
			}

			return entry.Deserialize(serializer, typedException);
		}

		Exception? inner = data.Inner is not null ? Reconstruct(data.Inner, serializer, allowedTypes) : null;
		if (data.TypeName == typeof(Exception).FullName)
		{
			return new Exception(data.Message, inner);
		}

		if (data.TypeName == typeof(InvalidOperationException).FullName)
		{
			return new InvalidOperationException(data.Message, inner);
		}

		if (data.TypeName == typeof(NotSupportedException).FullName)
		{
			return new NotSupportedException(data.Message, inner);
		}

		if (data.TypeName == typeof(FormatException).FullName)
		{
			return new FormatException(data.Message, inner);
		}

		if (data.TypeName == typeof(ArgumentNullException).FullName)
		{
			return new RemoteArgumentNullException(data);
		}

		if (data.TypeName == typeof(ArgumentOutOfRangeException).FullName)
		{
			return new RemoteArgumentOutOfRangeException(data);
		}

		if (data.TypeName == typeof(ArgumentException).FullName)
		{
			return new RemoteArgumentException(data, inner);
		}

		if (data.TypeName == typeof(AggregateException).FullName)
		{
			return data.Children.Count > 0
				? new AggregateException(data.Message, data.Children.Select(child => Reconstruct(child, serializer, allowedTypes)))
				: inner is not null ? new AggregateException(data.Message, inner) : new AggregateException(data.Message);
		}

		if (data.TypeName == typeof(TaskCanceledException).FullName)
		{
			return new TaskCanceledException(data.Message, inner);
		}

		if (data.TypeName == typeof(OperationCanceledException).FullName)
		{
			return new OperationCanceledException(data.Message, inner);
		}

		return new RemoteInnerException(data);
	}

	/// <summary>Encodes this snapshot using the connection's JSON-RPC value encoding.</summary>
	/// <param name="encoding">The encoding to use.</param>
	/// <returns>The encoded snapshot.</returns>
	internal JsonRpcValue Encode(JsonRpcEncoding encoding)
	{
		if (encoding == JsonRpcEncoding.Json)
		{
			using Sequence<byte> buffer = new();
			using Utf8JsonWriter writer = new(buffer);
			WriteJson(writer, this);
			writer.Flush();
			return JsonRpcValue.FromOwnedBytes(buffer.AsReadOnlySequence.ToArray(), encoding);
		}

		using Sequence<byte> messagePackBuffer = new();
		MessagePackWriter messagePackWriter = new(messagePackBuffer);
		WriteMessagePack(ref messagePackWriter, this);
		messagePackWriter.Flush();
		return JsonRpcValue.FromOwnedBytes(messagePackBuffer.AsReadOnlySequence.ToArray(), encoding);
	}

	private static RemoteExceptionData Capture(Exception exception, JsonRpcSerializer serializer, RemoteExceptionTypeMapping allowedTypes, int depth, ref int remainingNodes, Action<Exception> logFailure)
	{
		if (--remainingNodes < 0 || depth >= MaximumDepth)
		{
			return new RemoteExceptionData(exception.GetType().FullName ?? exception.GetType().Name, "Exception details omitted because the causal chain exceeded its limit.", exception.HResult, null, null, null, Array.Empty<RemoteExceptionData>(), EmptyData());
		}

		string typeName = SafeText(() => exception.GetType().FullName ?? exception.GetType().Name, "Unknown exception type", logFailure);
		string message = SafeText(() => exception.Message, "Exception message unavailable.", logFailure);
		string? stackTrace = SafeText(() => exception.StackTrace, null, logFailure);
		string? parameterName = exception is ArgumentException argument ? SafeText(() => argument.ParamName, null, logFailure) : null;
		RemoteExceptionData? inner = exception is AggregateException ? null : exception.InnerException is Exception innerException ? Capture(innerException, serializer, allowedTypes, depth + 1, ref remainingNodes, logFailure) : null;
		List<RemoteExceptionData> children = [];
		if (exception is AggregateException aggregate)
		{
			foreach (Exception child in aggregate.InnerExceptions)
			{
				if (remainingNodes <= 0 || depth + 1 >= MaximumDepth)
				{
					break;
				}

				children.Add(Capture(child, serializer, allowedTypes, depth + 1, ref remainingNodes, logFailure));
			}
		}

		Dictionary<string, string> data = new(StringComparer.Ordinal);
		try
		{
			foreach (System.Collections.DictionaryEntry item in exception.Data)
			{
				if (item.Key is string key && item.Value is string value && data.Count < 16)
				{
					data[Limit(key, 128)] = Limit(value, 1024);
				}
			}
		}
		catch (Exception ex)
		{
			logFailure(ex);
		}

		JsonRpcValue? typedException = null;
		if (allowedTypes.TryGet(exception.GetType(), out RemoteExceptionTypeMapping.Entry? entry) && entry is not null)
		{
			try
			{
				JsonRpcValue encoded = entry.Serialize(serializer, exception);
				if (encoded.OwnedBytes.Length <= MaximumTypedExceptionPayloadLength)
				{
					typedException = encoded;
				}
				else
				{
					logFailure(new InvalidOperationException("The serialized remote exception exceeded the typed payload limit."));
				}
			}
			catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
			{
				logFailure(ex);
			}
		}

		return new RemoteExceptionData(typeName, message, exception.HResult, stackTrace, parameterName, inner, children.AsReadOnly(), new ReadOnlyDictionary<string, string>(data), typedException);
	}

	private static RemoteExceptionData ReadJson(JsonElement element, int depth, ref int remainingNodes)
	{
		if (--remainingNodes < 0 || depth >= MaximumDepth || element.ValueKind != JsonValueKind.Object)
		{
			throw new FormatException("Invalid or excessive remote exception data.");
		}

		string typeName = GetString(element, "type", "Unknown exception type");
		string message = GetString(element, "message", string.Empty);
		string? stackTrace = GetOptionalString(element, "stack");
		string? parameterName = GetOptionalString(element, "parameterName");
		int hResult = element.TryGetProperty("code", out JsonElement codeElement) && codeElement.TryGetInt32(out int code) ? code : 0;
		RemoteExceptionData? inner = element.TryGetProperty("inner", out JsonElement innerElement) && innerElement.ValueKind == JsonValueKind.Object ? ReadJson(innerElement, depth + 1, ref remainingNodes) : null;
		List<RemoteExceptionData> children = [];
		if (element.TryGetProperty("children", out JsonElement childrenElement) && childrenElement.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement child in childrenElement.EnumerateArray())
			{
				children.Add(ReadJson(child, depth + 1, ref remainingNodes));
			}
		}

		Dictionary<string, string> data = new(StringComparer.Ordinal);
		if (element.TryGetProperty("data", out JsonElement dataElement) && dataElement.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in dataElement.EnumerateObject().Take(16))
			{
				if (property.Value.ValueKind == JsonValueKind.String)
				{
					data[Limit(property.Name, 128)] = Limit(property.Value.GetString(), 1024);
				}
			}
		}

		JsonRpcValue? typedException = null;
		if (element.TryGetProperty("exception", out JsonElement exceptionElement))
		{
			string rawException = exceptionElement.GetRawText();
			if (rawException.Length <= MaximumTypedExceptionPayloadLength)
			{
				byte[] rawExceptionBytes = Encoding.UTF8.GetBytes(rawException);
				if (rawExceptionBytes.Length <= MaximumTypedExceptionPayloadLength)
				{
					typedException = JsonRpcValue.FromJson(rawExceptionBytes);
				}
			}
		}

		return new RemoteExceptionData(typeName, message, hResult, stackTrace, parameterName, inner, children.AsReadOnly(), new ReadOnlyDictionary<string, string>(data), typedException);
	}

	private static RemoteExceptionData ReadMessagePack(ref MessagePackReader reader, int depth, ref int remainingNodes)
	{
		if (--remainingNodes < 0 || depth >= MaximumDepth || reader.NextMessagePackType != MessagePackType.Map)
		{
			throw new FormatException("Invalid or excessive remote exception data.");
		}

		SerializationContext context = new();
		int count = reader.ReadMapHeader();
		string typeName = "Unknown exception type";
		string message = string.Empty;
		int hResult = 0;
		string? stackTrace = null;
		string? parameterName = null;
		RemoteExceptionData? inner = null;
		List<RemoteExceptionData> children = [];
		Dictionary<string, string> data = new(StringComparer.Ordinal);
		JsonRpcValue? typedException = null;
		for (int i = 0; i < count; i++)
		{
			int key = -1;
			if (reader.NextMessagePackType == MessagePackType.Integer)
			{
				key = reader.ReadInt32();
			}
			else if (reader.NextMessagePackType == MessagePackType.String)
			{
				reader.ReadString();
			}
			else
			{
				reader.Skip(context);
				reader.Skip(context);
				continue;
			}

			switch (key)
			{
				case 0: typeName = Limit(reader.ReadString() ?? typeName, MaximumTextLength); break;
				case 1: message = Limit(reader.ReadString() ?? string.Empty, MaximumTextLength); break;
				case 2: stackTrace = Limit(reader.ReadString(), MaximumTextLength); break;
				case 3: hResult = reader.ReadInt32(); break;
				case 4:
					if (reader.NextMessagePackType == MessagePackType.Nil)
					{
						reader.ReadNil();
					}
					else
					{
						inner = ReadMessagePack(ref reader, depth + 1, ref remainingNodes);
					}

					break;
				case 5: ReadMessagePackExtension(ref reader, depth, ref remainingNodes, children, data, ref parameterName, ref typedException, context); break;
				default: reader.Skip(context); break;
			}
		}

		return new RemoteExceptionData(typeName, message, hResult, stackTrace, parameterName, inner, children.AsReadOnly(), new ReadOnlyDictionary<string, string>(data), typedException);
	}

	private static void ReadMessagePackExtension(ref MessagePackReader reader, int depth, ref int remainingNodes, List<RemoteExceptionData> children, Dictionary<string, string> data, ref string? parameterName, ref JsonRpcValue? typedException, SerializationContext context)
	{
		if (reader.NextMessagePackType != MessagePackType.Map)
		{
			reader.Skip(context);
			return;
		}

		int count = reader.ReadMapHeader();
		for (int i = 0; i < count; i++)
		{
			string key = reader.ReadString() ?? string.Empty;
			if (key == "parameterName" && reader.NextMessagePackType == MessagePackType.String)
			{
				parameterName = Limit(reader.ReadString(), 1024);
			}
			else if (key == "children" && reader.NextMessagePackType == MessagePackType.Array)
			{
				int childCount = reader.ReadArrayHeader();
				for (int j = 0; j < childCount; j++)
				{
					children.Add(ReadMessagePack(ref reader, depth + 1, ref remainingNodes));
				}
			}
			else if (key == "exception")
			{
				RawMessagePack rawException = reader.ReadRaw(context);
				if (rawException.MsgPack.Length <= MaximumTypedExceptionPayloadLength)
				{
					typedException = JsonRpcValue.FromMessagePack(rawException);
				}
			}
			else if (key == "data" && reader.NextMessagePackType == MessagePackType.Map)
			{
				int dataCount = reader.ReadMapHeader();
				for (int j = 0; j < dataCount; j++)
				{
					string name = reader.ReadString() ?? string.Empty;
					if (j < 16 && reader.NextMessagePackType == MessagePackType.String)
					{
						data[Limit(name, 128)] = Limit(reader.ReadString(), 1024);
					}
					else
					{
						reader.Skip(context);
					}
				}
			}
			else
			{
				reader.Skip(context);
			}
		}
	}

	private static void WriteJson(Utf8JsonWriter writer, RemoteExceptionData data)
	{
		writer.WriteStartObject();
		writer.WriteString("type", data.TypeName);
		writer.WriteString("message", data.Message);
		writer.WriteString("stack", data.StackTrace);
		writer.WriteNumber("code", data.HResult);
		if (data.Inner is null)
		{
			writer.WriteNull("inner");
		}
		else
		{
			writer.WritePropertyName("inner");
			WriteJson(writer, data.Inner);
		}

		writer.WriteString("parameterName", data.ParameterName);
		writer.WriteStartArray("children");
		foreach (RemoteExceptionData child in data.Children)
		{
			WriteJson(writer, child);
		}

		writer.WriteEndArray();
		writer.WriteStartObject("data");
		foreach ((string key, string value) in data.Data)
		{
			writer.WriteString(key, value);
		}

		writer.WriteEndObject();
		if (data.TypedException is JsonRpcValue typedException)
		{
			writer.WritePropertyName("exception");
			using JsonDocument document = JsonDocument.Parse(typedException.OwnedBytes);
			document.RootElement.WriteTo(writer);
		}

		writer.WriteEndObject();
	}

	private static void WriteMessagePack(ref MessagePackWriter writer, RemoteExceptionData data)
	{
		writer.WriteMapHeader(6);
		writer.Write(0);
		writer.Write(data.TypeName);
		writer.Write(1);
		writer.Write(data.Message);
		writer.Write(2);
		writer.Write(data.StackTrace);
		writer.Write(3);
		writer.Write(data.HResult);
		writer.Write(4);
		if (data.Inner is null)
		{
			writer.WriteNil();
		}
		else
		{
			WriteMessagePack(ref writer, data.Inner);
		}

		writer.Write(5);
		writer.WriteMapHeader(data.TypedException.HasValue ? 4 : 3);
		writer.Write("parameterName");
		writer.Write(data.ParameterName);
		writer.Write("children");
		writer.WriteArrayHeader(data.Children.Count);
		foreach (RemoteExceptionData child in data.Children)
		{
			WriteMessagePack(ref writer, child);
		}

		writer.Write("data");
		writer.WriteMapHeader(data.Data.Count);
		foreach ((string key, string value) in data.Data)
		{
			writer.Write(key);
			writer.Write(value);
		}

		if (data.TypedException is JsonRpcValue typedException)
		{
			writer.Write("exception");
			writer.WriteRaw(typedException.AsOwnedMessagePack().MsgPack);
		}
	}

	private static IReadOnlyDictionary<string, string> EmptyData() => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

	private static string SafeText(Func<string?> getter, string? fallback, Action<Exception> logFailure)
	{
		try
		{
			return Limit(getter(), MaximumTextLength) ?? fallback ?? string.Empty;
		}
		catch (Exception ex)
		{
			logFailure(ex);
			return fallback ?? string.Empty;
		}
	}

	private static string GetString(JsonElement element, string name, string fallback)
		=> element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? Limit(value.GetString(), MaximumTextLength) ?? fallback : fallback;

	private static string? GetOptionalString(JsonElement element, string name)
		=> element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? Limit(value.GetString(), MaximumTextLength) : null;

	private static string Limit(string? value, int length) => value is { Length: > 0 } text ? text.Length > length ? text[..length] : text : value ?? string.Empty;

	private sealed class RemoteArgumentException(RemoteExceptionData data, Exception? inner)
		: ArgumentException(data.Message, data.ParameterName, inner)
	{
		public override string Message => data.Message;
	}

	private sealed class RemoteArgumentNullException(RemoteExceptionData data)
		: ArgumentNullException(data.ParameterName, data.Message)
	{
		public override string Message => data.Message;
	}

	private sealed class RemoteArgumentOutOfRangeException(RemoteExceptionData data)
		: ArgumentOutOfRangeException(data.ParameterName, data.Message)
	{
		public override string Message => data.Message;
	}

	private sealed class RemoteInnerException(RemoteExceptionData data) : Exception(data.Message)
	{
		public override string ToString() => $"Remote exception: {data.TypeName} (HResult 0x{data.HResult:X8})\n{data.Message}{(data.StackTrace is null ? string.Empty : "\n--- remote stack ---\n" + data.StackTrace)}";
	}
}
