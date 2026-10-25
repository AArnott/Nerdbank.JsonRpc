// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Identifies supported service connection transports.</summary>
[Flags]
public enum RemoteServiceConnections
{
	/// <summary>No supported transport.</summary>
	None = 0,

	/// <summary>Same-account local IPC.</summary>
	IpcPipe = 1,

	/// <summary>Channels on an existing multiplexing stream.</summary>
	Multiplexing = 2,
}
