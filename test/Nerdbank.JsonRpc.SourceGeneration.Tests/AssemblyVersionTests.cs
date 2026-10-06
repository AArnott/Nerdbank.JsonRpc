// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Linq;
using System.Reflection;
using TUnit.Core;

/// <summary>
/// Verifies that projects with their own version.json get the assembly version it specifies
/// rather than the repo root version.json's version (e.g. when a root GitVersionBaseDirectory overrides it).
/// </summary>
/// <remarks>
/// The expected versions are computed at build time by the AddNestedVersionJsonAssemblyVersionExpectations target in test/Directory.Build.targets.
/// Like the other tests in this project, this throws rather than using Assert, which is ambiguous here between xunit.assert and xunit.v3.assert.
/// </remarks>
public class AssemblyVersionTests
{
	[Test]
	public void NerdbankJsonRpcSourceGeneration() => AssertNestedVersionJsonAssemblyVersion(typeof(global::Nerdbank.JsonRpc.SourceGeneration.ClientProxyGenerator).Assembly);

	private static void AssertNestedVersionJsonAssemblyVersion(Assembly assembly)
	{
		AssemblyName assemblyName = assembly.GetName();
		string expected = GetExpectedAssemblyVersion(assemblyName.Name!)
			?? throw new InvalidOperationException($"The test project should list {assemblyName.Name} as a NestedVersionJsonProject.");
		if (assemblyName.Version != Version.Parse(expected))
		{
			throw new InvalidOperationException($"{assemblyName.Name} should have assembly version {expected} from its own version.json, but has {assemblyName.Version}.");
		}
	}

	private static string? GetExpectedAssemblyVersion(string assemblyName)
	{
		return typeof(AssemblyVersionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			.SingleOrDefault(a => a.Key == $"ExpectedAssemblyVersion:{assemblyName}")?.Value;
	}
}
