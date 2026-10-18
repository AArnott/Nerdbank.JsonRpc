// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

namespace Benchmarks;

/// <summary>A structured notification payload for the serialized public API.</summary>
[GenerateShape]
public partial struct NotificationBenchmarkArguments
{
	/// <summary>Gets or sets the notification value.</summary>
	public string Value { get; set; }
}
