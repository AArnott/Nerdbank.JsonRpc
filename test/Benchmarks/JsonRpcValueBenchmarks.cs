// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;
using BenchmarkDotNet.Attributes;

namespace Benchmarks;

/// <summary>Measures the cost of taking ownership of a raw JSON value.</summary>
[MemoryDiagnoser]
public class JsonRpcValueBenchmarks
{
	private byte[] json = null!;

	/// <summary>Gets or sets the number of array entries in the JSON value.</summary>
	[Params(1, 64, 1024)]
	public int ElementCount { get; set; }

	/// <summary>Prepares representative valid JSON before measurement.</summary>
	[GlobalSetup]
	public void Setup()
	{
		StringBuilder builder = new("[", this.ElementCount * 32);
		for (int i = 0; i < this.ElementCount; i++)
		{
			if (i > 0)
			{
				builder.Append(',');
			}

			builder.Append("{\"index\":").Append(i).Append(",\"name\":\"entry\"}");
		}

		this.json = Encoding.UTF8.GetBytes(builder.Append(']').ToString());
		if (!JsonRpcValue.FromJson(this.json).HasValue)
		{
			throw new InvalidOperationException("The benchmark must produce a valid JSON value.");
		}
	}

	/// <summary>Creates an owned raw JSON value.</summary>
	/// <returns>The owned raw value.</returns>
	[Benchmark]
	public JsonRpcValue FromJson() => JsonRpcValue.FromJson(this.json);
}
