// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using MessagePack;
using PolyType;

namespace Benchmarks;

// Benchmark payload shapes keep their property defaults off the wire contract so encoded sizes stay comparable across serializers.
#pragma warning disable NBMsgPack110

/// <summary>A single file entry within a deterministic workspace graph used to benchmark large-argument round trips.</summary>
[MessagePackObject(keyAsPropertyName: true)]
[GenerateShape]
public partial class WorkspaceFile
{
	/// <summary>Gets or sets the file's path relative to its containing directory.</summary>
	public string Path { get; set; } = string.Empty;

	/// <summary>Gets or sets the file's simulated size in bytes.</summary>
	public long SizeBytes { get; set; }

	/// <summary>Gets or sets a simulated content hash.</summary>
	public string Hash { get; set; } = string.Empty;

	/// <summary>Gets or sets the tags associated with the file.</summary>
	public List<string> Tags { get; set; } = new();
}
#pragma warning restore NBMsgPack110
