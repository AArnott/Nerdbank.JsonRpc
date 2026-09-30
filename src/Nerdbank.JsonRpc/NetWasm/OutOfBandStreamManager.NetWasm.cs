// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETWASM

namespace Nerdbank.JsonRpc;

/// <summary>
/// A NetWasm stand-in for the out-of-band stream manager.
/// </summary>
/// <remarks>
/// Out-of-band streams require Nerdbank.Streams' MultiplexingStream, which has not been ported to NetWasm.
/// This stub keeps the call-tracking plumbing compiling while never producing any channels.
/// </remarks>
internal sealed class OutOfBandStreamManager : IDisposable
{
	public void Dispose()
	{
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

	internal void RegisterOutboundRequest(JsonRpcRequest request)
	{
	}

	internal void CompleteOutboundRequest(RequestId id, bool successful)
	{
	}

	internal void TrackActiveChannels(ChannelSet channels)
	{
	}

	internal void EnsureNoOutOfBandChannels(JsonRpcValue arguments)
	{
	}

	internal readonly struct InboundScope(RpcCallState callState) : IDisposable
	{
		public void Dispose()
		{
			callState.OutOfBandStreamsInbound = null;
			callState.Undeclare(RpcCallState.Scopes.OutOfBandStreamsInbound);
		}

		internal void Complete(bool successful)
		{
		}
	}

	internal readonly struct OutboundScope(RpcCallState? callState) : IDisposable
	{
		public void Dispose()
		{
			if (callState is not null)
			{
				callState.OutOfBandStreamsOutbound = null;
				callState.Undeclare(RpcCallState.Scopes.OutOfBandStreamsOutbound);
			}
		}

		internal ChannelSet Commit() => ChannelSet.Empty;
	}

	internal sealed class OutboundScopeState
	{
	}

	internal sealed class InboundScopeState
	{
	}

	internal sealed class ChannelSet : IDisposable
	{
		internal static readonly ChannelSet Empty = new();

		internal bool IsEmpty => true;

		public void Dispose()
		{
		}
	}
}

#endif
