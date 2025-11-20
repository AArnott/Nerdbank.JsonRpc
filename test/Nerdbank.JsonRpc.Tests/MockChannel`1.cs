// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Channels;

namespace Nerdbank.JsonRpc.Tests;

internal class MockChannel<T> : Channel<T>
{
	internal MockChannel(ChannelReader<T> reader, ChannelWriter<T> writer)
	{
		this.Reader = reader;
		this.Writer = writer;
	}

#pragma warning disable SA1414 // Tuple types in signatures should have element names
	internal static (MockChannel<T>, MockChannel<T>) CreatePair()
#pragma warning restore SA1414 // Tuple types in signatures should have element names
	{
		Channel<T> channel1 = Channel.CreateUnbounded<T>();
		Channel<T> channel2 = Channel.CreateUnbounded<T>();
		MockChannel<T> mockChannel1 = new(channel1.Reader, channel2.Writer);
		MockChannel<T> mockChannel2 = new(channel2.Reader, channel1.Writer);
		return (mockChannel1, mockChannel2);
	}
}
