// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;
using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

internal static class MsgPackValues
{
	internal static readonly RawMessagePack EmptyMap = CreateEmptyMap();

	internal static readonly RawMessagePack EmptyArray = CreateEmptyArray();

	private static RawMessagePack CreateEmptyMap()
	{
		byte[] buffer = new byte[1];
		Assumes.True(MessagePackPrimitives.TryWriteMapHeader(buffer, 0, out _));
		return (RawMessagePack)buffer;
	}

	private static RawMessagePack CreateEmptyArray()
	{
		byte[] buffer = new byte[1];
		Assumes.True(MessagePackPrimitives.TryWriteArrayHeader(buffer, 0, out _));
		return (RawMessagePack)buffer;
	}
}
