using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Diagnostics;
using System.Text.Json;
using Harness.App;
using Harness.App.ViewModels;
using Harness.App.Views;
using Harness.Core.Models;
using Harness.Storage;
using Harness.Workspace;
using Harness.Providers.Codex;

var outputPath = args.FirstOrDefault()
    ?? Path.Combine(Environment.CurrentDirectory, ".artifacts", "harness-shell.png");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

var runtimeProbeRoot = Path.Combine(Path.GetTempPath(), "Harness.RuntimeProbe", Guid.NewGuid().ToString("N"));
var runtimeProbeBin = Path.Combine(runtimeProbeRoot, "bin");
Directory.CreateDirectory(runtimeProbeBin);
try
{
    var runtimeExecutable = Path.Combine(runtimeProbeBin, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
    var codeHost = Path.Combine(runtimeProbeBin, OperatingSystem.IsWindows() ? "codex-code-mode-host.exe" : "codex-code-mode-host");
    await File.WriteAllBytesAsync(runtimeExecutable, []);
    if (CodexRuntimeResolver.HasRequiredTools(runtimeExecutable))
        throw new InvalidOperationException("An incomplete managed runtime was reported as tool-capable.");
    await File.WriteAllBytesAsync(codeHost, []);
    if (!CodexRuntimeResolver.HasRequiredTools(runtimeExecutable))
        throw new InvalidOperationException("A complete managed runtime was not recognized.");
}
finally
{
    Directory.Delete(runtimeProbeRoot, recursive: true);
}

var parsedDiff = UnifiedDiffParser.Parse(
    "diff --git a/demo.txt b/demo.txt\n--- a/demo.txt\n+++ b/demo.txt\n@@ -4,2 +4,3 @@\n same\n-old\n+new\n+extra");
if (parsedDiff.AddedLines != 2
    || parsedDiff.RemovedLines != 1
    || parsedDiff.Lines.Single(line => line.Kind == DiffLineKind.Removed).OldLineNumber != 5
    || parsedDiff.Lines.Where(line => line.Kind == DiffLineKind.Added)
        .Select(line => line.NewLineNumber)
        .SequenceEqual([5, 6]) is false)
{
    throw new InvalidOperationException("Unified diff line classification or numbering is incorrect.");
}

var historyProbeRoot = Path.Combine(Path.GetTempPath(), "Harness.HistoryProbe", Guid.NewGuid().ToString("N"));
var codexHistoryRoot = Path.Combine(historyProbeRoot, "codex");
var claudeHistoryRoot = Path.Combine(historyProbeRoot, "claude");
Directory.CreateDirectory(codexHistoryRoot);
Directory.CreateDirectory(claudeHistoryRoot);
try
{
    var codexProject = Path.Combine(historyProbeRoot, "project-a");
    var claudeProject = Path.Combine(historyProbeRoot, "project-b");
    Directory.CreateDirectory(codexProject);
    Directory.CreateDirectory(claudeProject);
    Directory.CreateDirectory(Path.Combine(codexProject, ".git"));
    Directory.CreateDirectory(Path.Combine(claudeProject, ".git"));
    await File.WriteAllTextAsync(Path.Combine(codexProject, "AGENTS.md"), "project rules");
    var codexProjectJson = JsonSerializer.Serialize(codexProject);
    var claudeProjectJson = JsonSerializer.Serialize(claudeProject);
    await File.WriteAllLinesAsync(Path.Combine(codexHistoryRoot, "session.jsonl"),
    [
        $"{{\"timestamp\":\"2026-08-30T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"codex-session\",\"cwd\":{codexProjectJson},\"source\":\"vscode\"}}}}",
        "{\"timestamp\":\"2026-08-30T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"Fix the parser\"}}",
        "{\"timestamp\":\"2026-08-30T10:02:00Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Parser fixed\"}]}}"
    ]);
    await File.WriteAllLinesAsync(Path.Combine(codexHistoryRoot, "session-2.jsonl"),
    [
        $"{{\"timestamp\":\"2026-08-31T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"codex-session-2\",\"cwd\":{codexProjectJson},\"source\":{{\"subagent\":{{\"other\":\"guardian\"}}}}}}}}",
        "{\"timestamp\":\"2026-08-31T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"Ship the parser\"}}",
        "{\"timestamp\":\"2026-08-31T10:02:00Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Parser shipped\"}]}}"
    ]);
    await File.WriteAllLinesAsync(Path.Combine(codexHistoryRoot, "harness-session.jsonl"),
    [
        $"{{\"timestamp\":\"2026-09-01T11:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"harness-session\",\"cwd\":{codexProjectJson},\"originator\":\"harness\",\"source\":\"vscode\"}}}}",
        "{\"timestamp\":\"2026-09-01T11:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"Do not reimport me\"}}"
    ]);
    await File.WriteAllLinesAsync(Path.Combine(claudeHistoryRoot, "session.jsonl"),
    [
        $"{{\"type\":\"user\",\"sessionId\":\"claude-session\",\"cwd\":{claudeProjectJson},\"timestamp\":\"2026-08-29T10:00:00Z\",\"message\":{{\"role\":\"user\",\"content\":\"Review this\"}}}}",
        $"{{\"type\":\"assistant\",\"sessionId\":\"claude-session\",\"cwd\":{claudeProjectJson},\"timestamp\":\"2026-08-29T10:01:00Z\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"Review complete\"}}]}}}}"
    ]);
    var inventory = await HarnessHistoryScanner.ScanAsync(codexHistoryRoot, claudeHistoryRoot);
    if (inventory.Projects.Count != 2
        || inventory.Conversations.Count != 2
        || inventory.Conversations.Any(candidate => candidate.MessageCount != 2)
        || inventory.Projects.Single(project => project.SourceHarness == "Codex").Conversations.Count != 1
        || inventory.Projects.Single(project => project.SourceHarness == "Codex").ContextFiles.Count != 1
        || inventory.Projects.Any(project => project.Conversations.Count(candidate => candidate.IsPrimaryContinuation) != 1)
        || inventory.Conversations.Select(candidate => candidate.SourceHarness)
            .Distinct().OrderBy(value => value)
            .SequenceEqual(new[] { "Claude Code", "Codex" }) is false)
    {
        throw new InvalidOperationException(
            $"Harness history detection lost a conversation, role, or source identity. "
            + $"Projects={inventory.Projects.Count}; conversations={inventory.Conversations.Count}; "
            + $"diagnostics={string.Join(" | ", inventory.Diagnostics)}; "
            + $"sources={string.Join(", ", inventory.Conversations.Select(candidate => $"{candidate.SourceHarness}:{candidate.MessageCount}:primary={candidate.IsPrimaryContinuation}"))}");
    }
}
finally
{
    Directory.Delete(historyProbeRoot, recursive: true);
}

var storageProbeRoot = Path.Combine(
    Path.GetTempPath(),
    "Harness.StorageProbe",
    Guid.NewGuid().ToString("N"));
var storageProbeWorkspace = Path.Combine(storageProbeRoot, "workspace");
var storageProbeDatabase = Path.Combine(storageProbeRoot, "harness.db");
Directory.CreateDirectory(storageProbeWorkspace);
try
{
    var transcriptPath = Path.Combine(storageProbeWorkspace, "conversation.md");
    await File.WriteAllTextAsync(transcriptPath, "## User\nBuild it.\n\n## Assistant\nDone and verified.");
    var importPlan = await ConversationImportScanner.ScanAsync(transcriptPath);
    if (importPlan.Messages.Count != 2
        || importPlan.Messages[0].Role != "YOU"
        || importPlan.Messages[1].Role != "HARNESS")
    {
        throw new InvalidOperationException("Conversation transcript detection lost message roles or order.");
    }
    string firstSessionId;
    string secondSessionId;
    string attachmentId;
    string storedAttachmentPath;
    string legacySubagentSessionId;
    string legacyHarnessSessionId;
    await using (var store = new HarnessStore(storageProbeDatabase))
    {
        await store.InitializeAsync();
        var snapshot = await store.OpenWorkspaceAsync(storageProbeWorkspace);
        firstSessionId = snapshot.ActiveSession.Id;
        var messageId = Guid.NewGuid().ToString("N");
        await store.UpsertMessageAsync(new StoredMessage(
            messageId,
            firstSessionId,
            0,
            "HARNESS",
            "Response",
            "partial",
            "STREAMING",
            "#65C7D0",
            false,
            DateTimeOffset.UtcNow));
        await store.UpsertMessageAsync(new StoredMessage(
            messageId,
            firstSessionId,
            0,
            "HARNESS",
            "Response",
            "complete",
            "COMPLETED",
            "#65C7D0",
            false,
            DateTimeOffset.UtcNow));
        await store.UpdateSessionConnectionAsync(
            firstSessionId,
            "provider-probe",
            "thread-probe",
            "model-probe",
            "high",
            "priority");
        await store.UpdateSessionModelSettingsAsync(
            firstSessionId,
            "provider-probe",
            "model-next",
            "max",
            null);
        await store.AppendProviderEventAsync(
            firstSessionId,
            "turn/completed",
            "{\"turn\":{\"status\":\"completed\"}}");
        await store.AppendProviderEventAsync(
            firstSessionId,
            "thread/tokenUsage/updated",
            "{\"tokenUsage\":{\"last\":{\"inputTokens\":37449},\"total\":{\"totalTokens\":24690858},\"modelContextWindow\":258400}}");
        var contextSource = Path.Combine(storageProbeWorkspace, "context.md");
        await File.WriteAllTextAsync(contextSource, "durable context");
        var attachment = await store.AddAttachmentAsync(firstSessionId, contextSource);
        attachmentId = attachment.Id;
        storedAttachmentPath = attachment.StoredPath;
        File.Delete(contextSource);
        secondSessionId = (await store.CreateSessionAsync(
            snapshot.Project.Id,
            "Second session")).Id;
        var legacySubagentPath = Path.Combine(storageProbeWorkspace, "legacy-subagent.jsonl");
        await File.WriteAllLinesAsync(legacySubagentPath,
        [
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"legacy-subagent\",\"cwd\":\"C:\\\\work\",\"source\":{\"subagent\":{\"other\":\"guardian\"}}}}",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"Internal check\"}}"
        ]);
        var legacySubagent = await store.ImportConversationAsync(
            snapshot.Project.Id,
            new ConversationImportPlan(
                "Codex history",
                legacySubagentPath,
                "Internal guardian",
                [new ImportMessage("YOU", "Internal check")],
                []));
        legacySubagentSessionId = legacySubagent.Session.Id;
        var legacyHarnessPath = Path.Combine(storageProbeWorkspace, "legacy-harness.jsonl");
        await File.WriteAllLinesAsync(legacyHarnessPath,
        [
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"legacy-harness\",\"cwd\":\"C:\\\\work\",\"originator\":\"harness\",\"source\":\"vscode\"}}",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"Harness provider turn\"}}"
        ]);
        var legacyHarness = await store.ImportConversationAsync(
            snapshot.Project.Id,
            new ConversationImportPlan(
                "Codex history",
                legacyHarnessPath,
                "Harness provider turn",
                [new ImportMessage("YOU", "Harness provider turn")],
                []));
        legacyHarnessSessionId = legacyHarness.Session.Id;
    }

    await using (var reopened = new HarnessStore(storageProbeDatabase))
    {
        await reopened.InitializeAsync();
        var recovered = await reopened.LoadSessionAsync(firstSessionId);
        if (recovered.Session.ProviderThreadId != "thread-probe"
            || recovered.Session.ModelId != "model-next"
            || recovered.Session.ReasoningEffort != "max"
            || recovered.Session.ServiceTier is not null
            || recovered.Messages.Count != 1
            || recovered.Messages[0].Text != "complete"
            || recovered.Messages[0].Status != "COMPLETED"
            || recovered.Attachments.Count != 1
            || recovered.Attachments[0].DisplayName != "context.md"
            || !File.Exists(recovered.Attachments[0].StoredPath)
            || await File.ReadAllTextAsync(recovered.Attachments[0].StoredPath) != "durable context")
        {
            throw new InvalidOperationException(
                "SQLite restart recovery did not preserve the latest message and provider thread state.");
        }

        await reopened.RenameSessionAsync(firstSessionId, "Recovered session");
        await reopened.DeleteSessionAsync(secondSessionId);
        var classifiedSubagent = await reopened.GetImportSourceAsync(legacySubagentSessionId);
        var classifiedHarness = await reopened.GetImportSourceAsync(legacyHarnessSessionId);
        var managed = await reopened.OpenWorkspaceAsync(storageProbeWorkspace);
        if (managed.Sessions.Count != 1
            || managed.ActiveSession.Id != firstSessionId
            || managed.ActiveSession.Title != "Recovered session"
            || classifiedSubagent?.SourceKind != "Codex internal session"
            || classifiedHarness?.SourceKind != "Codex internal session")
        {
            throw new InvalidOperationException(
                "Durable session rename or deletion did not survive a workspace reload.");
        }
        var secondWorkspacePath = Path.Combine(storageProbeRoot, "workspace-two");
        Directory.CreateDirectory(secondWorkspacePath);
        var secondWorkspace = await reopened.OpenWorkspaceAsync(secondWorkspacePath);
        var projectCatalog = await reopened.ListProjectsAsync();
        var workspaceProbe = new MainWindowViewModel();
        workspaceProbe.ApplyWorkspaceCatalog(projectCatalog, secondWorkspace.Project.Id);
        var stableOrder = workspaceProbe.Workspaces.Select(workspace => workspace.ProjectId).ToArray();
        var preservedWorkspaceItem = workspaceProbe.SelectedWorkspace;
        workspaceProbe.ApplyWorkspaceCatalog(projectCatalog.Reverse().ToArray(), managed.Project.Id);
        workspaceProbe.ApplyWorkspaceCatalog([], managed.Project.Id);
        if (workspaceProbe.Workspaces.Count != 2
            || workspaceProbe.SelectedWorkspace?.ProjectId != managed.Project.Id
            || !workspaceProbe.Workspaces.Contains(preservedWorkspaceItem!)
            || !workspaceProbe.Workspaces.Select(workspace => workspace.ProjectId).SequenceEqual(stableOrder)
            || workspaceProbe.SelectedWorkspace.DotColor != "#65C7D0"
            || workspaceProbe.Workspaces.Any(workspace => workspace.Background != "Transparent"))
        {
            throw new InvalidOperationException("Workspace switching changed rail order, lost projects, or highlighted more than the active indicator.");
        }
        var imported = await reopened.ImportConversationAsync(managed.Project.Id, importPlan);
        var importedSession = await reopened.LoadSessionAsync(imported.Session.Id);
        var importSource = await reopened.GetImportSourceAsync(imported.Session.Id);
        var hasImportBoundary = await reopened.HasProviderEventAsync(imported.Session.Id, "harness/importBoundary");
        var recognizesImportedSource = await reopened.HasImportedSourceAsync(managed.Project.Id, importPlan.SourcePath);
        if (importedSession.Messages.Count != 2
            || importedSession.Messages[0].Status != "IMPORTED"
            || !File.Exists(imported.StoredSourcePath)
            || importSource is null
            || !hasImportBoundary
            || !recognizesImportedSource)
        {
            throw new InvalidOperationException("Conversation import was not self-contained or ordered.");
        }
        var continuity = ImportedConversationContextBuilder.Build(importSource, importedSession.Messages);
        if (!continuity.Text.Contains("Build it.", StringComparison.Ordinal)
            || !continuity.Text.Contains("Done and verified.", StringComparison.Ordinal)
            || continuity.TotalMessages != 2
            || continuity.OmittedMessages != 0)
        {
            throw new InvalidOperationException("Imported messages were not reconstructed as provider continuity context.");
        }
        var hiddenImportProbe = new MainWindowViewModel();
        hiddenImportProbe.ApplyStoredSession(imported.Session, importedSession.Messages);
        if (hiddenImportProbe.Messages.Count != 0)
        {
            throw new InvalidOperationException("Imported history leaked into the visible new-session chat transcript.");
        }
        var oversizedMessages = Enumerable.Range(0, 24)
            .Select(index => new StoredMessage(
                $"oversized-{index}",
                imported.Session.Id,
                index,
                index % 2 == 0 ? "YOU" : "HARNESS",
                index % 2 == 0 ? "User" : "Assistant",
                index == 12
                    ? "marker-12 Awaiting Microsoft Artifact Signing approval before standalone distribution can proceed."
                    : $"marker-{index} " + new string((char)('a' + index % 26), 4_000),
                "IMPORTED",
                "#65C7D0",
                false,
                DateTimeOffset.UtcNow))
            .ToArray();
        var compactContinuity = ImportedConversationContextBuilder.Build(importSource, oversizedMessages, 10_000);
        if (compactContinuity.OmittedMessages == 0
            || compactContinuity.Text.Length > 10_500
            || !compactContinuity.Text.Contains("marker-0", StringComparison.Ordinal)
            || !compactContinuity.Text.Contains("Artifact Signing approval", StringComparison.Ordinal)
            || !compactContinuity.Text.Contains("marker-23", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Oversized imported history was not bounded while preserving its opening and stopping point.");
        }
        var settings = new HarnessApplicationSettings(
            ShowActivityTrace: false,
            PersonalInstructions: "Be concise.",
            LastWorkspacePath: storageProbeWorkspace,
            GitAuthorName: "Harness Publisher",
            GitAuthorEmail: "publisher@users.noreply.github.com",
            DefaultGitBranch: "release-main");
        await reopened.SaveApplicationSettingsAsync(settings);
        var loadedSettings = await reopened.LoadApplicationSettingsAsync();
        if (loadedSettings.ShowActivityTrace
            || loadedSettings.PersonalInstructions != "Be concise."
            || loadedSettings.GitAuthorName != "Harness Publisher"
            || loadedSettings.GitAuthorEmail != "publisher@users.noreply.github.com"
            || loadedSettings.DefaultGitBranch != "release-main"
            || loadedSettings.PermissionMode != "ask")
        {
            throw new InvalidOperationException("Application settings did not round-trip through SQLite.");
        }

        settings = settings with { PermissionMode = "auto" };
        await reopened.SaveApplicationSettingsAsync(settings);
        loadedSettings = await reopened.LoadApplicationSettingsAsync();
        var latestTokenEvent = await reopened.GetLatestProviderEventPayloadAsync(
            firstSessionId,
            "thread/tokenUsage/updated");
        if (loadedSettings.PermissionMode != "auto"
            || latestTokenEvent is null
            || !latestTokenEvent.Contains("37449", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Permission mode or latest provider telemetry did not survive persistence.");
        }

        var contextProbe = new MainWindowViewModel();
        contextProbe.UpdateTokenUsage(37_449, 24_690_858, 258_400);
        if (contextProbe.ContextUsagePercent is < 14 or > 15
            || contextProbe.ShouldCompactContext()
            || !contextProbe.ContextWindowStatus.Contains("24,690,858 processed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Context occupancy used cumulative thread tokens instead of active input tokens.");
        }
        contextProbe.UpdateTokenUsage(230_000, 24_920_858, 258_400);
        if (!contextProbe.ShouldCompactContext())
            throw new InvalidOperationException("Context compaction did not respond to active input occupancy.");

        var activityProbe = new MainWindowViewModel(previewData: true);
        var conversationAdvances = 0;
        activityProbe.ConversationAdvanced += (_, _) => conversationAdvances++;
        activityProbe.PromptText = "Implement the change";
        activityProbe.BeginTurn();
        activityProbe.StartAssistantMessage("status-probe");
        activityProbe.AppendAssistantDelta("status-probe", "I am continuing with the implementation.");
        activityProbe.CompleteAssistant("status-probe");
        var deliveredMessage = activityProbe.Messages.Single(message => message.Role == "HARNESS" && message.Text.Contains("continuing", StringComparison.Ordinal));
        activityProbe.StartExecutionItem("files-probe", "FILES", "Workspace changes", "", "#E2A84A");
        if (conversationAdvances == 0
            || activityProbe.TurnActivityStatus != "EDITING FILES"
            || deliveredMessage.Status != "DELIVERED"
            || activityProbe.PermissionModes.Select(mode => mode.Id).SequenceEqual(["ask", "auto", "full"]) is false)
        {
            throw new InvalidOperationException("Live chat follow, turn activity, or permission modes are incomplete.");
        }
        activityProbe.CompleteTurn();
        if (deliveredMessage.Status != "COMPLETED")
            throw new InvalidOperationException("Assistant text was marked complete before the provider turn completed.");

        var turnImagePath = Path.Combine(storageProbeWorkspace, "turn-image.png");
        var turnTextPath = Path.Combine(storageProbeWorkspace, "turn-notes.md");
        await File.WriteAllBytesAsync(turnImagePath, [0x89, 0x50, 0x4E, 0x47]);
        await File.WriteAllTextAsync(turnTextPath, "Turn-specific reference");
        activityProbe.AddTurnAttachments([turnImagePath], "image");
        activityProbe.AddTurnAttachments([turnTextPath], "text");
        if (activityProbe.TurnAttachments.Count != 2
            || activityProbe.TurnAttachments[0].MediaType != "image/png"
            || activityProbe.TurnAttachments[1].MediaType != "text/markdown"
            || !activityProbe.CanAttachImage
            || activityProbe.CanAttachVideo
            || !activityProbe.CanAttachText
            || activityProbe.HasUnsupportedTurnAttachments)
        {
            throw new InvalidOperationException("Turn attachments lost type metadata or ignored provider capability gating.");
        }
        activityProbe.ClearTurnAttachments();

        var videoCapabilityProbe = new MainWindowViewModel();
        videoCapabilityProbe.ApplyModels(
        [
            new ModelDescriptor(
                "video-provider",
                "video-model",
                "Video Model",
                ModelCapability.Text | ModelCapability.Vision | ModelCapability.VideoInput,
                IsDefault: true)
        ]);
        if (!videoCapabilityProbe.CanAttachVideo
            || videoCapabilityProbe.VideoAttachmentAvailability != "NATIVE INPUT")
            throw new InvalidOperationException("Provider-advertised video input was not surfaced in the attachment menu.");

        var parsedSkill = SkillManifestParser.Parse(
            "---\nname: physics-tuning\ndescription: Tune stable game physics.\n---\nInstructions",
            "fallback");
        if (parsedSkill.Name != "physics-tuning"
            || parsedSkill.Description != "Tune stable game physics."
            || SkillManifestParser.InferCategory(parsedSkill.Name, parsedSkill.Description, "skills/physics/SKILL.md") != "Game development")
        {
            throw new InvalidOperationException("Skill metadata parsing or category inference failed.");
        }
        var portableManifest = SkillManifestParser.Analyze(
            "---\nname: portable\ndescription: Portable workflow.\nlicense: MIT\n---\nInstructions",
            "fallback");
        var claudeManifest = SkillManifestParser.Analyze(
            "---\nname: claude-only\ndescription: Claude workflow.\ndisable-model-invocation: true\n---\nUse $ARGUMENTS",
            "fallback");
        if (portableManifest.Compatibility != "Portable Agent Skill"
            || claudeManifest.Compatibility != "Claude Code extension")
            throw new InvalidOperationException("Skill compatibility classification did not distinguish the open format from provider extensions.");
        var catalogSkill = new SkillCatalogEntry(
            "skill-catalog-probe",
            parsedSkill.Name,
            parsedSkill.Description,
            "Game development",
            "example/skills",
            "physics-tuning/SKILL.md",
            "0123456789abcdef0123456789abcdef01234567",
            "https://github.com/example/skills/blob/0123456789abcdef0123456789abcdef01234567/physics-tuning/SKILL.md",
            "Portable Agent Skill",
            "UNREVIEWED GITHUB SOURCE",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var catalogSource = new SkillCatalogSource(
            catalogSkill.Repository,
            "example",
            "https://github.com/example/skills",
            428,
            1,
            catalogSkill.SourceRevision,
            "PARTIAL · SEARCH TO EXPAND",
            DateTimeOffset.UtcNow,
            "Progressive metadata index");
        await reopened.UpsertSkillInventoriesAsync([new SkillRepositoryInventory(catalogSource, [catalogSkill])]);
        var matchingSkills = await reopened.SearchSkillCatalogAsync("physics", "Game development");
        var matchingSources = await reopened.ListSkillSourcesAsync();
        if (matchingSkills.Count != 1 || matchingSkills[0].Id != catalogSkill.Id
            || matchingSources.Count != 1 || matchingSources[0].ReportedSkillCount != 428)
            throw new InvalidOperationException("The local Skills catalog did not persist source counts or filter metadata.");

        var packageRoot = Path.Combine(storageProbeRoot, "skill-package");
        Directory.CreateDirectory(packageRoot);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "SKILL.md"),
            "---\nname: physics-tuning\ndescription: Tune stable game physics.\n---\nInstructions");
        var downloaded = new DownloadedSkillPackage(packageRoot, "probe-sha256", 1, 90);
        var installPath = await SkillPackageInstaller.InstallCodexAsync(
            downloaded, catalogSkill, "WORKSPACE", storageProbeWorkspace);
        if (!File.Exists(Path.Combine(installPath, "SKILL.md"))
            || !installPath.Contains(Path.Combine(".agents", "skills"), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(installPath).Contains("example-skills", StringComparison.OrdinalIgnoreCase)
            || !(await File.ReadAllTextAsync(Path.Combine(installPath, "SKILL.md"))).Contains(
                $"name: {SkillPackageInstaller.CreateInstalledSkillName(catalogSkill)}",
                StringComparison.Ordinal)
            || !File.Exists(Path.Combine(Path.GetDirectoryName(installPath)!, ".harness-skill-index.json")))
            throw new InvalidOperationException("The Codex skill adapter did not create a distinct discoverable identity and provider index.");
        var installedSkill = new InstalledSkill(
            SkillPackageInstaller.CreateInstallId(catalogSkill.Id, "openai-codex", "WORKSPACE", storageProbeWorkspace),
            catalogSkill.Id,
            catalogSkill.Name,
            catalogSkill.SourceRevision,
            packageRoot,
            installPath,
            "WORKSPACE",
            storageProbeWorkspace,
            "openai-codex",
            null,
            downloaded.ContentSha256,
            true,
            DateTimeOffset.UtcNow);
        await reopened.SaveInstalledSkillAsync(installedSkill);
        var installedSkills = await reopened.ListInstalledSkillsAsync();
        if (installedSkills.Count != 1 || installedSkills[0].CatalogId != catalogSkill.Id)
            throw new InvalidOperationException("Installed skill provenance did not survive SQLite persistence.");
        await reopened.RemoveAttachmentAsync(attachmentId);
        if (File.Exists(storedAttachmentPath))
        {
            throw new InvalidOperationException(
                "An unreferenced Harness-owned attachment blob was not removed.");
        }
    }
}
finally
{
    Directory.Delete(storageProbeRoot, recursive: true);
}

var gitProbeRoot = Path.Combine(
    Path.GetTempPath(),
    "Harness.GitProbe",
    Guid.NewGuid().ToString("N"));
var gitProbeRecovery = Path.Combine(gitProbeRoot, "recovery");
var gitProbeRepository = Path.Combine(gitProbeRoot, "repository");
Directory.CreateDirectory(gitProbeRepository);
try
{
    await RunGitProbeCommandAsync(gitProbeRepository, "init", "--quiet");
    await RunGitProbeCommandAsync(gitProbeRepository, "config", "user.name", "Harness Probe");
    await RunGitProbeCommandAsync(gitProbeRepository, "config", "user.email", "probe@harness.local");
    var trackedPath = Path.Combine(gitProbeRepository, "tracked.txt");
    var untrackedPath = Path.Combine(gitProbeRepository, "untracked.txt");
    await File.WriteAllTextAsync(trackedPath, "baseline\n");
    await RunGitProbeCommandAsync(gitProbeRepository, "add", "--", "tracked.txt");
    await RunGitProbeCommandAsync(gitProbeRepository, "commit", "--quiet", "-m", "baseline");
    await File.WriteAllTextAsync(trackedPath, "changed\n");
    await File.WriteAllTextAsync(untrackedPath, "new file\n");

    var gitProbe = new GitWorkspaceClient(gitProbeRecovery);
    var status = await gitProbe.ReadStatusAsync(gitProbeRepository);
    var tracked = status.Files.Single(file => file.RelativePath == "tracked.txt");
    var untracked = status.Files.Single(file => file.RelativePath == "untracked.txt");
    if (!status.IsRepository
        || !tracked.HasWorkTreeChanges
        || !untracked.IsUntracked
        || status.Files.Count != 2)
    {
        throw new InvalidOperationException("Git status did not preserve staged/unstaged semantics.");
    }

    var diff = await gitProbe.GetDiffAsync(gitProbeRepository, tracked);
    if (!diff.Contains("-baseline", StringComparison.Ordinal)
        || !diff.Contains("+changed", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The tracked working-tree diff was incomplete.");
    }

    await gitProbe.StageAsync(gitProbeRepository, untracked.RelativePath);
    status = await gitProbe.ReadStatusAsync(gitProbeRepository);
    var stagedUntracked = status.Files.Single(file => file.RelativePath == "untracked.txt");
    if (!stagedUntracked.IsStaged)
    {
        throw new InvalidOperationException("Git stage did not update index state.");
    }
    await gitProbe.UnstageAsync(gitProbeRepository, stagedUntracked.RelativePath);
    status = await gitProbe.ReadStatusAsync(gitProbeRepository);
    untracked = status.Files.Single(file => file.RelativePath == "untracked.txt");
    if (!untracked.IsUntracked)
    {
        throw new InvalidOperationException("Git unstage did not restore untracked state.");
    }

    var trackedRecovery = await gitProbe.RevertWorkTreeAsync(
        gitProbeRepository,
        status.Files.Single(file => file.RelativePath == "tracked.txt"));
    if ((await File.ReadAllTextAsync(trackedPath)).Trim() != "baseline"
        || !Directory.Exists(trackedRecovery.RecoveryPath))
    {
        throw new InvalidOperationException("Tracked-file revert did not create a recovery copy.");
    }

    var untrackedRecovery = await gitProbe.RevertWorkTreeAsync(
        gitProbeRepository,
        untracked);
    if (File.Exists(untrackedPath)
        || !File.Exists(Path.Combine(untrackedRecovery.RecoveryPath, "untracked.txt")))
    {
        throw new InvalidOperationException("Untracked-file revert deleted rather than recovered the file.");
    }

    await File.WriteAllTextAsync(trackedPath, "committed by dock\n");
    await File.WriteAllTextAsync(Path.Combine(gitProbeRepository, "commit-all.txt"), "included\n");
    await gitProbe.CommitAllAsync(gitProbeRepository, "repository dock commit");
    status = await gitProbe.ReadStatusAsync(gitProbeRepository);
    if (status.Files.Count != 0)
    {
        throw new InvalidOperationException("Repository dock commit did not stage and commit all workspace changes.");
    }

    var bareRemote = Path.Combine(gitProbeRoot, "remote.git");
    Directory.CreateDirectory(bareRemote);
    await RunGitProbeCommandAsync(bareRemote, "init", "--bare", "--quiet");
    await RunGitProbeCommandAsync(gitProbeRepository, "remote", "add", "origin", bareRemote);
    var originalBranch = status.Branch ?? throw new InvalidOperationException("Git probe did not report its branch.");
    await gitProbe.PushAsync(gitProbeRepository);
    await gitProbe.RenameCurrentBranchAsync(gitProbeRepository, "published-main");
    await gitProbe.PushAsync(gitProbeRepository);
    if (!await gitProbe.RemoteBranchExistsAsync(gitProbeRepository, originalBranch)
        || !await gitProbe.RemoteBranchExistsAsync(gitProbeRepository, "published-main"))
    {
        throw new InvalidOperationException("Published branch rename did not push the new remote branch.");
    }
    await RunGitProbeCommandAsync(bareRemote, "symbolic-ref", "HEAD", "refs/heads/published-main");
    await gitProbe.DeleteRemoteBranchAsync(gitProbeRepository, originalBranch);
    if (await gitProbe.RemoteBranchExistsAsync(gitProbeRepository, originalBranch)
        || !await gitProbe.RemoteBranchExistsAsync(gitProbeRepository, "published-main"))
    {
        throw new InvalidOperationException("Published branch rename did not remove the old remote branch.");
    }

    var publishProbeRepository = Path.Combine(gitProbeRoot, "publish-repository");
    Directory.CreateDirectory(publishProbeRepository);
    await gitProbe.InitializeRepositoryAsync(publishProbeRepository);
    await gitProbe.ConfigureIdentityAsync(
        publishProbeRepository,
        "Harness Publisher",
        "publisher@users.noreply.github.com");
    await File.WriteAllTextAsync(Path.Combine(publishProbeRepository, "published.txt"), "publish me\n");
    var oversizedProbePath = Path.Combine(publishProbeRepository, "release", "oversized.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(oversizedProbePath)!);
    await using (var oversized = new FileStream(oversizedProbePath, FileMode.CreateNew, FileAccess.Write))
    {
        oversized.SetLength(101L * 1024 * 1024);
    }
    var createdInitialCommit = await gitProbe.PrepareForInitialPushAsync(
        publishProbeRepository,
        "Initial commit");
    var excludedOversized = await gitProbe.ExcludeOversizedFilesAsync(publishProbeRepository);
    var repairedInitialCommit = await gitProbe.PrepareForInitialPushAsync(
        publishProbeRepository,
        "Initial commit",
        amendSingleInitialCommit: true);
    var publishIdentity = await gitProbe.ReadIdentityAsync(publishProbeRepository);
    var publishStatus = await gitProbe.ReadStatusAsync(publishProbeRepository);
    var publishCommitCount = await gitProbe.GetCommitCountAsync(publishProbeRepository);
    var committedCleanTree = await gitProbe.CommitAllAsync(publishProbeRepository, "must not create an empty commit");
    if (!createdInitialCommit
        || !repairedInitialCommit
        || committedCleanTree
        || excludedOversized.Count != 1
        || excludedOversized[0].RelativePath != "release/oversized.exe"
        || !excludedOversized[0].WasTracked
        || !File.Exists(oversizedProbePath)
        || await gitProbe.IsTrackedAsync(publishProbeRepository, "release/oversized.exe")
        || publishIdentity.Name != "Harness Publisher"
        || publishIdentity.Email != "publisher@users.noreply.github.com"
        || publishStatus.Branch != "main"
        || publishCommitCount != 1
        || publishStatus.Files.Count != 0)
    {
        throw new InvalidOperationException("Initial publishing did not repair an oversized unpublished commit.");
    }
}
finally
{
    DeleteGitProbeDirectory(gitProbeRoot);
}

var productionProbe = new MainWindowViewModel();
if (productionProbe.Models.Count != 0
    || productionProbe.Messages.Count != 0
    || productionProbe.UsageWindows.Count != 0
    || productionProbe.ConnectionStatus != "CONNECTING")
{
    throw new InvalidOperationException(
        "Production startup must not contain preview models, messages, or usage data.");
}

productionProbe.ApplyWorkingTree(new WorkingTreeSnapshot(
    true,
    storageProbeWorkspace,
    "main",
    []));
productionProbe.ApplyRepositoryRemote("https://github.com/example/project.git");
if (!productionProbe.RepositoryDockLabel.Contains("SIGN IN TO GITHUB", StringComparison.Ordinal)
    || productionProbe.CanUseRemoteActions)
{
    throw new InvalidOperationException("A configured Git remote was incorrectly treated as authenticated GitHub.");
}
productionProbe.ApplyGitHubConnection(true);
if (!productionProbe.CanUseRemoteActions
    || productionProbe.RepositoryDockLabel.Contains("SIGN IN", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Authenticated GitHub state was not projected into repository actions.");
}
productionProbe.SetRepositoryOperationStatus("Current branch pushed");
if (productionProbe.RepositoryOperationStatus != "Current branch pushed"
    || productionProbe.RepositoryOperationColor != "#65C7D0")
{
    throw new InvalidOperationException("Primary repository actions did not expose visible success feedback.");
}
productionProbe.BeginRepositoryRefresh("Next workspace");
if (productionProbe.RepositoryRoot is not null
    || productionProbe.CanUseRepositoryActions
    || productionProbe.CanUseRemoteActions
    || productionProbe.BranchStatus != "BRANCH · LOADING")
{
    throw new InvalidOperationException("Workspace switching left stale repository actions enabled.");
}

var sessionNamingProbe = new MainWindowViewModel();
var namingProject = new StoredProject(
    "project-naming",
    "Naming",
    storageProbeWorkspace,
    DateTimeOffset.UtcNow,
    DateTimeOffset.UtcNow);
var namingSession = new StoredSession(
    "session-naming",
    namingProject.Id,
    "New session",
    null,
    null,
    null,
    null,
    null,
    DateTimeOffset.UtcNow,
    DateTimeOffset.UtcNow);
sessionNamingProbe.ApplyWorkspaceSnapshot(new WorkspaceSessionSnapshot(
    namingProject,
    [namingSession],
    namingSession,
    [],
    []));
sessionNamingProbe.RenameStoredSession(namingSession.Id, "Persist this conversation");
if (sessionNamingProbe.CurrentSessionTitle != "Persist this conversation"
    || sessionNamingProbe.Tasks[0].Title != "Persist this conversation")
{
    throw new InvalidOperationException("A renamed durable session did not update the task rail and title bar.");
}

var modelProbe = new MainWindowViewModel(previewData: true);
modelProbe.UpdateTokenUsage(90_000, 90_000, 100_000);
if (!modelProbe.ReasoningLevels.Any(level => level.Id == "none")
    || !modelProbe.ReasoningLevels.Any(level => level.Id == "max")
    || modelProbe.SelectedReasoningLevel?.Id != "medium"
    || modelProbe.ServiceTiers.Select(tier => tier.DisplayName).SequenceEqual(["Standard", "Fast"]) is false
    || modelProbe.SelectedServiceTier?.Id is not null
    || modelProbe.ContextStatus != "1 ATTACHED"
    || modelProbe.UsageWindows[0].Label != "5 HOUR WINDOW"
    || modelProbe.UsageWindows[1].Label != "WEEKLY LIMIT"
    || !modelProbe.ShouldCompactContext()
    || !modelProbe.TokenStatus.Contains("90%", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Preview model metadata is incomplete.");
}
modelProbe.SetContextCompaction(true);
modelProbe.SetContextCompaction(false);
if (modelProbe.ShouldCompactContext())
{
    throw new InvalidOperationException("Context compaction can immediately repeat without additional token growth.");
}

var capabilityProbe = new MainWindowViewModel();
capabilityProbe.ApplyModels(
[
    new ModelDescriptor(
        "provider-a",
        "model-a",
        "Model A",
        ModelCapability.Text | ModelCapability.Reasoning,
        ReasoningLevels:
        [
            new("low", "Low", IsDefault: true),
            new("high", "High")
        ],
        ServiceTiers:
        [
            new(null, "Standard", IsDefault: true),
            new("priority", "Fast")
        ],
        IsDefault: true),
    new ModelDescriptor(
        "provider-b",
        "model-b",
        "Model B",
        ModelCapability.Text,
        ReasoningLevels: [new("automatic", "Automatic", IsDefault: true)])
]);
if (capabilityProbe.ReasoningLevels.Select(option => option.Id).SequenceEqual(["low", "high"]) is false
    || capabilityProbe.ServiceTiers.Select(option => option.Id).SequenceEqual([null, "priority"]) is false)
{
    throw new InvalidOperationException("The first model's reported controls were not projected exactly.");
}

capabilityProbe.SelectedModel = capabilityProbe.Models[1];
if (capabilityProbe.ReasoningLevels.Select(option => option.Id).SequenceEqual(["automatic"]) is false
    || capabilityProbe.SelectedReasoningLevel?.Id != "automatic"
    || capabilityProbe.ServiceTiers.Count != 0
    || capabilityProbe.SelectedServiceTier is not null)
{
    throw new InvalidOperationException("Model switching leaked controls from the previously selected model.");
}

capabilityProbe.ApplySessionModelSettings(new StoredSession(
    "model-continuity",
    "project-continuity",
    "Model continuity",
    "provider-a",
    "thread-continuity",
    "model-a",
    "high",
    "priority",
    DateTimeOffset.UtcNow,
    DateTimeOffset.UtcNow));
if (capabilityProbe.SelectedModel?.ProviderId != "provider-a"
    || capabilityProbe.SelectedModel.ModelName != "model-a"
    || capabilityProbe.SelectedReasoningLevel?.Id != "high"
    || capabilityProbe.SelectedServiceTier?.Id != "priority")
{
    throw new InvalidOperationException("Persisted provider, model, reasoning, and service-tier settings were not restored together.");
}

var messageStreamProbe = new MainWindowViewModel();
messageStreamProbe.StartAssistantMessage("commentary-1");
messageStreamProbe.AppendAssistantDelta("commentary-1", "First update stays visible.");
messageStreamProbe.CompleteAssistant("commentary-1", "First update stays visible.");
messageStreamProbe.StartAssistantMessage("final-1");
messageStreamProbe.AppendAssistantDelta("final-1", "Final answer stays separate.");
messageStreamProbe.CompleteAssistant("final-1", "Final answer stays separate.");
if (messageStreamProbe.Messages.Count != 2
    || messageStreamProbe.Messages[0].Text != "First update stays visible."
    || messageStreamProbe.Messages[1].Text != "Final answer stays separate.")
{
    throw new InvalidOperationException("Separate provider response items overwrote each other in chat.");
}

var commandPreview = modelProbe.ExecutionItems.SingleOrDefault(item => item.Kind == "COMMAND");
if (commandPreview?.Status != "EXIT 0"
    || !commandPreview.Detail.Contains("Build succeeded.", StringComparison.Ordinal)
    || modelProbe.Messages.Any(item => item.Role == "COMMAND")
    || modelProbe.Messages.Count(item => item.Role == "REPORT") != 1
    || modelProbe.Messages.Single(item => item.Role == "REPORT").Text.Contains("dotnet build", StringComparison.OrdinalIgnoreCase)
    || modelProbe.ChangedFiles.Count != 1
    || !modelProbe.HasTurnDiff)
{
    throw new InvalidOperationException("Execution events and turn changes are not projected correctly.");
}

for (var index = 0; index < 2000; index++)
{
    modelProbe.AppendExecutionDelta("preview-command", "OUTPUT", "Command output", new string('x', 128), "#E2A84A", true);
}
if (modelProbe.Messages.Any(item => item.Role == "OUTPUT")
    || commandPreview.Detail.Length > 49 * 1024)
{
    throw new InvalidOperationException("Verbose execution output leaked into chat or escaped its memory bound.");
}

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions
    {
        UseHeadlessDrawing = false
    })
    .SetupWithoutStarting();

var window = new MainWindow(usePreviewData: true)
{
    Width = 1440,
    Height = 900
};

window.Show();
var promptBox = window.FindControl<TextBox>("PromptBox")
    ?? throw new InvalidOperationException("Prompt box was not created.");
promptBox.Text = "first line";
promptBox.CaretIndex = promptBox.Text.Length;
promptBox.RaiseEvent(new KeyEventArgs
{
    RoutedEvent = InputElement.KeyDownEvent,
    Key = Key.Enter,
    KeyModifiers = KeyModifiers.Control
});
if (promptBox.Text != $"first line{Environment.NewLine}")
{
    throw new InvalidOperationException("Ctrl+Enter must insert a line break.");
}
promptBox.Text = "send this";
promptBox.CaretIndex = promptBox.Text.Length;
promptBox.RaiseEvent(new KeyEventArgs
{
    RoutedEvent = InputElement.KeyDownEvent,
    Key = Key.Enter,
    KeyModifiers = KeyModifiers.None
});
if (promptBox.Text.Contains('\n') || promptBox.Text.Contains('\r'))
{
    throw new InvalidOperationException("Enter must not insert a line break.");
}
promptBox.Text = string.Empty;

var maximizeButton = window.FindControl<Button>("MaximizeButton")
    ?? throw new InvalidOperationException("Custom maximize button was not created.");
maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
if (window.WindowState != WindowState.Maximized)
{
    throw new InvalidOperationException("Custom maximize action did not maximize the window.");
}

var maximizedPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
    $"{Path.GetFileNameWithoutExtension(outputPath)}-maximized{Path.GetExtension(outputPath)}");
using (var maximizedFrame = window.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not render the maximized window."))
{
    maximizedFrame.Save(maximizedPath);
}

maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
if (window.WindowState != WindowState.Normal)
{
    throw new InvalidOperationException("Custom maximize action did not restore the window.");
}

using var frame = window.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not produce a rendered frame.");
frame.Save(outputPath);

var modelPicker = window.FindControl<ComboBox>("ModelPicker")
    ?? throw new InvalidOperationException("Model picker was not created.");
modelPicker.IsDropDownOpen = true;
var dropdownPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
    $"{Path.GetFileNameWithoutExtension(outputPath)}-dropdown{Path.GetExtension(outputPath)}");
using (var dropdownFrame = window.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not render the open model picker."))
{
    dropdownFrame.Save(dropdownPath);
}

modelPicker.IsDropDownOpen = false;
_ = window.FindControl<Button>("OpenWorkingTreeButton")
    ?? throw new InvalidOperationException("The compact working-tree module launcher was not created.");
_ = window.FindControl<Button>("RepositoryDockButton")
    ?? throw new InvalidOperationException("The compact repository dock was not created.");
_ = window.FindControl<Button>("RepositoryCommitButton")
    ?? throw new InvalidOperationException("The repository commit action was not created.");
_ = window.FindControl<Button>("RepositoryPullButton")
    ?? throw new InvalidOperationException("The repository pull action was not created.");
_ = window.FindControl<Button>("RepositoryPushButton")
    ?? throw new InvalidOperationException("The repository push action was not created.");
_ = window.FindControl<ComboBox>("PermissionModePicker")
    ?? throw new InvalidOperationException("The workspace did not expose its permission mode.");
var attachmentButton = window.FindControl<Button>("AttachmentMenuButton")
    ?? throw new InvalidOperationException("The composer did not expose the attachment menu.");
_ = window.FindControl<Button>("SkillsLibraryButton")
    ?? throw new InvalidOperationException("The command strip did not expose the Skills Library bookshelf shortcut.");

var previewViewModel = (MainWindowViewModel)window.DataContext!;
previewViewModel.AddTurnAttachments([Path.GetFullPath(outputPath)], "image");
previewViewModel.AddTurnAttachments([Path.GetFullPath("README.md")], "text");
attachmentButton.Flyout?.ShowAt(attachmentButton);
Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
var attachmentMenuPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
    $"{Path.GetFileNameWithoutExtension(outputPath)}-attachments{Path.GetExtension(outputPath)}");
using (var attachmentMenuFrame = window.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not render the attachment menu."))
{
    attachmentMenuFrame.Save(attachmentMenuPath);
}
attachmentButton.Flyout?.Hide();
var generatedImageMessage = ChatMessageItem.Assistant(
    $"Generated image:\n\n![Harness preview]({Path.GetFullPath(outputPath)})");
generatedImageMessage.SetStatus("COMPLETED");
previewViewModel.Messages.Add(generatedImageMessage);
if (generatedImageMessage.Images.Count != 1
    || generatedImageMessage.Images[0].FullPath != Path.GetFullPath(outputPath))
{
    throw new InvalidOperationException("Generated image links did not become inline chat previews.");
}
window.FindControl<ScrollViewer>("ConversationScrollViewer")?.ScrollToEnd();
var imagePreviewPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
    $"{Path.GetFileNameWithoutExtension(outputPath)}-image-preview{Path.GetExtension(outputPath)}");
using (var imagePreviewFrame = window.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not render the generated image card."))
{
    imagePreviewFrame.Save(imagePreviewPath);
}

var restoreSession = new StoredSession(
    "restore-scroll-session",
    "preview-project",
    "Restored conversation",
    "preview",
    "preview-thread",
    "gpt-5.6-sol",
    "max",
    "priority",
    DateTimeOffset.UtcNow,
    DateTimeOffset.UtcNow);
var restoreMessages = Enumerable.Range(0, 36)
    .Select(index => new StoredMessage(
        $"restore-message-{index}",
        restoreSession.Id,
        index,
        index % 2 == 0 ? "YOU" : "HARNESS",
        index % 2 == 0 ? "Prompt" : "Response",
        $"Persisted message {index + 1}: {new string('x', 180)}",
        "COMPLETED",
        index % 2 == 0 ? "#8993A3" : "#65C7D0",
        false,
        DateTimeOffset.UtcNow.AddMinutes(index)))
    .ToArray();
previewViewModel.ApplyStoredSession(restoreSession, restoreMessages);
Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
var conversationScroll = window.FindControl<ScrollViewer>("ConversationScrollViewer")
    ?? throw new InvalidOperationException("Conversation scroll surface was not created.");
conversationScroll.UpdateLayout();
var maximumConversationOffset = Math.Max(0, conversationScroll.Extent.Height - conversationScroll.Viewport.Height);
if (conversationScroll.Offset.Y < maximumConversationOffset - 2)
{
    throw new InvalidOperationException("A restored conversation did not open at its latest message.");
}
window.Close();

var workingTreeViewModel = new WorkingTreeWindowViewModel();
workingTreeViewModel.Apply(new WorkingTreeSnapshot(
    true,
    "E:\\Dev Projects\\AI Harness",
    "codex/persistence",
    [
        new WorkingTreeFile("src/Harness.App/Views/MainWindow.axaml", ' ', 'M', false),
        new WorkingTreeFile("src/Harness.App/Views/WorkingTreeWindow.axaml", 'A', ' ', false),
        new WorkingTreeFile("docs/working-tree.md", '?', '?', true)
    ]));
workingTreeViewModel.DiffText =
    "WORKING TREE\n\n--- a/MainWindow.axaml\n+++ b/MainWindow.axaml\n@@ -1 +1 @@\n-old\n+new";
var workingTreeWindow = new WorkingTreeWindow
{
    Width = 1120,
    Height = 760,
    DataContext = workingTreeViewModel
};
workingTreeWindow.Show();
_ = workingTreeWindow.FindControl<Button>("RenameBranchButton")
    ?? throw new InvalidOperationException("Working Tree did not expose branch renaming.");
var workingTreePath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
    $"{Path.GetFileNameWithoutExtension(outputPath)}-working-tree{Path.GetExtension(outputPath)}");
using (var workingTreeFrame = workingTreeWindow.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not render the working-tree module."))
{
    workingTreeFrame.Save(workingTreePath);
}
workingTreeWindow.Close();

var settingsWindow = new SettingsWindow
{
    Width = 1480,
    Height = 880
};
settingsWindow.Show();
var settingsTabs = settingsWindow.FindControl<TabControl>("SettingsTabs")
    ?? throw new InvalidOperationException("The Settings category navigator was not created.");
var settingsViewModel = (SettingsWindowViewModel)settingsWindow.DataContext!;
var previewSkills = new[]
{
    ("game-feel", "Add shake, hit-stop, easing, and layered feedback without losing responsiveness.", "Game development", "community/indie-skills"),
    ("repo-mapper", "Map repository structure, dependencies, and ownership before making changes.", "DevOps", "community/indie-skills"),
    ("secure-auth-audit", "Audit authentication flows and identify common authorization failures.", "Security", "sec-labs/agent-skills"),
    ("ui-wireframe-planner", "Generate implementation-ready interface structure from product requirements.", "Frontend", "design-labs/skillbook"),
    ("test-orchestrator", "Coordinate unit, integration, and end-to-end verification across services.", "Testing", "community/indie-skills"),
    ("agent-memory-toolkit", "Build durable context summaries without flooding the active model window.", "Data", "design-labs/skillbook")
}.Select((item, index) => new SkillCatalogEntry(
    $"preview-skill-{index}", item.Item1, item.Item2, item.Item3, item.Item4,
    $"skills/{item.Item1}/SKILL.md",
    $"abcdef0123456789abcdef0123456789abcde{index:00}",
    $"https://github.com/{item.Item4}/blob/abcdef0123456789abcdef0123456789abcde{index:00}/skills/{item.Item1}/SKILL.md",
    index == 2 ? "Claude Code extension" : "Portable Agent Skill",
    "UNREVIEWED GITHUB SOURCE",
    DateTimeOffset.UtcNow.AddMinutes(-index), DateTimeOffset.UtcNow.AddMinutes(-index))).ToArray();
var previewSources = new[]
{
    new SkillCatalogSource("community/indie-skills", "community", "https://github.com/community/indie-skills", 1284, 1284, previewSkills[0].SourceRevision, "COMPLETE PATH INDEX", DateTimeOffset.UtcNow, DescribedSkillCount: 3),
    new SkillCatalogSource("sec-labs/agent-skills", "sec-labs", "https://github.com/sec-labs/agent-skills", 147, 147, previewSkills[2].SourceRevision, "COMPLETE PATH INDEX", DateTimeOffset.UtcNow, DescribedSkillCount: 1),
    new SkillCatalogSource("design-labs/skillbook", "design-labs", "https://github.com/design-labs/skillbook", 83, 83, previewSkills[3].SourceRevision, "COMPLETE PATH INDEX", DateTimeOffset.UtcNow, DescribedSkillCount: 2)
};
settingsViewModel.SetCompatibilityTargets(
[
    new SkillCompatibilityOption("openai-codex:gpt-5.6-sol", "openai-codex", "gpt-5.6-sol", "OpenAI Codex · GPT-5.6-Sol"),
    new SkillCompatibilityOption("anthropic-claude:claude-opus", "anthropic-claude", "claude-opus", "Anthropic Claude · Opus")
]);
settingsViewModel.SelectedSkillCompatibility = settingsViewModel.SkillCompatibilityOptions[1];
settingsViewModel.ReplaceSkills(previewSkills, [], previewSources);
settingsTabs.SelectedIndex = 5;
_ = settingsWindow.FindControl<TextBox>("SkillSearchBox")
    ?? throw new InvalidOperationException("Skills settings did not expose catalog search.");
_ = settingsWindow.FindControl<Button>("SkillInstallButton")
    ?? throw new InvalidOperationException("Skills settings did not expose explicit installation.");
_ = settingsWindow.FindControl<Button>("SkillSyncButton")
    ?? throw new InvalidOperationException("Skills settings did not expose repository inventory sync.");
var skillsSettingsPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
    $"{Path.GetFileNameWithoutExtension(outputPath)}-skills{Path.GetExtension(outputPath)}");
using (var skillsSettingsFrame = settingsWindow.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not render the Skills Library settings section."))
{
    skillsSettingsFrame.Save(skillsSettingsPath);
}
settingsTabs.SelectedIndex = 6;
_ = settingsWindow.FindControl<Button>("GitHubConnectButton")
    ?? throw new InvalidOperationException("GitHub settings did not expose account connection.");
_ = settingsWindow.FindControl<Button>("GitHubRefreshButton")
    ?? throw new InvalidOperationException("GitHub settings did not expose connection refresh.");
settingsTabs.SelectedIndex = 7;
_ = settingsWindow.FindControl<ComboBox>("SettingsPermissionModePicker")
    ?? throw new InvalidOperationException("Advanced settings did not expose the persistent permission mode.");
var advancedSettingsPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
    $"{Path.GetFileNameWithoutExtension(outputPath)}-advanced{Path.GetExtension(outputPath)}");
using (var advancedSettingsFrame = settingsWindow.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not render advanced settings."))
{
    advancedSettingsFrame.Save(advancedSettingsPath);
}
if (settingsWindow.FindControl<TextBox>("GitAuthorNameBox") is not null
    || settingsWindow.FindControl<TextBox>("GitAuthorEmailBox") is not null)
    throw new InvalidOperationException("GitHub settings still exposed workspace repository controls.");
var githubSettingsPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
    $"{Path.GetFileNameWithoutExtension(outputPath)}-github-settings{Path.GetExtension(outputPath)}");
using (var githubSettingsFrame = settingsWindow.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not render GitHub settings."))
{
    githubSettingsFrame.Save(githubSettingsPath);
}
settingsTabs.SelectedIndex = 1;
var settingsPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
    $"{Path.GetFileNameWithoutExtension(outputPath)}-settings{Path.GetExtension(outputPath)}");
using (var settingsFrame = settingsWindow.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not render Settings."))
{
    settingsFrame.Save(settingsPath);
}
settingsWindow.Close();

var executionWindow = new ExecutionWindow
{
    Width = 1060,
    Height = 700,
    DataContext = modelProbe
};
executionWindow.Show();
var executionPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
    $"{Path.GetFileNameWithoutExtension(outputPath)}-activity{Path.GetExtension(outputPath)}");
using (var executionFrame = executionWindow.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not render the bounded Activity module."))
{
    executionFrame.Save(executionPath);
}
executionWindow.Close();

var diffWindow = new DiffWindow
{
    Width = 1040,
    Height = 720,
    DataContext = new DiffWindowViewModel(
        "src/Harness.App/Views/MainWindow.axaml",
        "diff --git a/MainWindow.axaml b/MainWindow.axaml\n--- a/MainWindow.axaml\n+++ b/MainWindow.axaml\n@@ -4,2 +4,3 @@\n same\n-old\n+new\n+extra")
};
diffWindow.Show();
var turnDiffPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
    $"{Path.GetFileNameWithoutExtension(outputPath)}-turn-diff{Path.GetExtension(outputPath)}");
using (var diffFrame = diffWindow.CaptureRenderedFrame()
    ?? throw new InvalidOperationException("Avalonia did not render the structured turn diff."))
{
    diffFrame.Save(turnDiffPath);
}
diffWindow.Close();

Console.WriteLine(Path.GetFullPath(outputPath));

static async Task RunGitProbeCommandAsync(string workingDirectory, params string[] arguments)
{
    var start = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using var process = Process.Start(start)
        ?? throw new InvalidOperationException("Git probe could not start Git.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Git probe failed: {await error}\n{await output}");
    }
}

static void DeleteGitProbeDirectory(string path)
{
    var fullPath = Path.GetFullPath(path);
    var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Harness.GitProbe"))
        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!fullPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Refusing to clean a Git probe outside its temp root.");
    }
    if (!Directory.Exists(fullPath))
    {
        return;
    }

    foreach (var entry in Directory.EnumerateFileSystemEntries(
        fullPath,
        "*",
        SearchOption.AllDirectories))
    {
        File.SetAttributes(entry, FileAttributes.Normal);
    }
    Directory.Delete(fullPath, recursive: true);
}
