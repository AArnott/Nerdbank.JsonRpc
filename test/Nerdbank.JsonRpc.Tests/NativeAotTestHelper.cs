// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;

/// <summary>Provides guards for tests affected by NativeAOT runtime limitations.</summary>
internal static class NativeAotTestHelper
{
	private const string Net9GvmIssue = "Skipped because of https://github.com/dotnet/runtime/issues/113664, which is fixed in .NET 10.";

	/// <summary>Skips a test path that triggers the .NET 9 NativeAOT generic virtual method defect.</summary>
	/// <param name="encoding">The encoding used by the test, or <see langword="null"/> when the test always uses JSON.</param>
	internal static void SkipNerdbankJsonOnNativeAot(JsonRpcEncoding? encoding = null)
	{
#if NET
		Skip.When(!RuntimeFeature.IsDynamicCodeSupported && encoding is null or JsonRpcEncoding.Json, Net9GvmIssue);
#endif
	}
}
