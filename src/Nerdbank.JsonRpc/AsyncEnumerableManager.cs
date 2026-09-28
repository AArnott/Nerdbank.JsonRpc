// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Implements the JSON-RPC bridge for <see cref="IAsyncEnumerable{T}"/> values, which travel as a token
/// that the consumer uses to pull batches of values on demand.
/// </summary>
/// <param name="owner">The connection this manager serves.</param>
internal sealed class AsyncEnumerableManager(JsonRpc owner) : IDisposable
{
	/// <summary>The method a consumer invokes to request more values.</summary>
	internal const string NextMethod = "$/enumerator/next";

	/// <summary>The method a consumer invokes to release a generator it will not finish enumerating.</summary>
	internal const string AbortMethod = "$/enumerator/abort";

	private const string TokenPropertyName = "token";
	private const string ValuesPropertyName = "values";
	private const string FinishedPropertyName = "finished";

	private readonly object sync = new();
	private readonly Dictionary<long, Generator> generators = [];
	private readonly Dictionary<RequestId, List<long>> generatorsByRequest = [];
	private readonly System.Threading.AsyncLocal<OutboundScope?> activeOutboundScope = new();
	private readonly System.Threading.AsyncLocal<InboundScope?> activeInboundScope = new();
	private long nextToken;

	/// <summary>Gets the connection this manager serves.</summary>
	private JsonRpc Owner => owner;

	/// <summary>Releases every generator this connection is tracking.</summary>
	public void Dispose()
	{
		Generator[] snapshot;
		lock (this.sync)
		{
			snapshot = [.. this.generators.Values];
			this.generators.Clear();
			this.generatorsByRequest.Clear();
		}

		foreach (Generator generator in snapshot)
		{
			generator.DisposeAsync().AsTask().Forget();
		}
	}

	/// <summary>Begins tracking the generators created while serializing one outbound message.</summary>
	/// <param name="argumentLifetime">The call-scoped arguments retained by returned generators.</param>
	/// <returns>A scope to dispose when serialization completes.</returns>
	internal OutboundScope TrackOutboundMessage(CallScopedLifetime? argumentLifetime = null) => new(this, argumentLifetime);

	/// <summary>Begins tracking an inbound message so that sequences in notifications can be rejected.</summary>
	/// <param name="hasResponse">Whether the inbound message is a request that will receive a response.</param>
	/// <returns>A scope to dispose when deserialization completes.</returns>
	internal InboundScope TrackInboundRequest(bool hasResponse) => new(this, hasResponse);

	/// <summary>Assigns a token to a sequence being sent to the remote party and prepares to generate its values.</summary>
	/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
	/// <param name="enumerable">The sequence to transmit.</param>
	/// <param name="elementShape">The type shape describing <typeparamref name="T"/>.</param>
	/// <param name="encoding">The wire encoding to produce.</param>
	/// <returns>The encoded value to write in place of the sequence.</returns>
	internal JsonRpcValue Marshal<T>(IAsyncEnumerable<T> enumerable, ITypeShape<T> elementShape, JsonRpcEncoding encoding)
	{
		OutboundScope scope = this.activeOutboundScope.Value
			?? throw new InvalidOperationException("IAsyncEnumerable<T> values may only be sent in RPC requests and responses.");

		JsonRpcEnumerableSettings settings = JsonRpcEnumerableSettings.Default;
		IReadOnlyList<T> prefetched = Array.Empty<T>();
		bool finished = false;
		if (enumerable is RpcEnumerable<T> decorated)
		{
			settings = decorated.Settings;
			(prefetched, finished) = decorated.TearOffPrefetchedElements();
		}

		long? token = null;
		if (!finished)
		{
			token = Interlocked.Increment(ref this.nextToken);
			Generator generator = new Generator<T>(this, token.Value, enumerable, elementShape, settings)
			{
				ArgumentLifetime = scope.ArgumentLifetime?.Retain(),
			};
			lock (this.sync)
			{
				this.generators.Add(token.Value, generator);
			}

			scope.Add(token.Value);
		}

		return this.WriteOriginatingValue(token, prefetched, elementShape, encoding);
	}

	/// <summary>Creates a local sequence that pulls its values from a remote generator.</summary>
	/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
	/// <param name="value">The encoded value carrying the token and any values that rode along with it.</param>
	/// <param name="elementShape">The type shape describing <typeparamref name="T"/>.</param>
	/// <returns>A sequence that may be enumerated exactly once.</returns>
#pragma warning disable VSTHRD200 // This method returns a sequence, not an awaitable.
	internal IAsyncEnumerable<T> Unmarshal<T>(JsonRpcValue value, ITypeShape<T> elementShape)
#pragma warning restore VSTHRD200
	{
		if (this.activeInboundScope.Value is { HasResponse: false })
		{
			throw new FormatException("IAsyncEnumerable<T> values cannot be received in notifications.");
		}

		(long? token, List<JsonRpcValue> rawValues) = ReadOriginatingValue(value);
		List<T> prefetched = new(rawValues.Count);
		foreach (JsonRpcValue rawValue in rawValues)
		{
			prefetched.Add(owner.UserDataSerializer.Deserialize(rawValue, elementShape, owner.DisposalToken));
		}

		ConsumerEnumerable<T> consumer = new(this, token, prefetched, elementShape);
		if (token.HasValue)
		{
			this.activeInboundScope.Value?.Add(consumer.RetainCallScopedArguments);
		}

		return consumer;
	}

	/// <summary>Associates the generators serialized into a request with that request so they are released when it completes.</summary>
	/// <param name="request">The request about to be transmitted.</param>
	internal void RegisterOutboundRequest(JsonRpcRequest request)
	{
		if (request.Id is not RequestId id || request.Arguments.AsyncEnumerableTokens is not TokenSet { IsEmpty: false } set)
		{
			return;
		}

		lock (this.sync)
		{
			this.generatorsByRequest[id] = [.. set.Tokens];
		}
	}

	/// <summary>Releases the generators that were sent as arguments of a request that has now completed.</summary>
	/// <param name="id">The ID of the completed request.</param>
	internal void CompleteOutboundRequest(RequestId id)
	{
		List<Generator> released = [];
		lock (this.sync)
		{
			if (!this.generatorsByRequest.TryGetValue(id, out List<long>? tokens))
			{
				return;
			}

			this.generatorsByRequest.Remove(id);
			foreach (long token in tokens)
			{
				if (this.generators.TryGetValue(token, out Generator? generator))
				{
					this.generators.Remove(token);
					released.Add(generator);
				}
			}
		}

		foreach (Generator generator in released)
		{
			generator.DisposeAsync().AsTask().Forget();
		}
	}

	/// <summary>Releases the generators encoded into a value that will never be transmitted.</summary>
	/// <param name="value">The encoded value whose generators should be released.</param>
	internal void ReleaseGenerators(JsonRpcValue value)
	{
		if (value.AsyncEnumerableTokens is not TokenSet { IsEmpty: false } set)
		{
			return;
		}

		foreach (long token in set.Tokens)
		{
			this.DisposeGeneratorAsync(token).AsTask().Forget();
		}
	}

	/// <summary>Rejects an attempt to send a sequence in a notification, which the peer could never release.</summary>
	/// <param name="arguments">The encoded arguments to inspect.</param>
	internal void EnsureNoAsyncEnumerables(JsonRpcValue arguments)
	{
		if (arguments.AsyncEnumerableTokens is TokenSet { IsEmpty: false })
		{
			this.ReleaseGenerators(arguments);
			throw new InvalidOperationException("IAsyncEnumerable<T> values cannot be sent in notifications because the receiver has no way to release them.");
		}
	}

	/// <summary>Routes the enumerator protocol methods to this manager.</summary>
	/// <param name="request">The inbound request.</param>
	/// <param name="invoker">Receives the handler for the request.</param>
	/// <returns><see langword="true"/> if <paramref name="request"/> targets this manager.</returns>
	internal bool TryGetMethodInvoker(JsonRpcRequest request, out MethodInvoker invoker)
	{
		switch (request.Method)
		{
			case NextMethod:
				invoker = this.GetNextValuesAsync;
				return true;
			case AbortMethod:
				invoker = this.AbortAsync;
				return true;
			default:
				invoker = null!;
				return false;
		}
	}

	/// <summary>Reads the integer token carried by an encoded value.</summary>
	/// <param name="value">The encoded token.</param>
	/// <returns>The token.</returns>
	private static long ReadToken(JsonRpcValue value)
	{
		if (value.Encoding == JsonRpcEncoding.Json)
		{
			using JsonDocument document = JsonDocument.Parse(value.OwnedBytes);
			return document.RootElement.ValueKind == JsonValueKind.Number && document.RootElement.TryGetInt64(out long parsed)
				? parsed
				: throw new FormatException("An enumerator token must be an integer.");
		}

		MessagePackReader reader = new(value.AsOwnedMessagePack());
		return reader.ReadInt64();
	}

	/// <summary>Parses the message that introduces a sequence into its token and accompanying values.</summary>
	/// <param name="value">The encoded value.</param>
	/// <returns>The token, if more values may be requested, and the values that rode along.</returns>
	private static (long? Token, List<JsonRpcValue> Values) ReadOriginatingValue(JsonRpcValue value)
	{
		List<JsonRpcValue> values = [];
		long? token = null;
		if (value.Encoding == JsonRpcEncoding.Json)
		{
			using JsonDocument document = JsonDocument.Parse(value.OwnedBytes);
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				throw new FormatException("An async enumerable must be encoded as an object.");
			}

			foreach (JsonProperty property in document.RootElement.EnumerateObject())
			{
				switch (property.Name)
				{
					case TokenPropertyName when property.Value.ValueKind is not JsonValueKind.Null:
						token = property.Value.GetInt64();
						break;
					case ValuesPropertyName when property.Value.ValueKind is JsonValueKind.Array:
						foreach (JsonElement element in property.Value.EnumerateArray())
						{
							values.Add(JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(element.GetRawText())));
						}

						break;
				}
			}

			return (token, values);
		}

		MessagePackReader reader = new(value.AsOwnedMessagePack());
		SerializationContext context = new();
		int count = reader.ReadMapHeader();
		for (int i = 0; i < count; i++)
		{
			string? name = reader.ReadString();
			switch (name)
			{
				case TokenPropertyName when reader.NextMessagePackType != MessagePackType.Nil:
					token = reader.ReadInt64();
					break;
				case ValuesPropertyName when reader.NextMessagePackType == MessagePackType.Array:
					int valueCount = reader.ReadArrayHeader();
					for (int j = 0; j < valueCount; j++)
					{
						values.Add(JsonRpcValue.FromMessagePack(reader.ReadRaw(context)));
					}

					break;
				default:
					reader.Skip(context);
					break;
			}
		}

		return (token, values);
	}

	/// <summary>Parses a generator's response into its values and completion flag.</summary>
	/// <param name="value">The encoded response.</param>
	/// <returns>The values in this batch and whether the sequence has ended.</returns>
	private static (List<JsonRpcValue> Values, bool Finished) ReadResultValue(JsonRpcValue value)
	{
		List<JsonRpcValue> values = [];
		bool finished = false;
		if (value.Encoding == JsonRpcEncoding.Json)
		{
			using JsonDocument document = JsonDocument.Parse(value.OwnedBytes);
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				throw new FormatException("An enumerator response must be an object.");
			}

			foreach (JsonProperty property in document.RootElement.EnumerateObject())
			{
				switch (property.Name)
				{
					case ValuesPropertyName when property.Value.ValueKind is JsonValueKind.Array:
						foreach (JsonElement element in property.Value.EnumerateArray())
						{
							values.Add(JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(element.GetRawText())));
						}

						break;
					case FinishedPropertyName:
						finished = property.Value.ValueKind == JsonValueKind.True;
						break;
				}
			}

			return (values, finished);
		}

		MessagePackReader reader = new(value.AsOwnedMessagePack());
		SerializationContext context = new();
		int count = reader.ReadMapHeader();
		for (int i = 0; i < count; i++)
		{
			string? name = reader.ReadString();
			switch (name)
			{
				case ValuesPropertyName when reader.NextMessagePackType == MessagePackType.Array:
					int valueCount = reader.ReadArrayHeader();
					for (int j = 0; j < valueCount; j++)
					{
						values.Add(JsonRpcValue.FromMessagePack(reader.ReadRaw(context)));
					}

					break;
				case FinishedPropertyName:
					finished = reader.ReadBoolean();
					break;
				default:
					reader.Skip(context);
					break;
			}
		}

		return (values, finished);
	}

	/// <summary>Appends raw bytes to a buffer.</summary>
	/// <param name="buffer">The destination buffer.</param>
	/// <param name="value">The bytes to append.</param>
	private static void Write(IBufferWriter<byte> buffer, ReadOnlySpan<byte> value)
	{
		value.CopyTo(buffer.GetSpan(value.Length));
		buffer.Advance(value.Length);
	}

	/// <summary>Appends UTF-8 text to a buffer.</summary>
	/// <param name="buffer">The destination buffer.</param>
	/// <param name="value">The text to append.</param>
	private static void Write(IBufferWriter<byte> buffer, string value) => Write(buffer, Encoding.UTF8.GetBytes(value));

	/// <summary>Encodes the message that introduces a sequence to the remote party.</summary>
	/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
	/// <param name="token">The token the consumer uses to ask for more values, or <see langword="null"/> when the sequence is already complete.</param>
	/// <param name="values">The values to include in this message.</param>
	/// <param name="elementShape">The type shape describing <typeparamref name="T"/>.</param>
	/// <param name="encoding">The wire encoding to produce.</param>
	/// <returns>The encoded value.</returns>
	private JsonRpcValue WriteOriginatingValue<T>(long? token, IReadOnlyList<T> values, ITypeShape<T> elementShape, JsonRpcEncoding encoding)
	{
		if (encoding == JsonRpcEncoding.Json)
		{
			using Sequence<byte> buffer = new();
			Write(buffer, "{");
			bool wroteProperty = false;
			if (token is long tokenValue)
			{
				Write(buffer, "\"" + TokenPropertyName + "\":");
				Write(buffer, tokenValue.ToString(CultureInfo.InvariantCulture));
				wroteProperty = true;
			}

			if (values.Count > 0)
			{
				Write(buffer, wroteProperty ? ",\"" + ValuesPropertyName + "\":[" : "\"" + ValuesPropertyName + "\":[");
				for (int i = 0; i < values.Count; i++)
				{
					if (i > 0)
					{
						Write(buffer, ",");
					}

					Write(buffer, owner.UserDataSerializer.Serialize(values[i], elementShape, owner.DisposalToken).OwnedBytes.Span);
				}

				Write(buffer, "]");
			}

			Write(buffer, "}");
			return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
		}

		using Sequence<byte> messagePackBuffer = new();
		MessagePackWriter writer = new(messagePackBuffer);
		writer.WriteMapHeader((token is null ? 0 : 1) + (values.Count > 0 ? 1 : 0));
		if (token is long messagePackToken)
		{
			writer.Write(TokenPropertyName);
			writer.Write(messagePackToken);
		}

		if (values.Count > 0)
		{
			writer.Write(ValuesPropertyName);
			writer.WriteArrayHeader(values.Count);
			foreach (T value in values)
			{
				writer.Write(owner.UserDataSerializer.Serialize(value, elementShape, owner.DisposalToken).AsOwnedMessagePack());
			}
		}

		writer.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)messagePackBuffer.AsReadOnlySequence.ToArray());
	}

	/// <summary>Encodes a generator's response carrying a batch of values.</summary>
	/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
	/// <param name="values">The values in this batch.</param>
	/// <param name="finished">Whether the sequence has ended.</param>
	/// <param name="elementShape">The type shape describing <typeparamref name="T"/>.</param>
	/// <param name="encoding">The wire encoding to produce.</param>
	/// <returns>The encoded response value.</returns>
	private JsonRpcValue WriteResultValue<T>(IReadOnlyList<T> values, bool finished, ITypeShape<T> elementShape, JsonRpcEncoding encoding)
	{
		if (encoding == JsonRpcEncoding.Json)
		{
			using Sequence<byte> buffer = new();
			Write(buffer, "{\"" + ValuesPropertyName + "\":[");
			for (int i = 0; i < values.Count; i++)
			{
				if (i > 0)
				{
					Write(buffer, ",");
				}

				Write(buffer, owner.UserDataSerializer.Serialize(values[i], elementShape, owner.DisposalToken).OwnedBytes.Span);
			}

			Write(buffer, finished ? "],\"" + FinishedPropertyName + "\":true}" : "],\"" + FinishedPropertyName + "\":false}");
			return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
		}

		using Sequence<byte> messagePackBuffer = new();
		MessagePackWriter writer = new(messagePackBuffer);
		writer.WriteMapHeader(2);
		writer.Write(ValuesPropertyName);
		writer.WriteArrayHeader(values.Count);
		foreach (T value in values)
		{
			writer.Write(owner.UserDataSerializer.Serialize(value, elementShape, owner.DisposalToken).AsOwnedMessagePack());
		}

		writer.Write(FinishedPropertyName);
		writer.Write(finished);
		writer.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)messagePackBuffer.AsReadOnlySequence.ToArray());
	}

	/// <summary>Encodes the single token argument shared by the enumerator protocol methods.</summary>
	/// <param name="token">The token identifying the generator.</param>
	/// <returns>The encoded arguments.</returns>
	private JsonRpcValue CreateTokenArguments(long token)
	{
		if (owner.UserDataSerializer.Encoding == JsonRpcEncoding.Json)
		{
			using Sequence<byte> buffer = new();
			Write(buffer, "{\"" + TokenPropertyName + "\":");
			Write(buffer, token.ToString(CultureInfo.InvariantCulture));
			Write(buffer, "}");
			return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
		}

		using Sequence<byte> messagePackBuffer = new();
		MessagePackWriter writer = new(messagePackBuffer);
		writer.WriteMapHeader(1);
		writer.Write(TokenPropertyName);
		writer.Write(token);
		writer.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)messagePackBuffer.AsReadOnlySequence.ToArray());
	}

	/// <summary>Encodes an empty value for responses that carry no data.</summary>
	/// <returns>The encoded value.</returns>
	private JsonRpcValue CreateNullValue()
	{
		if (owner.UserDataSerializer.Encoding == JsonRpcEncoding.Json)
		{
			return JsonRpcValue.FromJson("null"u8.ToArray());
		}

		using Sequence<byte> buffer = new();
		MessagePackWriter writer = new(buffer);
		writer.WriteNil();
		writer.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)buffer.AsReadOnlySequence.ToArray());
	}

	/// <summary>Extracts the <c>token</c> argument from an inbound enumerator protocol request.</summary>
	/// <param name="dispatch">The inbound request.</param>
	/// <returns>The token identifying the generator.</returns>
	private long ReadTokenArgument(DispatchRequest dispatch)
	{
		(bool named, List<(string? Name, JsonRpcValue Value)> values) = dispatch.UserDataSerializer.ReadArguments(dispatch.Request.Arguments);
		if (named)
		{
			foreach ((string? name, JsonRpcValue value) in values)
			{
				if (name == TokenPropertyName)
				{
					return ReadToken(value);
				}
			}

			throw new FormatException("The enumerator request is missing its token argument.");
		}

		return values.Count == 1
			? ReadToken(values[0].Value)
			: throw new FormatException("The enumerator request must carry exactly one token argument.");
	}

	/// <summary>Handles a consumer's request for the next batch of values.</summary>
	/// <param name="dispatch">The inbound request.</param>
	/// <returns>The response carrying the batch.</returns>
	private async ValueTask<DispatchResponse> GetNextValuesAsync(DispatchRequest dispatch)
	{
		RequestId? id = dispatch.Request.Id;
		try
		{
			long token = this.ReadTokenArgument(dispatch);
			Generator? generator;
			lock (this.sync)
			{
				this.generators.TryGetValue(token, out generator);
			}

			if (generator is null)
			{
				return new DispatchResponse
				{
					Response = id is RequestId missingId
						? new JsonRpcError { Id = missingId, Error = new() { Code = JsonRpcErrorCode.NoMarshaledObjectFound, Message = $"No async enumerator with token {token} exists." } }
						: null,
				};
			}

			JsonRpcValue result;
			using (OutboundScope nestedScope = this.TrackOutboundMessage(generator.ArgumentLifetime))
			{
				try
				{
					result = await generator.GetNextValuesAsync(dispatch.CancellationToken).ConfigureAwait(false);
				}
				catch
				{
					await this.DisposeGeneratorAsync(token).ConfigureAwait(false);
					throw;
				}

				// Nested sequences discovered while serializing this batch's elements are independent generators;
				// keep them alive (the consumer will pull or abort them on their own) instead of letting the scope
				// dispose them now that serialization has completed successfully.
				nestedScope.Commit();
			}

			return new DispatchResponse { Response = id is RequestId resultId ? new JsonRpcResult { Id = resultId, Result = result } : null };
		}
		catch (OperationCanceledException ex) when (dispatch.CancellationToken.IsCancellationRequested)
		{
			return new DispatchResponse
			{
				Response = id is RequestId cancelledId
					? new JsonRpcError { Id = cancelledId, Error = new() { Code = JsonRpcErrorCode.RequestCancelled, Message = ex.Message } }
					: null,
			};
		}
		catch (Exception ex)
		{
			owner.LogApplicationError(ex);
			return new DispatchResponse
			{
				Response = id is RequestId errorId
					? new JsonRpcError { Id = errorId, Error = new() { Code = JsonRpcErrorCode.InternalError, Message = ex.Message } }
					: null,
			};
		}
	}

	/// <summary>Handles a consumer's notice that it will not finish enumerating a sequence.</summary>
	/// <param name="dispatch">The inbound request or notification.</param>
	/// <returns>An empty response when a response was requested.</returns>
	private async ValueTask<DispatchResponse> AbortAsync(DispatchRequest dispatch)
	{
		RequestId? id = dispatch.Request.Id;
		try
		{
			await this.DisposeGeneratorAsync(this.ReadTokenArgument(dispatch)).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			owner.LogApplicationError(ex);
		}

		return new DispatchResponse { Response = id is RequestId responseId ? new JsonRpcResult { Id = responseId, Result = this.CreateNullValue() } : null };
	}

	/// <summary>Stops tracking a generator and releases the state machine behind it.</summary>
	/// <param name="token">The token identifying the generator.</param>
	/// <returns>A task that completes when the generator has been released.</returns>
	private ValueTask DisposeGeneratorAsync(long token)
	{
		Generator? generator;
		lock (this.sync)
		{
			if (!this.generators.TryGetValue(token, out generator))
			{
				return default;
			}

			this.generators.Remove(token);
		}

		return generator.DisposeAsync();
	}

	/// <summary>Asks the remote generator for the next batch of values.</summary>
	/// <param name="token">The token identifying the generator.</param>
	/// <param name="cancellationToken">A token to cancel the request.</param>
	/// <returns>The encoded response carrying the batch.</returns>
	private ValueTask<JsonRpcValue> RequestNextValuesAsync(long token, CancellationToken cancellationToken)
		=> owner.RequestRawAsync(NextMethod, this.CreateTokenArguments(token), cancellationToken);

	/// <summary>Tells the remote generator that no more values will be requested.</summary>
	/// <param name="token">The token identifying the generator.</param>
	/// <param name="waitForCompletion">Whether disposal must finish before releasing call-scoped arguments.</param>
	/// <returns>A task that completes when the abort is queued or acknowledged.</returns>
	private async ValueTask AbortRemoteAsync(long token, bool waitForCompletion)
	{
		try
		{
			if (waitForCompletion)
			{
				await owner.RequestRawAsync(AbortMethod, this.CreateTokenArguments(token), CancellationToken.None).ConfigureAwait(false);
			}
			else
			{
				await owner.NotifyAsync(AbortMethod, this.CreateTokenArguments(token), CancellationToken.None).ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			// The connection may already be gone, in which case the remote generator has been released anyway.
			owner.LogApplicationError(ex);
		}
	}

	/// <summary>Tracks the generators created while one outbound message is serialized.</summary>
	internal sealed class OutboundScope : IDisposable
	{
		private readonly AsyncEnumerableManager manager;
		private readonly OutboundScope? priorScope;
		private List<long>? tokens;
		private bool committed;

		/// <summary>Initializes a new instance of the <see cref="OutboundScope"/> class.</summary>
		/// <param name="manager">The owning manager.</param>
		/// <param name="argumentLifetime">The arguments retained by generators created in this scope.</param>
		internal OutboundScope(AsyncEnumerableManager manager, CallScopedLifetime? argumentLifetime)
		{
			this.manager = manager;
			this.ArgumentLifetime = argumentLifetime;
			this.priorScope = manager.activeOutboundScope.Value;
			manager.activeOutboundScope.Value = this;
		}

		/// <summary>Gets the arguments retained by generators created in this scope.</summary>
		internal CallScopedLifetime? ArgumentLifetime { get; }

		/// <summary>Ends this scope, releasing any generators that were never committed.</summary>
		public void Dispose()
		{
			this.manager.activeOutboundScope.Value = this.priorScope;
			if (!this.committed && this.tokens is { } tokens)
			{
				foreach (long token in tokens)
				{
					this.manager.DisposeGeneratorAsync(token).AsTask().Forget();
				}
			}
		}

		/// <summary>Records a generator created while serializing this message.</summary>
		/// <param name="token">The token identifying the generator.</param>
		internal void Add(long token) => (this.tokens ??= []).Add(token);

		/// <summary>Transfers ownership of the tracked generators to the caller.</summary>
		/// <returns>The set of generator tokens carried by the serialized message.</returns>
		internal TokenSet Commit()
		{
			this.committed = true;
			return this.tokens is { Count: > 0 } tokens ? new([.. tokens]) : TokenSet.Empty;
		}
	}

	/// <summary>Tracks whether the message currently being deserialized may carry a sequence.</summary>
	internal sealed class InboundScope : IDisposable
	{
		private readonly AsyncEnumerableManager manager;
		private readonly InboundScope? priorScope;
		private List<Action<CallScopedLifetime>>? consumers;

		/// <summary>Initializes a new instance of the <see cref="InboundScope"/> class.</summary>
		/// <param name="manager">The owning manager.</param>
		/// <param name="hasResponse">Whether the inbound message will receive a response.</param>
		internal InboundScope(AsyncEnumerableManager manager, bool hasResponse)
		{
			this.manager = manager;
			this.HasResponse = hasResponse;
			this.priorScope = manager.activeInboundScope.Value;
			manager.activeInboundScope.Value = this;
		}

		/// <summary>Gets a value indicating whether the inbound message will receive a response.</summary>
		internal bool HasResponse { get; }

		/// <summary>Ends this scope.</summary>
		public void Dispose() => this.manager.activeInboundScope.Value = this.priorScope;

		/// <summary>Records a consumer created while decoding the message.</summary>
		/// <param name="retain">Attaches a lease to the consumer.</param>
		internal void Add(Action<CallScopedLifetime> retain) => (this.consumers ??= []).Add(retain);

		/// <summary>Transfers successful response argument ownership to its consumers.</summary>
		/// <param name="lifetime">The call-scoped argument lifetime, if any.</param>
		internal void RetainCallScopedArguments(CallScopedLifetime? lifetime)
		{
			if (lifetime is not null && this.consumers is { } consumers)
			{
				foreach (Action<CallScopedLifetime> retain in consumers)
				{
					retain(lifetime);
				}
			}
		}
	}

	/// <summary>The generator tokens carried by one serialized message.</summary>
	/// <param name="tokens">The tokens.</param>
	internal sealed class TokenSet(long[] tokens)
	{
		internal static readonly TokenSet Empty = new([]);

		/// <summary>Gets the tokens.</summary>
		internal IReadOnlyList<long> Tokens => tokens;

		/// <summary>Gets a value indicating whether no generators were created.</summary>
		internal bool IsEmpty => tokens.Length == 0;
	}

	/// <summary>Produces values for a remote consumer on demand.</summary>
	private abstract class Generator
	{
		/// <summary>Gets or sets the lease keeping this generator's call-scoped arguments valid.</summary>
		internal CallScopedLifetime? ArgumentLifetime { get; set; }

		/// <summary>Produces the next batch of values.</summary>
		/// <param name="cancellationToken">A token to cancel value production.</param>
		/// <returns>The encoded response carrying the batch.</returns>
		internal abstract ValueTask<JsonRpcValue> GetNextValuesAsync(CancellationToken cancellationToken);

		/// <summary>Releases the underlying state machine.</summary>
		/// <returns>A task that completes when the state machine has been released.</returns>
		internal abstract ValueTask DisposeAsync();
	}

	/// <summary>Produces values of a particular type for a remote consumer.</summary>
	/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
	private sealed class Generator<T> : Generator
	{
		private readonly AsyncEnumerableManager manager;
		private readonly long token;
		private readonly ITypeShape<T> elementShape;
		private readonly JsonRpcEnumerableSettings settings;
		private readonly CancellationTokenSource cancellationSource = new();
		private readonly IAsyncEnumerator<T> enumerator;
		private readonly Channel<T>? readAhead;
		private readonly Task? readAheadTask;
		private bool disposed;

		/// <summary>Initializes a new instance of the <see cref="Generator{T}"/> class.</summary>
		/// <param name="manager">The owning manager.</param>
		/// <param name="token">The token identifying this generator.</param>
		/// <param name="enumerable">The sequence to produce values from.</param>
		/// <param name="elementShape">The type shape describing <typeparamref name="T"/>.</param>
		/// <param name="settings">The settings tuning how values are produced and batched.</param>
		internal Generator(AsyncEnumerableManager manager, long token, IAsyncEnumerable<T> enumerable, ITypeShape<T> elementShape, JsonRpcEnumerableSettings settings)
		{
			this.manager = manager;
			this.token = token;
			this.elementShape = elementShape;
			this.settings = settings;
			this.enumerator = enumerable.GetAsyncEnumerator(this.cancellationSource.Token);

			if (settings.MaxReadAhead > 0)
			{
				this.readAhead = Channel.CreateBounded<T>(new BoundedChannelOptions(settings.MaxReadAhead) { SingleReader = true, SingleWriter = true });
				this.readAheadTask = this.ReadAheadAsync();
			}
		}

		/// <inheritdoc/>
		internal override async ValueTask<JsonRpcValue> GetNextValuesAsync(CancellationToken cancellationToken)
		{
			using CancellationTokenRegistration registration = cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), this.cancellationSource);
			CancellationToken generatorToken = this.cancellationSource.Token;

			List<T> results = new(this.settings.MinBatchSize);
			bool finished = false;
			if (this.readAhead is not null)
			{
				ChannelReader<T> reader = this.readAhead.Reader;
				while (results.Count < this.settings.MinBatchSize)
				{
					if (!await reader.WaitToReadAsync(generatorToken).ConfigureAwait(false))
					{
						finished = true;
						break;
					}

					if (reader.TryRead(out T? item))
					{
						results.Add(item);
					}
				}

				// Drain whatever else is already cached so read ahead can get back to work promptly.
				while (!finished && results.Count < this.settings.MaxReadAhead && reader.TryRead(out T? extra))
				{
					results.Add(extra);
				}
			}
			else
			{
				for (int i = 0; i < this.settings.MinBatchSize; i++)
				{
					if (!await this.enumerator.MoveNextAsync().ConfigureAwait(false))
					{
						finished = true;
						break;
					}

					results.Add(this.enumerator.Current);
				}
			}

			JsonRpcValue result = this.manager.WriteResultValue(results, finished, this.elementShape, this.manager.Owner.UserDataSerializer.Encoding);
			if (finished)
			{
				// The consumer is told not to ask again, so it will never send an abort message for this sequence.
				await this.manager.DisposeGeneratorAsync(this.token).ConfigureAwait(false);
			}

			return result;
		}

		/// <inheritdoc/>
		internal override async ValueTask DisposeAsync()
		{
			if (this.disposed)
			{
				return;
			}

			this.disposed = true;
			try
			{
#pragma warning disable VSTHRD103 // CancelAsync is unavailable on all target frameworks.
				this.cancellationSource.Cancel();
#pragma warning restore VSTHRD103
				if (this.readAheadTask is not null)
				{
					// Wait for read ahead to stop touching the enumerator before disposing it.
					await this.readAheadTask.NoThrowAwaitable();
				}

				await this.enumerator.DisposeAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				this.manager.Owner.LogApplicationError(ex);
			}
			finally
			{
				this.ArgumentLifetime?.Dispose();
			}
		}

		/// <summary>Produces values into the read ahead cache until it is full or the sequence ends.</summary>
		/// <returns>A task that completes when production stops.</returns>
		private async Task ReadAheadAsync()
		{
			ChannelWriter<T> writer = this.readAhead!.Writer;
			try
			{
				while (await this.enumerator.MoveNextAsync().ConfigureAwait(false))
				{
					await writer.WriteAsync(this.enumerator.Current, this.cancellationSource.Token).ConfigureAwait(false);
				}

				writer.TryComplete();
			}
			catch (Exception ex)
			{
				writer.TryComplete(ex);
			}
		}
	}

	/// <summary>A sequence whose values are pulled from a remote generator.</summary>
	/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
	private sealed class ConsumerEnumerable<T> : IAsyncEnumerable<T>
	{
		private readonly AsyncEnumerableManager manager;
		private readonly long? token;
		private readonly ITypeShape<T> elementShape;
		private IReadOnlyList<T>? prefetched;
		private bool enumeratorAcquired;
		private CallScopedLifetime? argumentLifetime;

		/// <summary>Initializes a new instance of the <see cref="ConsumerEnumerable{T}"/> class.</summary>
		/// <param name="manager">The owning manager.</param>
		/// <param name="token">The token used to request more values, or <see langword="null"/> if all values arrived already.</param>
		/// <param name="prefetched">The values that arrived with the sequence.</param>
		/// <param name="elementShape">The type shape describing <typeparamref name="T"/>.</param>
		internal ConsumerEnumerable(AsyncEnumerableManager manager, long? token, IReadOnlyList<T> prefetched, ITypeShape<T> elementShape)
		{
			this.manager = manager;
			this.token = token;
			this.prefetched = prefetched;
			this.elementShape = elementShape;
		}

		/// <inheritdoc/>
		public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
		{
			if (this.enumeratorAcquired)
			{
				throw new InvalidOperationException("A remoted IAsyncEnumerable<T> may only be enumerated once.");
			}

			this.enumeratorAcquired = true;
			IReadOnlyList<T> initial = this.prefetched ?? Array.Empty<T>();
			this.prefetched = null;
			return new ConsumerEnumerator<T>(this.manager, this.token, initial, this.elementShape, cancellationToken, Interlocked.Exchange(ref this.argumentLifetime, null));
		}

		/// <summary>Keeps the originating call's arguments alive until this sequence ends.</summary>
		/// <param name="lifetime">The lifetime to retain.</param>
		internal void RetainCallScopedArguments(CallScopedLifetime lifetime) => this.argumentLifetime = lifetime.Retain();
	}

	/// <summary>Walks a remote sequence, requesting batches of values as they are consumed.</summary>
	/// <typeparam name="T">The type of value produced by the sequence.</typeparam>
	private sealed class ConsumerEnumerator<T> : IAsyncEnumerator<T>
	{
		private readonly AsyncEnumerableManager manager;
		private readonly long? token;
		private readonly ITypeShape<T> elementShape;
		private readonly CancellationToken cancellationToken;
		private readonly Queue<T> cached;
		private CallScopedLifetime? argumentLifetime;
		private bool generatorFinished;
		private bool disposed;

		/// <summary>Initializes a new instance of the <see cref="ConsumerEnumerator{T}"/> class.</summary>
		/// <param name="manager">The owning manager.</param>
		/// <param name="token">The token used to request more values, or <see langword="null"/> if all values arrived already.</param>
		/// <param name="prefetched">The values that arrived with the sequence.</param>
		/// <param name="elementShape">The type shape describing <typeparamref name="T"/>.</param>
		/// <param name="cancellationToken">A token to cancel enumeration.</param>
		/// <param name="argumentLifetime">The originating call's argument lease.</param>
		internal ConsumerEnumerator(AsyncEnumerableManager manager, long? token, IReadOnlyList<T> prefetched, ITypeShape<T> elementShape, CancellationToken cancellationToken, CallScopedLifetime? argumentLifetime)
		{
			this.manager = manager;
			this.token = token;
			this.elementShape = elementShape;
			this.cancellationToken = cancellationToken;
			this.argumentLifetime = argumentLifetime;
			this.cached = new Queue<T>(prefetched.Count);
			foreach (T value in prefetched)
			{
				this.cached.Enqueue(value);
			}

			this.generatorFinished = token is null;
			this.Current = default!;
		}

		/// <inheritdoc/>
		public T Current { get; private set; }

		/// <inheritdoc/>
		public async ValueTask DisposeAsync()
		{
			if (this.disposed)
			{
				return;
			}

			this.disposed = true;
			try
			{
				if (!this.generatorFinished && this.token is long activeToken)
				{
					await this.manager.AbortRemoteAsync(activeToken, waitForCompletion: this.argumentLifetime is not null).ConfigureAwait(false);
				}
			}
			finally
			{
				Interlocked.Exchange(ref this.argumentLifetime, null)?.Dispose();
			}
		}

		/// <inheritdoc/>
		public async ValueTask<bool> MoveNextAsync()
		{
			try
			{
				if (this.disposed)
				{
					throw new ObjectDisposedException(nameof(ConsumerEnumerator<T>));
				}

				this.cancellationToken.ThrowIfCancellationRequested();
				while (true)
				{
					if (this.cached.Count > 0)
					{
						this.Current = this.cached.Dequeue();
						return true;
					}

					if (this.generatorFinished || this.token is not long activeToken)
					{
						this.Current = default!;
						return false;
					}

					JsonRpcValue response = await this.manager.RequestNextValuesAsync(activeToken, this.cancellationToken).ConfigureAwait(false);
					(List<JsonRpcValue> values, bool finished) = ReadResultValue(response);
					this.generatorFinished = finished;
					using (InboundScope inboundScope = this.manager.TrackInboundRequest(hasResponse: true))
					{
						foreach (JsonRpcValue value in values)
						{
							this.cached.Enqueue(this.manager.Owner.UserDataSerializer.Deserialize(value, this.elementShape, this.cancellationToken));
						}

						inboundScope.RetainCallScopedArguments(this.argumentLifetime);
					}

					if (finished)
					{
						Interlocked.Exchange(ref this.argumentLifetime, null)?.Dispose();
					}

					if (this.cached.Count == 0)
					{
						this.Current = default!;
						return false;
					}
				}
			}
			catch
			{
				await this.DisposeAsync().ConfigureAwait(false);
				throw;
			}
		}
	}
}
