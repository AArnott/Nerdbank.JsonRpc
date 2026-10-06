// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using TUnit.Core;

/// <summary>
/// Verifies that projects with their own version.json get its revision-level assembly version,
/// rather than the root version.json's x.y.0.0 (e.g. when a root GitVersionBaseDirectory overrides it).
/// </summary>
/// <remarks>
/// Like the other tests in this project, this throws rather than using Assert, which is ambiguous here between xunit.assert and xunit.v3.assert.
/// </remarks>
public class AssemblyVersionTests
{
	[Test]
	public void NerdbankJsonRpcSourceGeneration() => AssertRevisionIsNonZero(typeof(global::Nerdbank.JsonRpc.SourceGeneration.ClientProxyGenerator).Assembly);

	private static void AssertRevisionIsNonZero(System.Reflection.Assembly assembly)
	{
		System.Reflection.AssemblyName name = assembly.GetName();
		if (name.Version!.Revision == 0)
		{
			throw new InvalidOperationException($"{name.Name} has assembly version {name.Version}, but its own version.json should give it a non-zero revision.");
		}
	}
}
