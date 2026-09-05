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
        CodexUseIdentityButton.IsEnabled = !isActive;
        CodexRemoveIdentityButton.IsEnabled = !isActive && _codexIdentities.Count > 1;
        if (!isActive)
        {
            CodexAccountStatus.Text = identity.AccountLabel;
            CodexRuntimeStatus.Text = "SAVED PROFILE · SELECT USE ACCOUNT TO ACTIVATE";
            CodexModelCount.Text = "MODELS · PROFILE INACTIVE";
            CodexModelList.Text = "Model availability and live usage are refreshed when this account becomes active.";
            CodexSignInButton.IsVisible = false;
            CodexSignOutButton.IsVisible = false;
        }
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
            ViewModel.Status = "Signed out of OpenAI. Local workspaces and chats were preserved.";
            RecordActivity("ACCOUNT", "Signed out of OpenAI", "Local workspaces and chats were preserved", "COMPLETED", true);
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
            _savedApiConnections = await _apiStore.LoadAsync(_lifetime.Token);
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
        ApiConnectionStatus.Text = "New connection. Enter your API key, then connect.";
    }

    private async void ApiConnect_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunApiActionAsync(async () =>
        {
            var provider = (ApiProviderDefinition)ApiProviderPicker.SelectedItem!;
            var connection = new ApiConnection(_editingApiConnection ?? "api-" + Guid.NewGuid().ToString("N"), provider.Id,
                string.IsNullOrWhiteSpace(ApiConnectionName.Text) ? provider.Name : ApiConnectionName.Text.Trim(), ApiEndpoint.Text?.Trim() ?? "");
            var key = !string.IsNullOrWhiteSpace(ApiKey.Text) ? ApiKey.Text.Trim() : _editingApiConnection is null ? "" : ApiConnectionStore.ReadCredential(connection.Id);
            if (string.IsNullOrWhiteSpace(key) && !provider.KeyOptional) throw new InvalidOperationException("Enter this provider's API key. Chat subscription credentials are not API keys.");
            var saved = _savedApiConnections.FirstOrDefault(item => item.Connection.Id == connection.Id);
            // Never reuse credentials when changing the destination of an existing connection.
            if (saved is not null && (saved.Connection.Endpoint != connection.Endpoint || saved.Connection.ProviderId != connection.ProviderId)
                && string.IsNullOrWhiteSpace(ApiKey.Text)) throw new InvalidOperationException("Use a new connection, or supply a new key when changing its destination.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            using var transport = new ApiTransport(connection, key);
            var configurations = saved?.Models ?? [];
            var models = await ApiModelCatalog.LoadAsync(connection, transport, configurations, timeout.Token);
            await _apiStore.SaveAsync(new(connection, configurations), key, _lifetime.Token);
            _editingApiConnection = connection.Id;
            ApiKey.Text = "";
            await LoadApiConnectionsAsync();
            _apiModels = models;
            ApiModelPicker.ItemsSource = _apiModels;
            ApiModelPicker.SelectedIndex = _apiModels.Count > 0 ? 0 : -1;
            ApiConnectionStatus.Text = $"Connected · {models.Count:N0} conversational candidates reported. Availability is checked again when you send a request. API billing is separate from subscriptions.";
            if (_apiConnectionsChanged is not null) await _apiConnectionsChanged();
            RecordActivity(
                "PROVIDER",
                $"Connected · {connection.Name}",
                $"{models.Count:N0} conversational candidates reported",
                "COMPLETED",
                true);
        });
    }

    private async void ApiDisconnect_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunApiActionAsync(async () =>
        {
            if (_editingApiConnection is null) throw new InvalidOperationException("Choose a saved connection first.");
            await _apiStore.RemoveAsync(_editingApiConnection, _lifetime.Token);
            _editingApiConnection = null;
            ApiKey.Text = ""; ApiModelPicker.ItemsSource = null;
            await LoadApiConnectionsAsync();
            if (_apiConnectionsChanged is not null) await _apiConnectionsChanged();
            ApiConnectionStatus.Text = "Disconnected. Saved chat history is preserved.";
            RecordActivity("PROVIDER", "API connection removed", "Saved chat history was preserved", "COMPLETED", true);
        });
    }

    private void ApiModel_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ApiModelPicker.SelectedItem is not ApiModel model) return;
        var descriptor = model.Descriptor;
        var overridden = _savedApiConnections.FirstOrDefault(saved => saved.Connection.Id == _editingApiConnection)?.Models.Any(config => config.ModelId == descriptor.ModelId) == true;
        ApiModelMetadataStatus.Text = overridden ? "Using your explicit model override."
            : model.CapabilityMetadataReported ? "Using provider metadata. Unreported options remain unknown; only implemented modalities are enabled."
            : "The catalog does not publish capability metadata. Text requests can be attempted; enable tools/images only after verifying support for this model.";
        ApiModelTools.IsChecked = descriptor.Supports(ModelCapability.ToolUse);
        ApiModelImages.IsChecked = descriptor.Supports(ModelCapability.Vision);
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
            if (!reset) configurations.Add(new(model.Descriptor.ModelId, ApiModelTools.IsChecked == true, ApiModelImages.IsChecked == true,
                limit, Values(ApiModelReasoning.Text), Values(ApiModelTiers.Text)));
            await _apiStore.SaveAsync(saved with { Models = configurations }, null, _lifetime.Token);
            await LoadApiConnectionsAsync();
            if (_apiConnectionsChanged is not null) await _apiConnectionsChanged();
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
