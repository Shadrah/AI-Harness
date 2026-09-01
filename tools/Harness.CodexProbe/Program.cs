using Harness.Providers.Codex;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
{
    timeout.CancelAfter(TimeSpan.FromMinutes(5));
    var progress = new Progress<CodexRuntimeInstallProgress>(update =>
        Console.WriteLine($"{update.State}: {update.Detail}"));
    var installed = await new CodexRuntimeInstaller().InstallLatestAsync(progress, timeout.Token);
    Console.WriteLine($"Installed: {installed.ExecutablePath}");
    Console.WriteLine($"Code tools: {(installed.CodeToolsAvailable ? "ready" : "missing")}");
    return;
}
if (args.Contains("--release-check", StringComparer.OrdinalIgnoreCase))
{
    var release = await new CodexRuntimeInstaller().CheckLatestAsync(timeout.Token);
    Console.WriteLine($"Latest: {release.Version}");
    Console.WriteLine($"Asset: {release.AssetName}");
    Console.WriteLine($"Digest: {release.Digest}");
    return;
}

await using var client = await CodexAppServerClient.StartAsync(timeout.Token);
Console.WriteLine($"Runtime: {client.Runtime.SourceLabel} · {client.Runtime.ExecutablePath}");
Console.WriteLine($"Code tools: {(client.Runtime.CodeToolsAvailable ? "ready" : "missing")}");

var models = new List<string>();
await foreach (var model in client.GetModelsAsync(timeout.Token))
{
    var efforts = model.ReasoningLevels is null
        ? "none advertised"
        : string.Join(",", model.ReasoningLevels.Select(level => level.Id));
    var tiers = model.ServiceTiers is null || model.ServiceTiers.Count == 0
        ? "provider default tier"
        : string.Join(",", model.ServiceTiers.Select(
            tier => $"{tier.DisplayName}({tier.Id ?? "default"})"));
    models.Add($"{model.ModelId}: reasoning={efforts}; tiers={tiers}");
}

Console.WriteLine($"Models: {models.Count}");
foreach (var model in models)
{
    Console.WriteLine(model);
}

try
{
    var usage = await client.GetUsageAsync(timeout.Token);
    Console.WriteLine($"Plan: {usage?.PlanName ?? "not reported"}");
    foreach (var window in usage?.Windows ?? [])
    {
        Console.WriteLine(
            $"{window.DisplayName}: {window.RemainingPercent:0}% remaining; reset={window.ResetsAt:O}");
    }
}
catch (InvalidOperationException error)
{
    Console.WriteLine($"Usage unavailable: {error.Message}");
}
