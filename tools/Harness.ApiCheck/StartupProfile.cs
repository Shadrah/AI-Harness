using System.Diagnostics;
using Harness.Storage;
using Microsoft.Data.Sqlite;

internal static class StartupProfile
{
    public static async Task CheckAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".artifacts", "startup-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "fixture.db");
        await using var store = new HarnessStore(path);
        await store.InitializeAsync();
        using var locker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await locker.OpenAsync();
        using (var plan = locker.CreateCommand())
        {
            plan.CommandText = "EXPLAIN QUERY PLAN SELECT payload_json FROM provider_events WHERE session_id = 'fixture' AND method = 'fixture' ORDER BY created_at DESC LIMIT 1";
            using var reader = await plan.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || !reader.GetString(3).Contains("ix_provider_events_session_method_created", StringComparison.Ordinal))
                throw new InvalidOperationException("Event restoration does not use its index.");
        }
        using var transaction = locker.BeginTransaction();
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timing = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var pending = store.SaveApplicationSettingsAsync(new Harness.Core.Models.HarnessApplicationSettings());
                timing.SetResult(clock.Elapsed.TotalMilliseconds);
                returned.SetResult(pending);
            }
            catch (Exception exception) { returned.TrySetException(exception); }
        });
        caller.Start();
        // While a real SQLite write lock is held, starting a store write must return
        // control to the caller, rather than block the UI until the lock is released.
        var winner = await Task.WhenAny(returned.Task, Task.Delay(1000));
        transaction.Rollback();
        var pendingWrite = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pendingWrite.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Join();
        if (winner != returned.Task) throw new InvalidOperationException("Database lock blocked the calling/UI thread.");
        var models = new Harness.App.ViewModels.BatchObservableCollection<int>();
        var changes = 0;
        models.CollectionChanged += (_, _) => changes++;
        models.ReplaceAll(Enumerable.Range(0, 5000));
        if (changes != 1 || models.Count != 5000) throw new InvalidOperationException("Catalog publication was not batched.");
        models.ReplaceAll(Enumerable.Range(0, 5000));
        if (changes != 1) throw new InvalidOperationException("An unchanged catalog caused redundant UI invalidation.");
        var settings = new Harness.App.ViewModels.SettingsWindowViewModel(new Harness.Core.Models.HarnessApplicationSettings(), root);
        var skillChanges = 0;
        settings.Skills.CollectionChanged += (_, _) => skillChanges++;
        settings.ReplaceSkills(Enumerable.Range(0, 5000).Select(index => new Harness.Core.Models.SkillCatalogEntry(
            index.ToString(), "Skill " + index, "Fixture", "Testing", "fixture/repository", "skills/" + index + "/SKILL.md",
            "revision", "https://example.invalid", "Portable Agent Skill", "Unreviewed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "{}")), []);
        if (skillChanges != 1 || settings.Skills.Count != 5000)
            throw new InvalidOperationException("Settings published a large skill catalog item by item.");
        Console.WriteLine($"Startup checks passed: locked database write returned to caller in {await timing.Task:F1} ms; indexed restoration; 5,000 models and 5,000 skills each published in one notification. Isolated fixture only.");
    }

    // Read-only: reports counts, sizes, plans and timings, never conversation text or credentials.
    public static async Task RunAsync()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = HarnessStore.DefaultDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        foreach (var (label, sql) in new[]
        {
            ("Events", "SELECT count(*), sum(length(payload_json)) FROM provider_events"),
            ("Messages", "SELECT count(*), sum(length(text)), max(length(text)) FROM messages WHERE status != 'IMPORTED'"),
            ("Event lookup plan", "EXPLAIN QUERY PLAN SELECT payload_json FROM provider_events WHERE session_id = 'profile' AND method = 'profile' ORDER BY created_at DESC LIMIT 1"),
            ("Missing event lookup", "SELECT length(payload_json) FROM provider_events WHERE session_id = 'profile' AND method = 'profile' ORDER BY created_at DESC LIMIT 1")
        })
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            var elapsed = Stopwatch.StartNew();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) Console.WriteLine($"{label}: {string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue))}");
            Console.WriteLine($"{label}: {elapsed.Elapsed.TotalMilliseconds:F1} ms");
        }
    }
}
