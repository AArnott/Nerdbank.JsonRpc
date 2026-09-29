// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using MessagePack;
using PolyType;

namespace Benchmarks;

// Benchmark payload shapes keep their property defaults off the wire contract so encoded sizes stay comparable across serializers.
#pragma warning disable NBMsgPack110

/// <summary>A deterministic, sizeable object graph representative of a real workspace snapshot.</summary>
[MessagePackObject(keyAsPropertyName: true)]
[GenerateShape]
public partial class WorkspaceGraph
{
	/// <summary>Gets or sets the root directory name.</summary>
	public string Root { get; set; } = string.Empty;

	/// <summary>Gets or sets the top-level directories in the workspace.</summary>
	public List<WorkspaceDirectory> Directories { get; set; } = new();
}
#pragma warning restore NBMsgPack110
