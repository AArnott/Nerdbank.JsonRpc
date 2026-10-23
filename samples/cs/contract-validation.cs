// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

namespace ContractValidation;

#region target-only
[JsonRpcContract(GenerateProxy = false)]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ITargetOnly
{
    ValueTask DisplayMessageAsync(string message, CancellationToken cancellationToken);
}
#endregion

#pragma warning disable NBJSONRPC002 // Demonstrate the wire-name collision diagnosed by the analyzer.
#region colliding-methods
[JsonRpcContract(GenerateProxy = false)]
public interface ICollidingBackchannel
{
    [MethodShape(Name = "displayMessage")]
    Task DisplayMessageWithActionsAsync(string message, string[] actions);

    Task DisplayMessageAsync(string message);
}
#endregion
#pragma warning restore NBJSONRPC002

#region distinct-methods
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IBackchannel
{
    [MethodShape(Name = "displayMessageWithActions")]
    Task DisplayMessageWithActionsAsync(string message, string[] actions);

    Task DisplayMessageAsync(string message);
}
#endregion

#pragma warning disable NBJSONRPC003 // Demonstrate an unsupported RPC property.
#region unsupported-member
[JsonRpcContract(GenerateProxy = false)]
public interface IUnsupportedCounter
{
    int Count { get; }
}
#endregion
#pragma warning restore NBJSONRPC003

#region supported-member
[JsonRpcContract(GenerateProxy = false)]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICounter
{
    ValueTask<int> GetCountAsync(CancellationToken cancellationToken);
}
#endregion
