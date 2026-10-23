// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Reports the remote contract interfaces enabled on a client proxy.</summary>
/// <remarks>
/// Generated root and RPC-marshalable proxies implement this interface. Use it instead of a CLR type test
/// when a proxy may implement interfaces that are not enabled on the remote object.
/// Capability queries are local and do not contact the remote endpoint or prevent direct method calls.
/// </remarks>
public interface IClientProxy
{
	/// <summary>Gets a value indicating whether a contract interface is enabled on this proxy.</summary>
	/// <param name="type">The contract interface type to query.</param>
	/// <returns><see langword="true"/> if the proxy implements the interface and it is enabled; otherwise <see langword="false"/>.</returns>
	/// <remarks>
	/// The primary contract and its inherited interfaces are always enabled on generated proxies.
	/// Enabling an additional interface also enables its inherited interfaces. Non-interface types return <see langword="false"/>.
	/// </remarks>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="type"/> is <see langword="null"/>.</exception>
	bool Is(Type type);
}
