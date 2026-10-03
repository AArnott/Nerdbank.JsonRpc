// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

internal sealed class OutOfBandStreamManager : IDisposable
{
	private readonly object sync = new();
	private readonly Dictionary<RequestId, ChannelSet> outboundChannels = [];
	private readonly List<ChannelSet> activeChannels = [];
	private bool disposed;

	internal MultiplexingStream? MultiplexingStream { get; set; }

	public void Dispose()
	{
		ChannelSet[] channels;
		lock (this.sync)
		{
			this.disposed = true;
			channels = [.. this.outboundChannels.Values, .. this.activeChannels];
			this.outboundChannels.Clear();
			this.activeChannels.Clear();
		}

		List<Exception>? failures = null;
		foreach (ChannelSet set in channels)
		{
			try
			{
				set.Dispose();
			}
			catch (Exception ex)
			{
				(failures ??= []).Add(ex);
			}
		}

		if (failures is not null)
		{
			throw new AggregateException("Out-of-band stream cleanup failed.", failures);
		}
	}

	internal OutboundScope TrackOutboundRequest(RpcCallState callState)
	{
		callState.Declare(RpcCallState.Scopes.OutOfBandStreamsOutbound);
		return new(callState);
	}

	internal InboundScope TrackInboundRequest(bool hasResponse, RpcCallState callState)
	{
		callState.DeclareInbound(RpcCallState.Scopes.OutOfBandStreamsInbound, hasResponse);
		return new(callState);
	}

	internal JsonRpcValue Marshal(IDuplexPipe pipe, JsonRpcEncoding encoding, RpcCallState? callState)
	{
		if (callState?.IsDeclared(RpcCallState.Scopes.OutOfBandStreamsOutbound) is not true)
		{
			throw new InvalidOperationException("Out-of-band streams may only be sent in RPC requests.");
		}

		lock (this.sync)
		{
			if (this.disposed)
			{
				throw new ObjectDisposedException(nameof(JsonRpc));
			}
		}

		MultiplexingStream multiplexingStream = this.MultiplexingStream ?? throw new NotSupportedException("Out-of-band streams require a configured MultiplexingStream.");
		MultiplexingStream.Channel channel = multiplexingStream.CreateChannel(new() { ExistingPipe = pipe });
		(callState.OutOfBandStreamsOutbound ??= new()).Add(channel);
		return CreateToken(channel.QualifiedId.Id, encoding);
	}

	internal IDuplexPipe Unmarshal(JsonRpcValue token, RpcCallState? callState)
	{
		if (callState?.IsDeclared(RpcCallState.Scopes.OutOfBandStreamsInbound) is not true)
		{
			throw new FormatException("Out-of-band streams may only be received in RPC request arguments.");
		}

		if (!callState.HasResponse)
		{
			throw new FormatException("Out-of-band streams cannot be received in notifications.");
		}

		MultiplexingStream multiplexingStream = this.MultiplexingStream ?? throw new NotSupportedException("Out-of-band streams require a configured MultiplexingStream.");
		MultiplexingStream.Channel channel = multiplexingStream.AcceptChannel(ReadToken(token));
		(callState.OutOfBandStreamsInbound ??= new(this)).Add(channel);
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
			if (successful)
			{
				this.activeChannels.Add(channels);
				return;
			}
		}

		channels.Dispose();
	}

	/// <summary>Keeps a set of channels alive (and disposes them when this connection is disposed) after ownership has successfully transferred to a peer.</summary>
	/// <param name="channels">The channels to track.</param>
	internal void TrackActiveChannels(ChannelSet channels)
	{
		if (channels.IsEmpty)
		{
			return;
		}

		bool reject;
		lock (this.sync)
		{
			reject = this.disposed;
			if (!reject)
			{
				this.activeChannels.Add(channels);
			}
		}

		if (reject)
		{
			channels.Dispose();
		}
	}

	internal void ReleaseChannels(JsonRpcValue value) => value.OutOfBandChannels?.Dispose();

	internal void EnsureNoOutOfBandChannels(JsonRpcValue arguments)
	{
		if (arguments.OutOfBandChannels is ChannelSet { IsEmpty: false })
		{
			throw new InvalidOperationException("Out-of-band streams cannot be sent in notifications.");
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
		return JsonRpcValue.FromOwnedBytes(buffer.AsReadOnlySequence.ToArray(), JsonRpcEncoding.MessagePack);
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

		MessagePackReader reader = new(token.AsOwnedMessagePack());
		ulong tokenValue = reader.ReadUInt64();
		return reader.End ? tokenValue : throw new FormatException("Out-of-band stream token must be an unsigned integer.");
	}

	/// <summary>Declares that out-of-band streams may be received in one inbound message.</summary>
	/// <remarks>State is created only when a stream is actually received.</remarks>
	internal readonly struct InboundScope(RpcCallState callState) : IDisposable
	{
		public void Dispose()
		{
			callState.OutOfBandStreamsInbound?.Dispose();
			callState.OutOfBandStreamsInbound = null;
			callState.Undeclare(RpcCallState.Scopes.OutOfBandStreamsInbound);
		}

		internal void Complete(bool successful) => callState.OutOfBandStreamsInbound?.Complete(successful);
	}

	/// <summary>Declares that out-of-band streams may be sent in one outbound message.</summary>
	/// <remarks>State is created only when a stream is actually sent.</remarks>
	internal readonly struct OutboundScope(RpcCallState? callState) : IDisposable
	{
		public void Dispose()
		{
			if (callState is not null)
			{
				callState.OutOfBandStreamsOutbound?.Dispose();
				callState.OutOfBandStreamsOutbound = null;
				callState.Undeclare(RpcCallState.Scopes.OutOfBandStreamsOutbound);
			}
		}

		internal ChannelSet Commit() => callState?.OutOfBandStreamsOutbound?.Commit() ?? ChannelSet.Empty;
	}

	internal sealed class OutboundScopeState
	{
		private List<MultiplexingStream.Channel>? channels;
		private bool committed;

		internal void Dispose()
		{
			if (!this.committed)
			{
				new ChannelSet(this.channels?.ToArray() ?? []).Dispose();
			}
		}

		internal void Add(MultiplexingStream.Channel channel) => (this.channels ??= []).Add(channel);

		internal ChannelSet Commit()
		{
			this.committed = true;
			return this.channels is { Count: > 0 } channels ? new([.. channels]) : ChannelSet.Empty;
		}
	}

	internal sealed class InboundScopeState(OutOfBandStreamManager manager)
	{
		private List<MultiplexingStream.Channel>? channels;
		private bool completed;

		internal void Dispose()
		{
			if (!this.completed)
			{
				new ChannelSet(this.channels?.ToArray() ?? []).Dispose();
			}
		}

		internal void Add(MultiplexingStream.Channel channel) => (this.channels ??= []).Add(channel);

		internal void Complete(bool successful)
		{
			this.completed = true;
			ChannelSet set = this.channels is { Count: > 0 } channels ? new([.. channels]) : ChannelSet.Empty;
			if (successful)
			{
				manager.TrackActiveChannels(set);
			}
			else
			{
				set.Dispose();
			}
		}
	}

	internal sealed class ChannelSet(MultiplexingStream.Channel[] channels) : IDisposable
	{
		internal static readonly ChannelSet Empty = new([]);
		private int disposed;

		internal bool IsEmpty => channels.Length == 0;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref this.disposed, 1) != 0)
			{
				return;
			}

			List<Exception>? failures = null;
			foreach (MultiplexingStream.Channel channel in channels)
			{
				try
				{
					channel.Dispose();
				}
				catch (Exception ex)
				{
					(failures ??= []).Add(ex);
				}
			}

			if (failures is not null)
			{
				throw new AggregateException("Out-of-band channel cleanup failed.", failures);
			}
		}
	}
}
