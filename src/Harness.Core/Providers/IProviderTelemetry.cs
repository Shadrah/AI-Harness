using Harness.Core.Models;

namespace Harness.Core.Providers;

public interface IProviderTelemetry
{
    Task<ProviderUsageSnapshot?> GetUsageAsync(
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ProviderUsageSnapshot> WatchUsageAsync(
        CancellationToken cancellationToken = default);
}
