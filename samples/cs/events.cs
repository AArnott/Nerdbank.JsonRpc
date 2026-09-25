// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

namespace Events;

#pragma warning disable SA1649 // The sample file name matches its documentation topic rather than its type.

public static class EventsExamples
{
    public static void Run(JsonRpc rpc, Watcher watcher, bool disableEventForwarding = false)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(watcher);

        #region default-registration

        // Raising PriceChanged or RangeChanged on `watcher` after this call sends a
        // "priceChanged" or "rangeChanged" notification to the remote party.
        if (!disableEventForwarding)
        {
            rpc.AddRpcTarget(watcher);
        }
        #endregion

        #region opt-out

        // Disable event forwarding for a target: raising its events no longer sends notifications.
        // This is an alternative to the default registration above, not a second registration
        // on the same JsonRpc instance.
        if (disableEventForwarding)
        {
            rpc.AddRpcTarget(watcher, new JsonRpcTargetOptions { NotifyClientOfEvents = false });
        }
        #endregion
    }
}

#region contract
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial class Watcher
{
    // The classic .NET event pattern: the "sender" parameter is recognized and omitted from the
    // notification, so only the EventArgs-derived value is sent. Sent on the wire as "priceChanged"
    // by default (the same camelCase transform used for method names).
    public event EventHandler<decimal>? PriceChanged;

    // A custom delegate without a leading "sender" parameter forwards all of its parameters,
    // positionally, as the notification's arguments.
    public event Action<int, int>? RangeChanged;

    public void RaisePriceChanged(decimal price) => this.PriceChanged?.Invoke(this, price);

    public void RaiseRangeChanged(int low, int high) => this.RangeChanged?.Invoke(low, high);
}
#endregion
