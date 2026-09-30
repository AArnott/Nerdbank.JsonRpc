// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// This file is compiled into both the ASP.NET Core server (net10.0) and the browser client (netwasm0.1).
using System.Threading;
using System.Threading.Tasks;
using Nerdbank.JsonRpc;
using PolyType;

namespace NetWasmDemo;

/// <summary>
/// The RPC contract the server implements and the browser client calls.
/// </summary>
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IDemoServer
{
    /// <summary>Adds two integers.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The sum.</returns>
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);

    /// <summary>Produces a greeting.</summary>
    /// <param name="name">The name of the person to greet.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The greeting.</returns>
    ValueTask<string> GreetAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// The notifications the server sends to the browser client.
/// </summary>
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IDemoClient
{
    /// <summary>A periodic server-to-client notification.</summary>
    /// <param name="count">A counter that increments with each notification.</param>
    /// <param name="message">A message from the server.</param>
    void Tick(int count, string message);
}
