// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.Threading;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Correlates <see cref="JoinableTask"/> tokens within and between <see cref="JsonRpc"/> instances
/// in a process that does <em>not</em> use a <see cref="JoinableTaskFactory"/>,
/// for purposes of mitigating deadlocks in processes that <em>do</em>.
/// </summary>
/// <remarks>
/// When a request carrying a <see cref="JoinableTask"/> token is received by a <see cref="JsonRpc"/> instance without a
/// <see cref="JsonRpc.JoinableTaskFactory"/>, the token is stored in this tracker for the duration of the request's
/// asynchronous flow so that outbound requests made while servicing it (on any <see cref="JsonRpc"/> instance that shares
/// this tracker) forward the token onward.
/// </remarks>
public class JoinableTaskTokenTracker
{
	/// <summary>The instance shared by all <see cref="JsonRpc"/> instances that do not specify their own.</summary>
	internal static readonly JoinableTaskTokenTracker Default = new();

	private readonly System.Threading.AsyncLocal<string?> token = new();

	/// <summary>Gets or sets the token that applies to the current asynchronous flow.</summary>
	internal string? Token
	{
		get => this.token.Value;
		set => this.token.Value = value;
	}
}
