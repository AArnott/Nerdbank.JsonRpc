// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BenchmarkDotNet.Attributes;
using Nerdbank.MessagePack;

namespace Benchmarks;

/// <summary>Measures the cost of taking ownership of a raw MessagePack value.</summary>
[MemoryDiagnoser]
public class MessagePackValueBenchmarks
{
	private byte[] messagePack = null!;

	/// <summary>Gets or sets the number of characters in the encoded string.</summary>
	[Params(1, 64, 1024)]
	public int CharacterCount { get; set; }

	/// <summary>Prepares a valid encoded MessagePack string before measurement.</summary>
	[GlobalSetup]
	public void Setup()
	{
		this.messagePack = new byte[this.CharacterCount + (this.CharacterCount <= byte.MaxValue ? 2 : 3)];
		if (this.CharacterCount <= byte.MaxValue)
		{
			this.messagePack[0] = 0xd9;
			this.messagePack[1] = (byte)this.CharacterCount;
		}
		else
		{
			this.messagePack[0] = 0xda;
			this.messagePack[1] = (byte)(this.CharacterCount >> 8);
			this.messagePack[2] = (byte)this.CharacterCount;
		}

		this.messagePack.AsSpan(this.messagePack.Length - this.CharacterCount).Fill((byte)'x');
		if (!JsonRpcValue.FromMessagePack((RawMessagePack)this.messagePack).HasValue)
		{
			throw new InvalidOperationException("The benchmark must produce a present MessagePack value.");
		}
	}

	/// <summary>Creates an owned raw MessagePack value.</summary>
	/// <returns>The owned raw value.</returns>
	[Benchmark]
	public JsonRpcValue FromMessagePack() => JsonRpcValue.FromMessagePack((RawMessagePack)this.messagePack);
}
