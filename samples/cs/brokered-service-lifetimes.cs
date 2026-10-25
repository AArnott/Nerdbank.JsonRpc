using Nerdbank.JsonRpc.BrokeredServices;

namespace BrokeredServices;

internal static class BrokeredServiceLifetimeExamples
{
    internal static async Task MissingDisposalAsync(IServiceBroker broker, CancellationToken cancellationToken)
    {
#pragma warning disable NBJSONRPC004 // Demonstrates the diagnosed pattern.
        #region brokered-undisposed-proxy
        IGreeter? greeter = await broker.GetProxyAsync(BrokeredServiceExamples.Greeter, cancellationToken: cancellationToken);
        if (greeter is not null)
        {
            await greeter.GreetAsync("Ada", cancellationToken);
        }
        #endregion
#pragma warning restore NBJSONRPC004
    }

    internal static async Task DisposeLocallyAsync(IServiceBroker broker, CancellationToken cancellationToken)
    {
        #region brokered-dispose-with-cast
        IGreeter? greeter = await broker.GetProxyAsync(BrokeredServiceExamples.Greeter, cancellationToken: cancellationToken);
        using (greeter as IDisposable)
        {
            if (greeter is not null)
            {
                await greeter.GreetAsync("Ada", cancellationToken);
            }
        }
        #endregion
    }
}

#region brokered-disposable-owner
internal sealed class GreeterOwner : IDisposable
{
    private IGreeter? greeter;

    public void Dispose() => (this.greeter as IDisposable)?.Dispose();

    internal async Task ReplaceAsync(IServiceBroker broker, CancellationToken cancellationToken)
    {
        (this.greeter as IDisposable)?.Dispose();
        this.greeter = await broker.GetProxyAsync(BrokeredServiceExamples.Greeter, cancellationToken: cancellationToken);
    }
}
#endregion

internal sealed class CachedGreeterOwner
{
    private ServiceBrokerClient.Rental<IGreeter>? rental;

    internal async Task AcquireAsync(ServiceBrokerClient client, CancellationToken cancellationToken)
    {
#pragma warning disable NBJSONRPC005 // Demonstrates the diagnosed pattern.
        #region brokered-stored-rental
        this.rental = await client.GetProxyAsync(BrokeredServiceExamples.Greeter, cancellationToken: cancellationToken);
        #endregion
#pragma warning restore NBJSONRPC005
    }
}
