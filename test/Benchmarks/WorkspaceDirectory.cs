// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using MessagePack;
using PolyType;

namespace Benchmarks;

// Benchmark payload shapes keep their property defaults off the wire contract so encoded sizes stay comparable across serializers.
#pragma warning disable NBMsgPack110

/// <summary>A directory within a deterministic workspace graph, containing files and nested subdirectories.</summary>
[MessagePackObject(keyAsPropertyName: true)]
[GenerateShape]
public partial class WorkspaceDirectory
{
	/// <summary>Gets or sets the directory's name.</summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>Gets or sets the files directly contained in this directory.</summary>
	public List<WorkspaceFile> Files { get; set; } = new();

	/// <summary>Gets or sets the nested subdirectories.</summary>
	public List<WorkspaceDirectory> Subdirectories { get; set; } = new();

	/// <summary>Gets or sets arbitrary string metadata associated with the directory.</summary>
	public Dictionary<string, string> Metadata { get; set; } = new();
}
#pragma warning restore NBMsgPack110
