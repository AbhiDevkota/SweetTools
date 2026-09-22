using System.Windows;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// Status of the account lock check between active Steam user and the allowed account.
/// </summary>
public enum AccountGuardStatus
{
    Allowed,
    NoAccountConfigured,
    NoActiveSteamAccount,
    AccountMismatch
}

/// <summary>
/// Central guard service ensuring that fetching games, installing manifests, modifying Steam configurations,
/// and executing other tool features can only occur when the currently logged-in Steam account matches
/// the configured AllowedSteamId, keeping other Steam accounts safe.
/// </summary>
public class AccountGuardService
{
    private readonly SettingsService _settings;
    private readonly CurrentSteamUserService? _currentUser;
    private readonly ToastService? _toast;
    private readonly ILogger<AccountGuardService>? _log;
    private readonly Func<string?>? _currentSteamIdProvider;

    private int _dialogGate;

    /// <summary>
    /// Global delegate to navigate to the Settings page when the user clicks 'Open Settings' on a notification.
    /// </summary>
    public Action? RequestOpenSettings { get; set; }

    public AccountGuardService(
        SettingsService settings,
        CurrentSteamUserService currentUser,
        ToastService toast,
        ILogger<AccountGuardService>? log = null)
        : this(settings, currentUser, toast, null, log)
    {
    }

    internal AccountGuardService(
        SettingsService settings,
        CurrentSteamUserService? currentUser,
        ToastService? toast,
        Func<string?>? currentSteamIdProvider,
        ILogger<AccountGuardService>? log = null)
    {
        _settings = settings;
        _currentUser = currentUser;
        _toast = toast;
        _currentSteamIdProvider = currentSteamIdProvider;
        _log = log;
    }

    /// <summary>
    /// Resolves the currently active SteamID64.
    /// </summary>
    public string? GetCurrentSteamId() =>
        _currentSteamIdProvider?.Invoke() ?? _currentUser?.GetCurrentSteamId();

    /// <summary>
    /// Resolves the locked AllowedSteamId from Settings.
    /// </summary>
    public string? GetAllowedSteamId()
    {
        string? allowed = _settings.AllowedSteamId;
        return string.IsNullOrWhiteSpace(allowed) ? null : allowed.Trim();
    }

    /// <summary>
    /// Evaluates current active Steam account against the allowed account setting.
    /// </summary>
    public (AccountGuardStatus Status, string? CurrentSteamId, string? AllowedSteamId) CheckStatus()
    {
        string? allowed = GetAllowedSteamId();
        if (string.IsNullOrWhiteSpace(allowed))
            return (AccountGuardStatus.NoAccountConfigured, GetCurrentSteamId(), null);

        string? current = GetCurrentSteamId()?.Trim();
        if (string.IsNullOrWhiteSpace(current))
            return (AccountGuardStatus.NoActiveSteamAccount, null, allowed);

        if (string.Equals(allowed, current, StringComparison.OrdinalIgnoreCase))
            return (AccountGuardStatus.Allowed, current, allowed);

        return (AccountGuardStatus.AccountMismatch, current, allowed);
    }

    /// <summary>
    /// Returns true if and only if an allowed account is locked and matches the active Steam account.
    /// </summary>
    public bool IsAllowed() => CheckStatus().Status == AccountGuardStatus.Allowed;

    /// <summary>
    /// Checks if the active Steam account is allowed. If not, displays a modal popup message box
    /// explaining that the feature is disabled for this account and shows a toast notification.
    /// </summary>
    /// <param name="featureName">Optional display name of the feature being guarded (e.g. "Fetching games").</param>
    /// <param name="onOpenSettings">Optional callback to navigate to settings; falls back to RequestOpenSettings.</param>
    /// <param name="suppressPopup">If true, suppresses the modal MessageBox (e.g. for background/silent operations).</param>
    /// <returns>True if allowed to proceed; false if blocked.</returns>
    public bool EnsureAllowed(string? featureName = null, Action? onOpenSettings = null, bool suppressPopup = false)
    {
        var (status, currentId, allowedId) = CheckStatus();
        if (status == AccountGuardStatus.Allowed)
            return true;

        string featureText = !string.IsNullOrWhiteSpace(featureName) ? featureName : "This feature";
        string title;
        string message;

        switch (status)
        {
            case AccountGuardStatus.NoAccountConfigured:
                title = "Account Configuration Required";
                message = $"{featureText} requires a locked Steam account.\n\n" +
                          "No Steam account is currently locked in Sweet Tools.\n\n" +
                          "Please go to Settings and lock your Steam account to enable this feature and keep your Steam accounts safe.";
                break;

            case AccountGuardStatus.NoActiveSteamAccount:
                title = "No Active Steam Account";
                message = $"{featureText} is disabled because no active Steam account was detected.\n\n" +
                          $"Allowed Account: {allowedId}\n\n" +
                          "Please launch and log into Steam with your allowed account before using this feature.";
                break;

            case AccountGuardStatus.AccountMismatch:
            default:
                title = "Feature Disabled for This Account";
                message = $"{featureText} is disabled for this Steam account.\n\n" +
                          $"Active Steam Account: {currentId}\n" +
                          $"Allowed Steam Account: {allowedId}\n\n" +
                          "Sweet Tools only works with your locked allowed account to keep other Steam accounts safe.\n\n" +
                          "Please switch to your allowed account in Steam or change your allowed account in Settings.";
                break;
        }

        _log?.LogWarning(
            "AccountGuard blocked '{Feature}': status={Status}, current={Current}, allowed={Allowed}",
            featureText, status, currentId, allowedId);

        // Show persistent toast with action to open settings
        Action openSettingsAction = onOpenSettings ?? RequestOpenSettings ?? (() => { });
        _toast?.ShowAction(
            title,
            message.Replace("\n\n", " "),
            "Open Settings",
            openSettingsAction,
            error: true);

        // Show modal popup dialog
        if (!suppressPopup)
        {
            ShowPopup(message, title);
        }

        return false;
    }

    private void ShowPopup(string message, string title)
    {
        if (Interlocked.CompareExchange(ref _dialogGate, 1, 0) != 0)
            return;

        try
        {
            var app = Application.Current;
            if (app is null) return;

            void Display()
            {
                try
                {
                    Window? owner = app.MainWindow;
                    if (owner is not null && owner.IsVisible)
                    {
                        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch
                {
                    MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            if (app.Dispatcher.CheckAccess())
            {
                Display();
            }
            else
            {
                app.Dispatcher.Invoke(Display);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _dialogGate, 0);
        }
    }
}
