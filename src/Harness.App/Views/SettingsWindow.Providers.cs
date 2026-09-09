using Avalonia.Controls;
using Avalonia.Interactivity;
using Harness.App.Services;
using Harness.App.ViewModels;
using Harness.Core.Models;
using Harness.Providers.Api;
using Harness.Providers.Codex;

namespace Harness.App.Views;

public sealed partial class SettingsWindow
{
    private readonly ApiConnectionStore _apiStore = new();
    private readonly Func<Task>? _apiConnectionsChanged;
    private IReadOnlyList<SavedApiConnection> _savedApiConnections = [];
    private IReadOnlyList<ApiModel> _apiModels = [];
    private string? _editingApiConnection;
    private bool _apiBusy;
    private readonly Func<CancellationToken, Task<SubscriptionConnectionSnapshot>>? _readCodexConnection;
    private readonly Func<CancellationToken, Task<CodexDeviceCodeLoginStart>>? _beginCodexSignIn;
    private readonly Func<CancellationToken, Task>? _signOutCodex;
    private bool _codexBusy;
    private IReadOnlyList<SubscriptionIdentity> _codexIdentities = [];
    private readonly Func<CancellationToken, Task<SubscriptionConnectionSnapshot>>? _readClaudeConnection;
    private readonly Func<CancellationToken, Task>? _signInClaude;
    private readonly Func<CancellationToken, Task>? _signOutClaude;
    private bool _claudeBusy;
    private IReadOnlyList<SubscriptionIdentity> _claudeIdentities = [];
    private bool _applyingIdentitySelection;

    private async Task RefreshSubscriptionIdentitiesAsync(string? selectIdentityId = null)
    {
        if (_subscriptionIdentityActions is null)
        {
            CodexIdentityPicker.ItemsSource = Array.Empty<SubscriptionIdentity>();
            CodexUseIdentityButton.IsEnabled = false;
            CodexRemoveIdentityButton.IsEnabled = false;
            return;
        }

        var snapshot = await _subscriptionIdentityActions.Read(_lifetime.Token);
        _codexIdentities = snapshot.Identities;
        CodexUsageCards.ItemsSource = _codexIdentities;
        ViewModel.ActiveCodexIdentityId = snapshot.ActiveIdentityId;
        CodexIdentityPicker.ItemsSource = _codexIdentities;
        CodexIdentityPicker.SelectedItem = _codexIdentities.FirstOrDefault(identity =>
            identity.Id == (selectIdentityId ?? snapshot.ActiveIdentityId))
            ?? _codexIdentities.FirstOrDefault();
        ApplySelectedIdentity();
    }

    private void CodexIdentity_OnChanged(object? sender, SelectionChangedEventArgs e) =>
        ApplySelectedIdentity();

    private void ApplySelectedIdentity()
    {
        if (CodexIdentityPicker.SelectedItem is not SubscriptionIdentity identity) return;
        var isActive = identity.Id == ViewModel.ActiveCodexIdentityId;
        CodexIdentityName.Text = identity.DisplayName;
        CodexIdentityActive.IsVisible = isActive;
        CodexIdentityUsage.Text = identity.UsageLabel;
        _applyingIdentitySelection = true;
        CodexAutomaticHandoffToggle.IsChecked = identity.AutomaticHandoffEnabled;
        _applyingIdentitySelection = false;
        CodexUseIdentityButton.IsEnabled = !isActive;
        CodexRemoveIdentityButton.IsEnabled = !isActive && _codexIdentities.Count > 1;
        if (!isActive)
        {
            CodexAccountStatus.Text = identity.AccountLabel;
            CodexRuntimeStatus.Text = "SAVED PROFILE · SELECT USE ACCOUNT TO ACTIVATE";
            CodexModelCount.Text = "MODELS · PROFILE INACTIVE";
            CodexModelList.Text = identity.LastModelIds is { Count: > 0 }
                ? string.Join("  ·  ", identity.LastModelIds)
                : "Model availability has not been reported for this account.";
            CodexSignInButton.IsVisible = false;
            CodexSignOutButton.IsVisible = false;
        }
    }

    private async void CodexAutomaticHandoff_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_applyingIdentitySelection || _subscriptionIdentityActions?.SetAutomaticHandoff is null
            || CodexIdentityPicker.SelectedItem is not SubscriptionIdentity identity) return;
        await RunAsync("Updating OpenAI handoff participation…", async () =>
        {
            await _subscriptionIdentityActions.SetAutomaticHandoff(
                identity.Id, CodexAutomaticHandoffToggle.IsChecked == true, _lifetime.Token);
            await RefreshSubscriptionIdentitiesAsync(identity.Id);
        });
    }

    private async void CodexUseIdentity_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_subscriptionIdentityActions is null
            || CodexIdentityPicker.SelectedItem is not SubscriptionIdentity identity
            || identity.Id == ViewModel.ActiveCodexIdentityId) return;
        await RunAsync($"Switching to {identity.DisplayName}…", async () =>
        {
            await _subscriptionIdentityActions.Activate(identity.Id, _lifetime.Token);
            ViewModel.ActiveCodexIdentityId = identity.Id;
            await RefreshSubscriptionIdentitiesAsync(identity.Id);
            await RefreshCodexConnectionAsync();
            ViewModel.Status = $"{identity.DisplayName} is now the active OpenAI account";
            RecordActivity("ACCOUNT", $"Activated · {identity.DisplayName}", outcome: "COMPLETED");
        });
    }

    private async void CodexAddIdentity_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_subscriptionIdentityActions is null) return;
        await RunAsync("Adding an isolated OpenAI account…", async () =>
        {
            var identity = await _subscriptionIdentityActions.Add(
                CodexIdentityNameInput.Text,
                _lifetime.Token);
            CodexIdentityNameInput.Text = "";
            await _subscriptionIdentityActions.Activate(identity.Id, _lifetime.Token);
            ViewModel.ActiveCodexIdentityId = identity.Id;
            await RefreshSubscriptionIdentitiesAsync(identity.Id);
            await RefreshCodexConnectionAsync();
            ViewModel.Status = $"{identity.DisplayName} added. Sign in to connect this isolated account.";
            RecordActivity(
                "ACCOUNT",
                $"Added · {identity.DisplayName}",
                "Isolated OpenAI subscription profile created",
                "READY",
                true);
        });
    }

    private async void CodexRemoveIdentity_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_subscriptionIdentityActions is null
            || CodexIdentityPicker.SelectedItem is not SubscriptionIdentity identity
            || identity.Id == ViewModel.ActiveCodexIdentityId) return;
        await RunAsync($"Removing {identity.DisplayName} from Harness…", async () =>
        {
            await _subscriptionIdentityActions.Remove(identity.Id, _lifetime.Token);
            await RefreshSubscriptionIdentitiesAsync();
            ViewModel.Status = $"{identity.DisplayName} removed from Harness. Provider-owned profile files were preserved.";
            RecordActivity("ACCOUNT", $"Removed · {identity.DisplayName}", "Provider-owned profile files were preserved", "COMPLETED", true);
        });
    }

    public async Task RefreshCodexConnectionAsync()
    {
        if (_codexBusy) return;
        if (CodexIdentityPicker.SelectedItem is SubscriptionIdentity selected
            && selected.Id != ViewModel.ActiveCodexIdentityId)
        {
            ApplySelectedIdentity();
            return;
        }
        _codexBusy = true;
        CodexConnectionPanel.IsEnabled = false;
        CodexAccountStatus.Text = "Checking account…";
        try
        {
            var snapshot = _readCodexConnection is null
                ? SubscriptionConnectionSnapshot.Unavailable("Open Harness normally to inspect the connected account.")
                : await _readCodexConnection(_lifetime.Token);
            CodexAccountStatus.Text = snapshot.AccountLabel;
            CodexRuntimeStatus.Text = snapshot.Detail;
            CodexModelCount.Text = $"MODELS · {snapshot.Models.Count:N0} REPORTED";
            CodexModelList.Text = snapshot.Models.Count == 0
                ? snapshot.IsAuthenticated ? "The provider did not report any available models." : "Sign in to discover available models."
                : string.Join("  ·  ", snapshot.Models);
            CodexSignInButton.IsVisible = snapshot.RuntimeAvailable && !snapshot.IsAuthenticated;
            CodexSignOutButton.IsVisible = snapshot.RuntimeAvailable && snapshot.IsAuthenticated;
            RefreshProviderDerivedOptions();
            if (CodexIdentityPicker.SelectedItem is SubscriptionIdentity active)
            {
                CodexIdentityName.Text = active.DisplayName;
                CodexIdentityActive.IsVisible = true;
                CodexUseIdentityButton.IsEnabled = false;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CodexAccountStatus.Text = "Connection unavailable";
            CodexRuntimeStatus.Text = exception.Message.Replace("\r", " ").Replace("\n", " ");
            CodexModelCount.Text = "MODELS · UNAVAILABLE";
            CodexModelList.Text = "Harness could not read the provider catalog.";
            CodexSignInButton.IsVisible = false;
            CodexSignOutButton.IsVisible = false;
        }
        finally
        {
            _codexBusy = false;
            CodexConnectionPanel.IsEnabled = true;
        }
    }

    private async void CodexRefresh_OnClick(object? sender, RoutedEventArgs e)
    {
        await RefreshSubscriptionIdentitiesAsync(
            (CodexIdentityPicker.SelectedItem as SubscriptionIdentity)?.Id);
        await RefreshCodexConnectionAsync();
    }

    private async void CodexSignIn_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_codexBusy || _beginCodexSignIn is null) return;
        await RunAsync("Opening OpenAI sign-in…", async () =>
        {
            var login = await _beginCodexSignIn(_lifetime.Token);
            await Launcher.LaunchUriAsync(new Uri(login.VerificationUrl));
            await OpenAiDeviceCodeDialog.ShowAsync(this, login);
            await RefreshCodexConnectionAsync();
            await RefreshSubscriptionIdentitiesAsync(ViewModel.ActiveCodexIdentityId);
            RecordActivity("ACCOUNT", "OpenAI sign-in completed", outcome: "COMPLETED", isMilestone: true);
        });
    }

    private async void CodexSignOut_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_codexBusy || _signOutCodex is null) return;
        await RunAsync("Signing out of OpenAI…", async () =>
        {
            await _signOutCodex(_lifetime.Token);
            await RefreshCodexConnectionAsync();
            await RefreshSubscriptionIdentitiesAsync(ViewModel.ActiveCodexIdentityId);
            ViewModel.Status = "Signed out of OpenAI. Local workspaces and chats were preserved.";
            RecordActivity("ACCOUNT", "Signed out of OpenAI", "Local workspaces and chats were preserved", "COMPLETED", true);
        });
    }

    public async Task RefreshClaudeConnectionAsync()
    {
        if (_claudeBusy) return;
        if (ClaudeIdentityPicker.SelectedItem is SubscriptionIdentity selected
            && selected.Id != ViewModel.ActiveClaudeIdentityId)
        {
            ApplySelectedClaudeIdentity();
            return;
        }
        _claudeBusy = true;
        ClaudeConnectionPanel.IsEnabled = false;
        ClaudeAccountStatus.Text = "Checking account…";
        try
        {
            var snapshot = _readClaudeConnection is null
                ? SubscriptionConnectionSnapshot.Unavailable("Open Harness normally to inspect Claude Code.")
                : await _readClaudeConnection(_lifetime.Token);
            ClaudeAccountStatus.Text = snapshot.AccountLabel;
            ClaudeRuntimeStatus.Text = snapshot.Detail;
            ClaudeModelCount.Text = $"MODELS · {snapshot.Models.Count:N0} REPORTED";
            ClaudeModelList.Text = snapshot.Models.Count == 0
                ? snapshot.IsAuthenticated
                    ? "Claude Code did not report selectable models for this account."
                    : snapshot.RuntimeAvailable ? "Sign in to discover available models." : "Install Claude Code, then refresh."
                : string.Join("  ·  ", snapshot.Models);
            ClaudeSignInButton.IsVisible = snapshot.RuntimeAvailable && !snapshot.IsAuthenticated;
            ClaudeSignOutButton.IsVisible = snapshot.RuntimeAvailable && snapshot.IsAuthenticated;
            ClaudeInstallHelpButton.IsVisible = !snapshot.RuntimeAvailable;
            RefreshProviderDerivedOptions();
            if (ClaudeIdentityPicker.SelectedItem is SubscriptionIdentity active)
            {
                ClaudeIdentityName.Text = active.DisplayName;
                ClaudeIdentityActive.IsVisible = true;
                ClaudeUseIdentityButton.IsEnabled = false;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ClaudeAccountStatus.Text = "Connection unavailable";
            ClaudeRuntimeStatus.Text = exception.Message.Replace("\r", " ").Replace("\n", " ");
            ClaudeModelCount.Text = "MODELS · UNAVAILABLE";
            ClaudeModelList.Text = "Harness could not read the Claude Code provider catalog.";
            ClaudeSignInButton.IsVisible = false;
            ClaudeSignOutButton.IsVisible = false;
            ClaudeInstallHelpButton.IsVisible = true;
        }
        finally
        {
            _claudeBusy = false;
            ClaudeConnectionPanel.IsEnabled = true;
        }
    }

    private async void ClaudeRefresh_OnClick(object? sender, RoutedEventArgs e) =>
        await RefreshClaudeAccountsAndConnectionAsync();

    private async Task RefreshClaudeAccountsAndConnectionAsync()
    {
        await RefreshClaudeSubscriptionIdentitiesAsync(
            (ClaudeIdentityPicker.SelectedItem as SubscriptionIdentity)?.Id);
        await RefreshClaudeConnectionAsync();
    }

    private async Task RefreshClaudeSubscriptionIdentitiesAsync(string? selectIdentityId = null)
    {
        if (_claudeSubscriptionIdentityActions is null)
        {
            ClaudeIdentityPicker.ItemsSource = Array.Empty<SubscriptionIdentity>();
            ClaudeUsageCards.ItemsSource = Array.Empty<SubscriptionIdentity>();
            ClaudeUseIdentityButton.IsEnabled = false;
            ClaudeRemoveIdentityButton.IsEnabled = false;
            return;
        }
        var snapshot = await _claudeSubscriptionIdentityActions.Read(_lifetime.Token);
        _claudeIdentities = snapshot.Identities;
        ClaudeUsageCards.ItemsSource = _claudeIdentities;
        ViewModel.ActiveClaudeIdentityId = snapshot.ActiveIdentityId;
        ClaudeIdentityPicker.ItemsSource = _claudeIdentities;
        ClaudeIdentityPicker.SelectedItem = _claudeIdentities.FirstOrDefault(identity =>
            identity.Id == (selectIdentityId ?? snapshot.ActiveIdentityId)) ?? _claudeIdentities.FirstOrDefault();
        ApplySelectedClaudeIdentity();
    }

    private void ClaudeIdentity_OnChanged(object? sender, SelectionChangedEventArgs e) =>
        ApplySelectedClaudeIdentity();

    private void ApplySelectedClaudeIdentity()
    {
        if (ClaudeIdentityPicker.SelectedItem is not SubscriptionIdentity identity) return;
        var isActive = identity.Id == ViewModel.ActiveClaudeIdentityId;
        ClaudeIdentityName.Text = identity.DisplayName;
        ClaudeIdentityActive.IsVisible = isActive;
        ClaudeIdentityUsage.Text = identity.UsageLabel;
        ClaudeUseIdentityButton.IsEnabled = !isActive;
        ClaudeRemoveIdentityButton.IsEnabled = !isActive && _claudeIdentities.Count > 1;
        _applyingIdentitySelection = true;
        ClaudeAutomaticHandoffToggle.IsChecked = identity.AutomaticHandoffEnabled;
        _applyingIdentitySelection = false;
        if (!isActive)
        {
            ClaudeAccountStatus.Text = identity.AccountLabel;
            ClaudeRuntimeStatus.Text = "SAVED PROFILE · SELECT USE ACCOUNT TO ACTIVATE";
            ClaudeModelCount.Text = "MODELS · PROFILE INACTIVE";
            ClaudeModelList.Text = identity.LastModelIds is { Count: > 0 }
                ? string.Join("  ·  ", identity.LastModelIds)
                : "Model availability has not been reported for this account.";
            ClaudeSignInButton.IsVisible = false;
            ClaudeSignOutButton.IsVisible = false;
        }
    }

    private async void ClaudeUseIdentity_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_claudeSubscriptionIdentityActions is null
            || ClaudeIdentityPicker.SelectedItem is not SubscriptionIdentity identity
            || identity.Id == ViewModel.ActiveClaudeIdentityId) return;
        await RunAsync($"Switching to {identity.DisplayName}…", async () =>
        {
            await _claudeSubscriptionIdentityActions.Activate(identity.Id, _lifetime.Token);
            ViewModel.ActiveClaudeIdentityId = identity.Id;
            await RefreshClaudeSubscriptionIdentitiesAsync(identity.Id);
            await RefreshClaudeConnectionAsync();
            ViewModel.Status = $"{identity.DisplayName} is now the active Claude account";
            RecordActivity("ACCOUNT", $"Activated · {identity.DisplayName}", outcome: "COMPLETED");
        });
    }

    private async void ClaudeAddIdentity_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_claudeSubscriptionIdentityActions is null) return;
        await RunAsync("Adding an isolated Claude account…", async () =>
        {
            var identity = await _claudeSubscriptionIdentityActions.Add(
                ClaudeIdentityNameInput.Text, _lifetime.Token);
            ClaudeIdentityNameInput.Text = "";
            await _claudeSubscriptionIdentityActions.Activate(identity.Id, _lifetime.Token);
            ViewModel.ActiveClaudeIdentityId = identity.Id;
            await RefreshClaudeSubscriptionIdentitiesAsync(identity.Id);
            await RefreshClaudeConnectionAsync();
            ViewModel.Status = $"{identity.DisplayName} added. Sign in to connect this isolated account.";
            RecordActivity("ACCOUNT", $"Added · {identity.DisplayName}",
                "Isolated Claude subscription profile created", "READY", true);
        });
    }

    private async void ClaudeRemoveIdentity_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_claudeSubscriptionIdentityActions is null
            || ClaudeIdentityPicker.SelectedItem is not SubscriptionIdentity identity
            || identity.Id == ViewModel.ActiveClaudeIdentityId) return;
        await RunAsync($"Removing {identity.DisplayName} from Harness…", async () =>
        {
            await _claudeSubscriptionIdentityActions.Remove(identity.Id, _lifetime.Token);
            await RefreshClaudeSubscriptionIdentitiesAsync();
            ViewModel.Status = $"{identity.DisplayName} removed from Harness. Provider-owned profile files were preserved.";
            RecordActivity("ACCOUNT", $"Removed · {identity.DisplayName}",
                "Provider-owned profile files were preserved", "COMPLETED", true);
        });
    }

    private async void ClaudeAutomaticHandoff_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_applyingIdentitySelection || _claudeSubscriptionIdentityActions?.SetAutomaticHandoff is null
            || ClaudeIdentityPicker.SelectedItem is not SubscriptionIdentity identity) return;
        await RunAsync("Updating Claude handoff participation…", async () =>
        {
            await _claudeSubscriptionIdentityActions.SetAutomaticHandoff(
                identity.Id, ClaudeAutomaticHandoffToggle.IsChecked == true, _lifetime.Token);
            await RefreshClaudeSubscriptionIdentitiesAsync(identity.Id);
        });
    }

    private async void ClaudeInstallHelp_OnClick(object? sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://code.claude.com/docs/en/setup"));
        ViewModel.Status = "Opened the official Claude Code installation guide";
    }

    private async void ClaudeSignIn_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_claudeBusy || _signInClaude is null) return;
        await RunAsync("Opening Claude sign-in…", async () =>
        {
            await _signInClaude(_lifetime.Token);
            await RefreshClaudeConnectionAsync();
            await RefreshClaudeSubscriptionIdentitiesAsync(ViewModel.ActiveClaudeIdentityId);
            RecordActivity("ACCOUNT", "Claude Code sign-in completed", outcome: "COMPLETED", isMilestone: true);
        });
    }

    private async void ClaudeSignOut_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_claudeBusy || _signOutClaude is null) return;
        await RunAsync("Signing out of Claude…", async () =>
        {
            await _signOutClaude(_lifetime.Token);
            await RefreshClaudeConnectionAsync();
            await RefreshClaudeSubscriptionIdentitiesAsync(ViewModel.ActiveClaudeIdentityId);
            ViewModel.Status = "Signed out of Claude. Local workspaces and chats were preserved.";
            RecordActivity("ACCOUNT", "Signed out of Claude", "Local workspaces and chats were preserved", "COMPLETED", true);
        });
    }

    private void ModelFavorite_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModelPreferenceItem item }) item.IsFavorite = !item.IsFavorite;
    }

    private void ModelMoveUp_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModelPreferenceItem item }) ViewModel.MoveModel(item, -1);
    }

    private void ModelMoveDown_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModelPreferenceItem item }) ViewModel.MoveModel(item, 1);
    }

    private async Task LoadApiConnectionsAsync()
    {
        try
        {
            _savedApiConnections = await Task.Run(() => _apiStore.LoadAsync(_lifetime.Token), _lifetime.Token);
            ApiSavedConnections.ItemsSource = _savedApiConnections.Select(saved => saved.Connection).ToArray();
            ApiSavedConnections.SelectedItem = _savedApiConnections.FirstOrDefault(saved => saved.Connection.Id == _editingApiConnection)?.Connection;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { ApiConnectionStatus.Text = exception.Message; }
    }

    private void ApiProvider_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ApiProviderPicker.SelectedItem is not ApiProviderDefinition definition || ApiEndpoint is null) return;
        ApiEndpoint.Text = definition.Endpoint;
        ApiEndpoint.IsReadOnly = !definition.KeyOptional;
        ApiConnectionName.Text = definition.Name;
    }

    private void ApiSavedConnection_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ApiSavedConnections.SelectedItem is not ApiConnection connection) return;
        _editingApiConnection = connection.Id;
        ApiProviderPicker.SelectedItem = connection.Definition;
        ApiEndpoint.Text = connection.Endpoint;
        ApiConnectionName.Text = connection.Name;
        ApiKey.Text = "";
        ApiModelPicker.ItemsSource = null;
        ApiConnectionStatus.Text = "Saved connection. Refresh to inspect its live model catalog; no sign-in is needed.";
    }

    private void ApiNew_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_apiBusy) return;
        _editingApiConnection = null;
        ApiSavedConnections.SelectedItem = null;
        ApiKey.Text = ""; ApiModelPicker.ItemsSource = null;
        if (ApiProviderPicker.SelectedItem is ApiProviderDefinition provider)
        { ApiConnectionName.Text = provider.Name; ApiEndpoint.Text = provider.Endpoint; }
        ApiConnectionStatus.Text = ApiProviderPicker.SelectedItem is ApiProviderDefinition { KeyOptional: true }
            ? "New local connection. Start the runtime, then connect; no API key is required."
            : "New connection. Enter your API key, then connect.";
    }

    private async void ApiConnect_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunApiActionAsync(async () =>
        {
            var provider = (ApiProviderDefinition)ApiProviderPicker.SelectedItem!;
            var connection = new ApiConnection(_editingApiConnection ?? "api-" + Guid.NewGuid().ToString("N"), provider.Id,
                string.IsNullOrWhiteSpace(ApiConnectionName.Text) ? provider.Name : ApiConnectionName.Text.Trim(), ApiEndpoint.Text?.Trim() ?? "");
            var saved = _savedApiConnections.FirstOrDefault(item => item.Connection.Id == connection.Id);
            var enteredKey = ApiKey.Text?.Trim() ?? "";
            // Never reuse credentials when changing the destination of an existing connection.
            if (saved is not null && (saved.Connection.Endpoint != connection.Endpoint || saved.Connection.ProviderId != connection.ProviderId)
                && string.IsNullOrWhiteSpace(enteredKey)) throw new InvalidOperationException("Use a new connection, or supply a new key when changing its destination.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var configurations = saved?.Models ?? [];
            var models = await Task.Run(async () =>
            {
                var key = !string.IsNullOrWhiteSpace(enteredKey) ? enteredKey
                    : saved is null ? "" : ApiConnectionStore.ReadCredential(connection.Id);
                if (string.IsNullOrWhiteSpace(key) && !provider.KeyOptional)
                    throw new InvalidOperationException("Enter this provider's API key. Chat subscription credentials are not API keys.");
                using var transport = new ApiTransport(connection, key);
                var discovered = await ApiModelCatalog.LoadAsync(connection, transport, configurations, timeout.Token).ConfigureAwait(false);
                await _apiStore.SaveAsync(new(connection, configurations), enteredKey.Length > 0 ? enteredKey : null, timeout.Token).ConfigureAwait(false);
                return discovered;
            }, timeout.Token);
            _editingApiConnection = connection.Id;
            ApiKey.Text = "";
            await LoadApiConnectionsAsync();
            _apiModels = models;
            ApiModelPicker.ItemsSource = _apiModels;
            ApiModelPicker.SelectedIndex = _apiModels.Count > 0 ? 0 : -1;
            ApiConnectionStatus.Text = $"Connected · {models.Count:N0} conversational candidates reported. Availability is checked again when you send a request. API billing is separate from subscriptions.";
            if (_apiConnectionsChanged is not null) await _apiConnectionsChanged();
            RefreshProviderDerivedOptions();
            RecordActivity(
                "PROVIDER",
                $"Connected · {connection.Name}",
                $"{models.Count:N0} conversational candidates reported",
                "COMPLETED",
                true);
        });
    }

    private async void ApiDetectLocal_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunApiActionAsync(async () =>
        {
            var savedSnapshot = _savedApiConnections;
            var results = await Task.Run(async () =>
            {
                var providers = ApiProviderDefinition.All
                    .Where(provider => provider.Id is "ollama-local" or "llama-cpp-local").ToArray();
                var probes = providers.Select(async provider =>
                {
                    var saved = savedSnapshot.FirstOrDefault(item => item.Connection.ProviderId == provider.Id
                        && string.Equals(item.Connection.Endpoint.TrimEnd('/'), provider.Endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                    var connection = saved?.Connection ?? new ApiConnection("api-" + Guid.NewGuid().ToString("N"),
                        provider.Id, provider.Name, provider.Endpoint);
                    using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    probeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        using var transport = new ApiTransport(connection, "");
                        var models = await ApiModelCatalog.LoadAsync(connection, transport, saved?.Models ?? [], probeTimeout.Token).ConfigureAwait(false);
                        await _apiStore.SaveAsync(new(connection, saved?.Models ?? []), null, probeTimeout.Token).ConfigureAwait(false);
                        return new LocalDetectionResult(connection, models, null);
                    }
                    catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                    { return new LocalDetectionResult(connection, [], "The local metadata request timed out."); }
                    catch (Exception exception) { return new LocalDetectionResult(connection, [], exception.Message); }
                });
                return await Task.WhenAll(probes).ConfigureAwait(false);
            }, _lifetime.Token);

            var detected = results.Where(result => result.Error is null).ToArray();
            if (detected.Length == 0)
                throw new InvalidOperationException("No Ollama or llama.cpp server responded on its default local port. Start a runtime, or add its endpoint as a new local connection.");

            var selected = detected.OrderByDescending(result => result.Models.Count).First();
            _editingApiConnection = selected.Connection.Id;
            await LoadApiConnectionsAsync();
            _apiModels = selected.Models;
            ApiModelPicker.ItemsSource = _apiModels;
            ApiModelPicker.SelectedIndex = _apiModels.Count > 0 ? 0 : -1;
            var names = string.Join(", ", detected.Select(result => result.Connection.Name));
            var failures = results.Count(result => result.Error is not null);
            ApiConnectionStatus.Text = $"Detected {names} · {detected.Sum(result => result.Models.Count):N0} conversational models."
                + (failures == 0 ? "" : $" {failures} other local runtime did not respond.");
            if (_apiConnectionsChanged is not null) await _apiConnectionsChanged();
            RefreshProviderDerivedOptions();
            RecordActivity("PROVIDER", "Local runtimes detected", ApiConnectionStatus.Text, "COMPLETED", true);
        });
    }

    private async void ApiDisconnect_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunApiActionAsync(async () =>
        {
            if (_editingApiConnection is null) throw new InvalidOperationException("Choose a saved connection first.");
            await Task.Run(() => _apiStore.RemoveAsync(_editingApiConnection, _lifetime.Token), _lifetime.Token);
            _editingApiConnection = null;
            ApiKey.Text = ""; ApiModelPicker.ItemsSource = null;
            await LoadApiConnectionsAsync();
            if (_apiConnectionsChanged is not null) await _apiConnectionsChanged();
            RefreshProviderDerivedOptions();
            ApiConnectionStatus.Text = "Disconnected. Saved chat history is preserved.";
            RecordActivity("PROVIDER", "API connection removed", "Saved chat history was preserved", "COMPLETED", true);
        });
    }

    private void ApiModel_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ApiModelPicker.SelectedItem is not ApiModel model) return;
        var descriptor = model.Descriptor;
        var overridden = _savedApiConnections.FirstOrDefault(saved => saved.Connection.Id == _editingApiConnection)?.Models.Any(config => config.ModelId == descriptor.ModelId) == true;
        var connection = _savedApiConnections.FirstOrDefault(saved => saved.Connection.Id == _editingApiConnection)?.Connection;
        if (connection is null)
        {
            ApiModelMetadataStatus.Text = "The selected provider connection is no longer available.";
            return;
        }
        var report = ApiCapabilityConformance.Evaluate(connection, model);
        var ready = string.Join(", ", report.Ready.Select(ApiCapabilityConformance.Name));
        var gaps = string.Join(", ", report.AdapterGaps.Select(ApiCapabilityConformance.Name));
        var source = overridden ? "Using your explicit model override."
            : model.CapabilityMetadataReported ? "Using provider metadata. Unreported options remain unknown; only implemented modalities are enabled."
            : "The catalog does not publish capability metadata. Text requests can be attempted; enable tools/images only after verifying support for this model.";
        ApiModelMetadataStatus.Text = $"{source} Harness ready: {(ready.Length == 0 ? "none reported" : ready)}."
            + (gaps.Length == 0 ? "" : $" Reported but not implemented yet: {gaps}.");
        ApiModelTools.IsChecked = descriptor.Supports(ModelCapability.ToolUse);
        ApiModelImages.IsChecked = descriptor.Supports(ModelCapability.Vision);
        ApiModelAudio.IsChecked = descriptor.Supports(ModelCapability.AudioInput);
        ApiModelVideo.IsChecked = descriptor.Supports(ModelCapability.VideoInput);
        ApiModelPdf.IsChecked = descriptor.Supports(ModelCapability.PdfInput);
        ApiModelCaching.IsChecked = model.PromptCachingEnabled;
        ApiModelHostedArtifacts.IsChecked = model.HostedArtifactsEnabled;
        ApiModelContextManagement.IsChecked = model.ContextManagementEnabled;
        ApiModelContext.Text = descriptor.ContextWindow?.ToString() ?? "";
        ApiModelReasoning.Text = string.Join(", ", descriptor.ReasoningLevels?.Select(level => level.Id) ?? []);
        ApiModelTiers.Text = string.Join(", ", descriptor.ServiceTiers?.Select(tier => tier.Id).OfType<string>() ?? []);
    }

    private async void ApiSaveModel_OnClick(object? sender, RoutedEventArgs e) => await SaveApiModelAsync(reset: false);
    private async void ApiResetModel_OnClick(object? sender, RoutedEventArgs e) => await SaveApiModelAsync(reset: true);

    private async Task SaveApiModelAsync(bool reset)
    {
        await RunApiActionAsync(async () =>
        {
            var saved = _savedApiConnections.FirstOrDefault(item => item.Connection.Id == _editingApiConnection);
            if (saved is null || ApiModelPicker.SelectedItem is not ApiModel model) throw new InvalidOperationException("Connect and select a model first.");
            int? limit = null;
            if (!string.IsNullOrWhiteSpace(ApiModelContext.Text))
                limit = int.TryParse(ApiModelContext.Text, out var parsed) && parsed > 0 ? parsed : throw new InvalidOperationException("Context limit must be a positive whole number.");
            var configurations = saved.Models.Where(config => config.ModelId != model.Descriptor.ModelId).ToList();
            if (!reset && ApiModelCaching.IsChecked == true
                && (ApiCapabilityConformance.ImplementedFor(saved.Connection.Definition.Protocol) & ModelCapability.PromptCaching) == 0)
                throw new InvalidOperationException("Automatic prompt caching is not implemented for this provider adapter yet.");
            if (!reset && ApiModelHostedArtifacts.IsChecked == true
                && (ApiCapabilityConformance.ImplementedFor(saved.Connection) & ModelCapability.GeneratedArtifacts) == 0)
                throw new InvalidOperationException("Provider-hosted artifact generation is not implemented for this connection yet.");
            if (!reset && ApiModelContextManagement.IsChecked == true
                && (ApiCapabilityConformance.ImplementedFor(saved.Connection) & ModelCapability.ContextManagement) == 0)
                throw new InvalidOperationException("Provider-native context compaction is not implemented for this connection yet.");
            if (!reset) configurations.Add(new(model.Descriptor.ModelId, ApiModelTools.IsChecked == true, ApiModelImages.IsChecked == true,
                limit, Values(ApiModelReasoning.Text), Values(ApiModelTiers.Text), ApiModelAudio.IsChecked == true,
                ApiModelVideo.IsChecked == true, ApiModelPdf.IsChecked == true, ApiModelCaching.IsChecked == true,
                ApiModelHostedArtifacts.IsChecked == true, ApiModelContextManagement.IsChecked == true));
            await Task.Run(() => _apiStore.SaveAsync(saved with { Models = configurations }, null, _lifetime.Token), _lifetime.Token);
            await LoadApiConnectionsAsync();
            if (_apiConnectionsChanged is not null) await _apiConnectionsChanged();
            RefreshProviderDerivedOptions();
            ApiConnectionStatus.Text = reset ? "Model override removed. Refresh to inspect provider metadata." : "Model override saved and applied. The provider will validate these options on requests.";
        });
    }

    private static string[] Values(string? value) => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();

    private async Task RunApiActionAsync(Func<Task> action)
    {
        if (_apiBusy) return;
        _apiBusy = true;
        ApiConnectionPanel.IsEnabled = false;
        ApiModelPanel.IsEnabled = false;
        ApiConnectionStatus.Text = "Working…";
        try { await action(); }
        catch (OperationCanceledException)
        {
            ApiConnectionStatus.Text = "Connection request cancelled or timed out. No generation was retried.";
            RecordActivity("PROVIDER", "Provider connection stopped", ApiConnectionStatus.Text, "CANCELLED", color: "#E2A84A");
        }
        catch (Exception exception)
        {
            ApiConnectionStatus.Text = exception.Message;
            RecordActivity("ERROR", "Provider connection failed", exception.Message, "FAILED", color: "#E2A84A");
        }
        finally { _apiBusy = false; ApiConnectionPanel.IsEnabled = true; ApiModelPanel.IsEnabled = true; }
    }
}

file sealed record LocalDetectionResult(ApiConnection Connection, IReadOnlyList<ApiModel> Models, string? Error);

public sealed record SubscriptionConnectionSnapshot(
    bool RuntimeAvailable,
    bool IsAuthenticated,
    string AccountLabel,
    string Detail,
    IReadOnlyList<string> Models)
{
    public static SubscriptionConnectionSnapshot Unavailable(string detail) =>
        new(false, false, "Runtime unavailable", detail, []);
}
