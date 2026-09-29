// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Benchmarks;

/// <summary>Creates and validates the deterministic <see cref="WorkspaceGraph"/> used by the large-argument benchmarks.</summary>
public static class WorkspaceGraphFactory
{
	/// <summary>Creates a deterministic workspace graph containing hundreds of file entries.</summary>
	/// <returns>The generated graph.</returns>
	public static WorkspaceGraph CreateLarge()
	{
		Random random = new(Seed: 42);
		WorkspaceGraph graph = new() { Root = "workspace" };
		for (int d = 0; d < 20; d++)
		{
			WorkspaceDirectory directory = new() { Name = $"dir{d}" };
			for (int f = 0; f < 25; f++)
			{
				directory.Files.Add(new WorkspaceFile
				{
					Path = $"dir{d}/file{f}.cs",
					SizeBytes = random.Next(100, 100_000),
					Hash = Convert.ToHexString(BitConverter.GetBytes(random.NextInt64())),
					Tags = ["source", f % 2 == 0 ? "even" : "odd", $"group{f % 5}"],
				});
			}

			directory.Metadata["owner"] = $"team{d % 4}";
			directory.Metadata["lastBuild"] = "2026-01-01T00:00:00Z";
			graph.Directories.Add(directory);
		}

		return graph;
	}

	/// <summary>Computes a deterministic checksum over every string and numeric field in the graph.</summary>
	/// <param name="graph">The graph to summarize.</param>
	/// <returns>The checksum.</returns>
	public static long Checksum(WorkspaceGraph graph)
	{
		long checksum = graph.Root.Length;
		foreach (WorkspaceDirectory directory in graph.Directories)
		{
			checksum += directory.Name.Length;
			foreach (WorkspaceFile file in directory.Files)
			{
				checksum += file.SizeBytes + file.Path.Length + file.Hash.Length;
				foreach (string tag in file.Tags)
				{
					checksum += tag.Length;
				}
			}

			foreach (KeyValuePair<string, string> pair in directory.Metadata)
			{
				checksum += pair.Key.Length + pair.Value.Length;
			}
		}

		return checksum;
	}
}
