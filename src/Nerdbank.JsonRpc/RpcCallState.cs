// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Carries the scopes that apply to one serialization or deserialization job.</summary>
/// <remarks>
/// <para>
/// Converters that marshal objects, progress reporters, out-of-band streams and async enumerables need to reach the
/// scope established by the RPC call that is currently being encoded or decoded.
/// </para>
/// <para>
/// A (de)serialization job runs to completion synchronously on the thread that starts it, so the job's state is
/// published in a thread-static slot for the duration of that call. Concurrent calls therefore remain isolated
/// without the execution-context copying that ambient storage requires, and without the per-job state bag
/// allocation that publishing through the serializer's context would incur.
/// </para>
/// <para>
/// The owner of a call declares which scopes apply to it, but the state behind each scope is created only when a
/// converter first needs it. Most messages carry no marshaled objects, progress reporters, streams or sequences, so
/// they allocate no scope state at all.
/// </para>
/// </remarks>
internal sealed class RpcCallState
{
	[ThreadStatic]
	private static RpcCallState? current;

	/// <summary>Identifies the scopes an owner has declared for a call.</summary>
	[Flags]
	internal enum Scopes : ushort
	{
		/// <summary>No scopes.</summary>
		None = 0,

		/// <summary>Objects may be marshaled into the message being written.</summary>
		MarshaledObjects = 0x1,

		/// <summary>Objects marshaled into the message being written may have a call-scoped lifetime.</summary>
		CallScopedLifetimeAllowed = 0x2,

		/// <summary>Progress reporters may be sent in the message being written.</summary>
		ProgressRegistrations = 0x4,

		/// <summary>Out-of-band streams may be sent in the message being written.</summary>
		OutOfBandStreamsOutbound = 0x8,

		/// <summary>Sequences may be sent in the message being written.</summary>
		AsyncEnumerablesOutbound = 0x10,

		/// <summary>Marshaled objects may be received in the message being read.</summary>
		InboundCall = 0x20,

		/// <summary>Progress reporters may be received in the message being read.</summary>
		ProgressInbound = 0x40,

		/// <summary>Out-of-band streams may be received in the message being read.</summary>
		OutOfBandStreamsInbound = 0x80,

		/// <summary>Sequences may be received in the message being read.</summary>
		AsyncEnumerablesInbound = 0x100,

		/// <summary>The message being read is a request that will receive a response.</summary>
		HasResponse = 0x200,
	}

	/// <summary>Gets the state of the (de)serialization job running on this thread, if any.</summary>
	internal static RpcCallState? Current => current;

	/// <summary>Gets the scopes declared for this call.</summary>
	internal Scopes Declared { get; private set; }

	/// <summary>Gets a value indicating whether the message being read will receive a response.</summary>
	internal bool HasResponse => (this.Declared & Scopes.HasResponse) != 0;

	/// <summary>Gets or sets the objects marshaled into the message being written, once any have been.</summary>
	internal MarshaledObjectManager.HandleScopeState? MarshaledObjects { get; set; }

	/// <summary>Gets or sets the proxies received in the message being read, once any have been.</summary>
	internal MarshaledObjectManager.InboundCallScopeState? InboundCall { get; set; }

	/// <summary>Gets or sets the progress registrations created while writing the message, once any have been.</summary>
	internal ProgressManager.RegistrationScopeState? ProgressRegistrations { get; set; }

	/// <summary>Gets or sets the state shared by progress proxies created while reading the message, once any have been.</summary>
	internal ProgressManager.InboundScopeState? ProgressInbound { get; set; }

	/// <summary>Gets or sets the out-of-band channels offered by the message being written, once any have been.</summary>
	internal OutOfBandStreamManager.OutboundScopeState? OutOfBandStreamsOutbound { get; set; }

	/// <summary>Gets or sets the out-of-band channels accepted from the message being read, once any have been.</summary>
	internal OutOfBandStreamManager.InboundScopeState? OutOfBandStreamsInbound { get; set; }

	/// <summary>Gets or sets the sequence generators created while writing the message, once any have been.</summary>
	internal AsyncEnumerableManager.OutboundScopeState? AsyncEnumerablesOutbound { get; set; }

	/// <summary>Gets or sets the sequence consumers created while reading the message, once any have been.</summary>
	internal AsyncEnumerableManager.InboundScopeState? AsyncEnumerablesInbound { get; set; }

	/// <summary>Gets the arguments retained by sequence generators created while writing the message.</summary>
	internal CallScopedLifetime? AsyncEnumerableArgumentLifetime { get; private set; }

	/// <summary>Publishes state to the converters taking part in one (de)serialization job.</summary>
	/// <param name="callState">The state to publish, if any.</param>
	/// <returns>A frame that must be disposed when the job completes.</returns>
	/// <remarks>
	/// A converter may encode a nested value with its own state; the prior state is restored when the nested job's
	/// frame is disposed.
	/// </remarks>
	internal static Frame Enter(RpcCallState? callState)
	{
		RpcCallState? prior = current;
		current = callState;
		return new(prior);
	}

	/// <summary>Declares scopes for this call.</summary>
	/// <param name="scopes">The scopes to declare, which must not already be declared.</param>
	internal void Declare(Scopes scopes)
	{
		const Scopes Modifiers = Scopes.CallScopedLifetimeAllowed | Scopes.HasResponse;
		if ((this.Declared & scopes & ~Modifiers) != 0)
		{
			throw new InvalidOperationException("A scope was declared twice for the same call.");
		}

		this.Declared |= scopes;
	}

	/// <summary>Declares an inbound scope for this call.</summary>
	/// <param name="scope">The inbound scope to declare.</param>
	/// <param name="hasResponse">Whether the message being read will receive a response.</param>
	internal void DeclareInbound(Scopes scope, bool hasResponse)
	{
		const Scopes Inbound = Scopes.InboundCall | Scopes.ProgressInbound | Scopes.OutOfBandStreamsInbound | Scopes.AsyncEnumerablesInbound;
		if ((this.Declared & Inbound) != 0 && this.HasResponse != hasResponse)
		{
			throw new InvalidOperationException("Inbound scopes for the same call disagree on whether it receives a response.");
		}

		this.Declare(hasResponse ? scope | Scopes.HasResponse : scope);
	}

	/// <summary>Declares the scope for sequences sent in the message being written.</summary>
	/// <param name="argumentLifetime">The arguments retained by generators created in this scope.</param>
	internal void DeclareAsyncEnumerablesOutbound(CallScopedLifetime? argumentLifetime)
	{
		this.Declare(Scopes.AsyncEnumerablesOutbound);
		this.AsyncEnumerableArgumentLifetime = argumentLifetime;
	}

	/// <summary>Ends previously declared scopes.</summary>
	/// <param name="scopes">The scopes to end.</param>
	internal void Undeclare(Scopes scopes)
	{
		this.Declared &= ~scopes;
		if ((scopes & Scopes.AsyncEnumerablesOutbound) != 0)
		{
			this.AsyncEnumerableArgumentLifetime = null;
		}
	}

	/// <summary>Checks whether all the specified scopes are declared.</summary>
	/// <param name="scopes">The scopes to test.</param>
	/// <returns><see langword="true"/> if every scope in <paramref name="scopes"/> is declared.</returns>
	internal bool IsDeclared(Scopes scopes) => (this.Declared & scopes) == scopes;

	/// <summary>Checks whether an inbound scope is declared for a message that will receive no response.</summary>
	/// <param name="scope">The inbound scope to test.</param>
	/// <returns><see langword="true"/> if <paramref name="scope"/> is declared and the message is a notification.</returns>
	internal bool IsDeclaredForNotification(Scopes scope) => this.IsDeclared(scope) && !this.HasResponse;

	/// <summary>Restores the state that was current before a (de)serialization job began.</summary>
	/// <param name="prior">The state to restore.</param>
	internal readonly struct Frame(RpcCallState? prior) : IDisposable
	{
		/// <inheritdoc/>
		public void Dispose() => current = prior;
	}
}
