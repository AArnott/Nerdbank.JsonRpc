// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Text;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

internal sealed class ProgressManager(JsonRpc owner)
{
	private const string ProgressMethod = "$/progress";
	private readonly object sync = new();
	private readonly Dictionary<JsonRpcValue, Registration> registrations = [];
	private readonly Dictionary<RequestId, RegistrationSet> requestRegistrations = [];
	private readonly AsyncLocal<RegistrationScope?> activeRegistrationScope = new();
	private readonly AsyncLocal<InboundScope?> activeInboundScope = new();
	private long nextToken;

	internal RegistrationScope TrackRegistrations() => new(this);

	internal JsonRpcValue Marshal<T>(IProgress<T> progress, ITypeShape<T> valueShape, JsonRpcEncoding encoding)
	{
		RegistrationScope scope = this.activeRegistrationScope.Value ?? throw new InvalidOperationException("IProgress<T> values may only be sent as RPC request arguments.");
		JsonRpcValue token = this.CreateToken(encoding);
		scope.Add(token, new Contract<T>(progress, valueShape));
		return token;
	}

	internal IProgress<T> Unmarshal<T>(JsonRpcValue token, ITypeShape<T> valueShape)
	{
		InboundScope scope = this.activeInboundScope.Value ?? throw new FormatException("IProgress<T> values may only be received in RPC request arguments.");
		if (!scope.HasResponse)
		{
			throw new FormatException("IProgress<T> values cannot be received in notifications.");
		}

		return new ProgressProxy<T>(owner, token, valueShape, scope);
	}

	internal InboundScope TrackInboundCall(bool hasResponse) => new(this, hasResponse);

	internal void RegisterOutboundRequest(JsonRpcRequest request)
	{
		if (request.Id is not RequestId || request.Arguments.ProgressRegistrations is not RegistrationSet set)
		{
			return;
		}

		lock (this.sync)
		{
			this.requestRegistrations.Add(request.Id!.Value, set);
			foreach ((JsonRpcValue token, Registration registration) in set.Registrations)
			{
				this.registrations.Add(token, registration);
			}
		}
	}

	internal void UnregisterOutboundRequest(JsonRpcRequest request)
	{
		if (request.Id is RequestId id)
		{
			this.UnregisterOutboundRequest(id);
		}
	}

	internal void UnregisterOutboundRequest(RequestId id)
	{
		RegistrationSet? set;
		lock (this.sync)
		{
			if (!this.requestRegistrations.TryGetValue(id, out set))
			{
				return;
			}

			this.requestRegistrations.Remove(id);

			foreach ((JsonRpcValue token, Registration registration) in set.Registrations)
			{
				if (this.registrations.TryGetValue(token, out Registration? registered) && ReferenceEquals(registered, registration))
				{
					this.registrations.Remove(token);
				}
			}
		}
	}

	internal bool TryHandleNotification(JsonRpcRequest request)
	{
		if (request.Method != ProgressMethod)
		{
			return false;
		}

		try
		{
			(bool named, List<(string? Name, JsonRpcValue Value)> values) = owner.UserDataSerializer.ReadArguments(request.Arguments);
			if (values.Count != 2)
			{
				throw new FormatException("Progress notifications must include a token and a value parameter.");
			}

			JsonRpcValue? token = null;
			JsonRpcValue? value = null;
			if (named)
			{
				foreach ((string? name, JsonRpcValue parameter) in values)
				{
					switch (name)
					{
						case "token" when token is null:
							token = parameter;
							break;
						case "value" when value is null:
							value = parameter;
							break;
						default:
							throw new FormatException("Progress notifications must include one token and one value parameter.");
					}
				}
			}
			else
			{
				token = values[0].Value;
				value = values[1].Value;
			}

			if (token is null || value is null)
			{
				throw new FormatException("Progress notifications must include token and value parameters.");
			}

			Registration? registration;
			lock (this.sync)
			{
				this.registrations.TryGetValue(token.Value, out registration);
			}

			registration?.Report(owner.UserDataSerializer, value.Value, owner.DisposalToken);
		}
		catch (Exception ex)
		{
			owner.LogApplicationError(ex);
		}

		return true;
	}

	internal void EnsureNoProgressRegistrations(JsonRpcValue arguments)
	{
		if (arguments.ProgressRegistrations is RegistrationSet { IsEmpty: false })
		{
			throw new InvalidOperationException("IProgress<T> values cannot be sent in notifications.");
		}
	}

	private ValueTask PostProgressAsync(JsonRpcRequest notification) => owner.PostMessageAsync(notification);

	private JsonRpcValue CreateToken(JsonRpcEncoding encoding)
	{
		long token = Interlocked.Increment(ref this.nextToken);
		if (encoding == JsonRpcEncoding.Json)
		{
			return JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(token.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}

		using Sequence<byte> buffer = new();
		MessagePackWriter writer = new(buffer);
		writer.Write(token);
		writer.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)buffer.AsReadOnlySequence.ToArray());
	}

	internal sealed class RegistrationScope : IDisposable
	{
		private readonly ProgressManager manager;
		private readonly RegistrationScope? priorScope;
		private readonly List<(JsonRpcValue Token, Registration Registration)> registrations = [];
		private bool committed;

		internal RegistrationScope(ProgressManager manager)
		{
			this.manager = manager;
			this.priorScope = manager.activeRegistrationScope.Value;
			manager.activeRegistrationScope.Value = this;
		}

		public RegistrationSet Commit()
		{
			this.committed = true;
			return new([.. this.registrations]);
		}

		public void Dispose()
		{
			this.manager.activeRegistrationScope.Value = this.priorScope;
			if (!this.committed)
			{
				this.registrations.Clear();
			}
		}

		internal void Add(JsonRpcValue token, Registration registration) => this.registrations.Add((token, registration));
	}

	internal sealed class RegistrationSet
	{
		private readonly (JsonRpcValue Token, Registration Registration)[] registrations;

		internal RegistrationSet((JsonRpcValue Token, Registration Registration)[] registrations) => this.registrations = registrations;

		internal IReadOnlyList<(JsonRpcValue Token, Registration Registration)> Registrations => this.registrations;

		internal bool IsEmpty => this.registrations.Length == 0;
	}

#pragma warning disable VSTHRD003 // The queue is built exclusively by this scope.
	internal sealed class InboundScope : IDisposable
	{
		private readonly ProgressManager manager;
		private readonly InboundScope? priorScope;
		private readonly object sync = new();
		private Task reportsQueued = Task.CompletedTask;
		private bool active = true;

		internal InboundScope(ProgressManager manager, bool hasResponse)
		{
			this.manager = manager;
			this.HasResponse = hasResponse;
			this.priorScope = manager.activeInboundScope.Value;
			manager.activeInboundScope.Value = this;
		}

		internal bool HasResponse { get; }

		public void Dispose()
		{
			this.manager.activeInboundScope.Value = this.priorScope;
			_ = this.CompleteAsync();
		}

		internal void Report(Func<JsonRpcRequest> createNotification)
		{
			lock (this.sync)
			{
				if (!this.active)
				{
					return;
				}

				JsonRpcRequest notification = createNotification();
				this.reportsQueued = this.PostAfterAsync(this.reportsQueued, notification);
			}
		}

		internal Task CompleteAsync()
		{
			lock (this.sync)
			{
				this.active = false;
				return this.reportsQueued;
			}
		}

		private async Task PostAfterAsync(Task priorReport, JsonRpcRequest notification)
		{
			await priorReport.ConfigureAwait(false);
			await this.manager.PostProgressAsync(notification).ConfigureAwait(false);
		}
	}

#pragma warning restore VSTHRD003

	internal abstract class Registration
	{
		internal abstract void Report(JsonRpcSerializer serializer, JsonRpcValue value, CancellationToken cancellationToken);
	}

	private sealed class Contract<T>(IProgress<T> progress, ITypeShape<T> valueShape) : Registration
	{
		internal override void Report(JsonRpcSerializer serializer, JsonRpcValue value, CancellationToken cancellationToken)
			=> progress.Report(serializer.Deserialize(value, valueShape, cancellationToken));
	}

	private sealed class ProgressProxy<T>(JsonRpc owner, JsonRpcValue token, ITypeShape<T> valueShape, InboundScope scope) : IProgress<T>
	{
		public void Report(T value)
		{
			scope.Report(() =>
			{
				JsonRpcValue serializedValue = owner.UserDataSerializer.Serialize(value, valueShape, owner.DisposalToken);
				owner.MarshaledObjects.EnsureNoMarshaledObjects(serializedValue);
				return new JsonRpcRequest { Method = ProgressMethod, Arguments = CreateArguments(token, serializedValue) };
			});
		}

		private static JsonRpcValue CreateArguments(JsonRpcValue token, JsonRpcValue value)
		{
			if (token.Encoding == JsonRpcEncoding.Json)
			{
				using Sequence<byte> buffer = new();
				Write(buffer, "{\"token\":");
				Write(buffer, token.OwnedBytes.Span);
				Write(buffer, ",\"value\":");
				Write(buffer, value.OwnedBytes.Span);
				Write(buffer, "}");
				return JsonRpcValue.FromJson(buffer.AsReadOnlySequence.ToArray());
			}

			using Sequence<byte> messagePackBuffer = new();
			MessagePackWriter writer = new(messagePackBuffer);
			writer.WriteMapHeader(2);
			writer.Write("token");
			writer.Write(token.AsMessagePack());
			writer.Write("value");
			writer.Write(value.AsMessagePack());
			writer.Flush();
			return JsonRpcValue.FromMessagePack((RawMessagePack)messagePackBuffer.AsReadOnlySequence.ToArray());
		}

		private static void Write(IBufferWriter<byte> buffer, string value) => Write(buffer, Encoding.UTF8.GetBytes(value));

		private static void Write(IBufferWriter<byte> buffer, ReadOnlySpan<byte> value)
		{
			value.CopyTo(buffer.GetSpan(value.Length));
			buffer.Advance(value.Length);
		}
	}
}
