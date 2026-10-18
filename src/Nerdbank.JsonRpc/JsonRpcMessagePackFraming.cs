// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Defines the framing of MessagePack-encoded JSON-RPC messages.</summary>
public enum JsonRpcMessagePackFraming
{
	/// <summary>
	/// Each message is a single self-delimiting MessagePack structure with no header.
	/// The receiver must scan each structure to find where it ends before deserializing it.
	/// </summary>
	SelfDelimiting,

	/// <summary>
	/// Each message is preceded by its length in bytes, encoded as a 4-byte big-endian unsigned integer.
	/// This is compatible with StreamJsonRpc's <c>LengthHeaderMessageHandler</c>.
	/// </summary>
	BigEndianInt32LengthHeader,
}
