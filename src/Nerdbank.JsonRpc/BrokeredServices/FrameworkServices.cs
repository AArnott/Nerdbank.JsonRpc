// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Describes broker infrastructure contracts shared by both peers.</summary>
public static class FrameworkServices
{
	/// <summary>Gets the broker control contract, using MessagePack with length-prefixed framing.</summary>
	public static JsonRpcMessagePackDescriptor<IRemoteServiceBroker> RemoteServiceBroker { get; } = new(
		new("Nerdbank.JsonRpc.RemoteServiceBroker", new Version(1, 0)),
		PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc.Default.IRemoteServiceBroker);
}
