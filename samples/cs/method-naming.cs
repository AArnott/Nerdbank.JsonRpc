// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

namespace MethodNaming;

#region contract
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
    // Sent on the wire as "add" by default: the trailing "Async" is removed and the
    // remainder is camelCased.
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);

    // An explicit name is authoritative and is used verbatim, bypassing any configured transform.
    [MethodShape(Name = "subtract")]
    ValueTask<int> SubtractAsync(int a, int b, CancellationToken cancellationToken);
}
#endregion

public static class MethodNamingExamples
{
    public static void Run(JsonRpc rpc, Calculator calculator)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(calculator);

        #region default-target-naming

        // "AddAsync" dispatches on "add"; "SubtractAsync" dispatches on "subtract" (its explicit name).
        rpc.AddRpcTarget<ICalculator>(calculator);
        #endregion

        #region identity-target-naming

        // Interoperate with a StreamJsonRpc peer that hasn't configured its own method name transform:
        // dispatch on the CLR names it sends by default (e.g. "AddAsync").
        rpc.AddRpcTarget<ICalculator>(calculator, new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.Identity });
        #endregion

        #region default-proxy-naming

        // Calls AddAsync send the request as "add"; calls SubtractAsync send "subtract" (its explicit name).
        ICalculator client = rpc.Attach<ICalculator>();
        #endregion

        #region identity-proxy-naming

        // Send CLR-style names (e.g. "AddAsync") to interoperate with a StreamJsonRpc peer
        // that hasn't configured its own method name transform.
        ICalculator interopClient = rpc.Attach<ICalculator>(new JsonRpcProxyOptions { MethodNameTransform = CommonMethodNameTransforms.Identity });
        #endregion
    }
}

public sealed class Calculator : ICalculator
{
    public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);

    public ValueTask<int> SubtractAsync(int a, int b, CancellationToken cancellationToken) => new(a - b);
}
