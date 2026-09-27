// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

internal sealed class OutOfBandStreamManager(JsonRpc owner) : IDisposable
{
	private readonly object sync = new();
	private readonly Dictionary<RequestId, ChannelSet> outboundChannels = [];
	private readonly AsyncLocal<OutboundScope?> activeOutboundScope = new();
	private readonly AsyncLocal<InboundScope?> activeInboundScope = new();

	internal MultiplexingStream? MultiplexingStream { get; set; }

	internal OutboundScope TrackOutboundRequest() => new(this);

	internal InboundScope TrackInboundRequest(bool hasResponse) => new(this, hasResponse);

	internal JsonRpcValue Marshal(IDuplexPipe pipe, JsonRpcEncoding encoding)
	{
		OutboundScope scope = this.activeOutboundScope.Value ?? throw new InvalidOperationException("Out-of-band streams may only be sent in RPC requests.");
		MultiplexingStream multiplexingStream = this.MultiplexingStream ?? throw new NotSupportedException("Out-of-band streams require a configured MultiplexingStream.");
		MultiplexingStream.Channel channel = multiplexingStream.CreateChannel(new() { ExistingPipe = pipe });
		scope.Add(channel);
		return CreateToken(channel.QualifiedId.Id, encoding);
	}

	internal IDuplexPipe Unmarshal(JsonRpcValue token)
	{
		InboundScope scope = this.activeInboundScope.Value ?? throw new FormatException("Out-of-band streams may only be received in RPC request arguments.");
		if (!scope.HasResponse)
		{
			throw new FormatException("Out-of-band streams cannot be received in notifications.");
		}

		MultiplexingStream multiplexingStream = this.MultiplexingStream ?? throw new NotSupportedException("Out-of-band streams require a configured MultiplexingStream.");
		MultiplexingStream.Channel channel = multiplexingStream.AcceptChannel(ReadToken(token));
		scope.Add(channel);
		return channel;
	}

	internal void RegisterOutboundRequest(JsonRpcRequest request)
	{
		if (request.Id is not RequestId id || request.Arguments.OutOfBandChannels is not ChannelSet channels || channels.IsEmpty)
		{
			return;
		}

		lock (this.sync)
		{
			this.outboundChannels.Add(id, channels);
		}
	}

	internal void CompleteOutboundRequest(RequestId id, bool successful)
	{
		ChannelSet? channels;
		lock (this.sync)
		{
			if (!this.outboundChannels.TryGetValue(id, out channels))
			{
				return;
			}

			this.outboundChannels.Remove(id);
		}

		if (!successful)
		{
			channels.Dispose();
		}
	}

	internal void EnsureNoOutOfBandChannels(JsonRpcValue arguments)
	{
		if (arguments.OutOfBandChannels is ChannelSet { IsEmpty: false })
		{
			throw new InvalidOperationException("Out-of-band streams cannot be sent in notifications.");
		}
	}

	public void Dispose()
	{
		ChannelSet[] channels;
		lock (this.sync)
		{
			channels = [.. this.outboundChannels.Values];
			this.outboundChannels.Clear();
		}

		foreach (ChannelSet set in channels)
		{
			set.Dispose();
		}
	}

	private static JsonRpcValue CreateToken(ulong token, JsonRpcEncoding encoding)
	{
		if (encoding == JsonRpcEncoding.Json)
		{
			return JsonRpcValue.FromJson(System.Text.Encoding.UTF8.GetBytes(token.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}

		using Sequence<byte> buffer = new();
		MessagePackWriter writer = new(buffer);
		writer.Write(token);
		writer.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)buffer.AsReadOnlySequence.ToArray());
	}

	private static ulong ReadToken(JsonRpcValue token)
	{
		if (token.Encoding == JsonRpcEncoding.Json)
		{
			using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(token.OwnedBytes);
			return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Number && document.RootElement.TryGetUInt64(out ulong value)
				? value
				: throw new FormatException("Out-of-band stream token must be an unsigned integer.");
		}

		MessagePackReader reader = new(token.AsMessagePack());
		ulong tokenValue = reader.ReadUInt64();
		return reader.End ? tokenValue : throw new FormatException("Out-of-band stream token must be an unsigned integer.");
	}

	internal sealed class OutboundScope : IDisposable
	{
		private readonly OutOfBandStreamManager manager;
		private readonly OutboundScope? priorScope;
		private readonly List<MultiplexingStream.Channel> channels = [];
		private bool committed;

		internal OutboundScope(OutOfBandStreamManager manager)
		{
			this.manager = manager;
			this.priorScope = manager.activeOutboundScope.Value;
			manager.activeOutboundScope.Value = this;
		}

		internal void Add(MultiplexingStream.Channel channel) => this.channels.Add(channel);

		internal ChannelSet Commit()
		{
			this.committed = true;
			return new([.. this.channels]);
		}

		public void Dispose()
		{
			this.manager.activeOutboundScope.Value = this.priorScope;
			if (!this.committed)
			{
				new ChannelSet([.. this.channels]).Dispose();
			}
		}
	}

	internal sealed class InboundScope : IDisposable
	{
		private readonly OutOfBandStreamManager manager;
		private readonly InboundScope? priorScope;
		private readonly List<MultiplexingStream.Channel> channels = [];
		private bool completed;

		internal InboundScope(OutOfBandStreamManager manager, bool hasResponse)
		{
			this.manager = manager;
			this.HasResponse = hasResponse;
			this.priorScope = manager.activeInboundScope.Value;
			manager.activeInboundScope.Value = this;
		}

		internal bool HasResponse { get; }

		internal void Add(MultiplexingStream.Channel channel) => this.channels.Add(channel);

		internal void Complete(bool successful)
		{
			this.completed = true;
			if (!successful)
			{
				new ChannelSet([.. this.channels]).Dispose();
			}
		}

		public void Dispose()
		{
			this.manager.activeInboundScope.Value = this.priorScope;
			if (!this.completed)
			{
				new ChannelSet([.. this.channels]).Dispose();
			}
		}
	}

	internal sealed class ChannelSet(MultiplexingStream.Channel[] channels) : IDisposable
	{
		internal bool IsEmpty => channels.Length == 0;

		public void Dispose()
		{
			foreach (MultiplexingStream.Channel channel in channels)
			{
				channel.Dispose();
			}
		}
	}
}
