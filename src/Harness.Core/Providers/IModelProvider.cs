using Harness.Core.Models;

namespace Harness.Core.Providers;

public interface IModelProvider
{
    string Id { get; }
    string DisplayName { get; }

    IAsyncEnumerable<ModelDescriptor> GetModelsAsync(
        CancellationToken cancellationToken = default);
}
