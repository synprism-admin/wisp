using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Wisp.Core;

/// <summary>
/// Updates from the project's GitHub Releases (Velopack's release feed is published with each
/// release by the release workflow). Only the installed app can update itself; a dev build or
/// the portable zip just reports its version.
/// </summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/synprism-admin/wisp";

    // 0.x releases are pre-releases on GitHub, so include them.
    readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, true));

    public bool CanUpdate => _manager.IsInstalled;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "dev";

    /// <summary>
    /// Opens a new GitHub issue with a short template and this install's version filled in
    /// (nothing else about the PC is sent).
    /// </summary>
    public void ReportBug()
    {
        var body = "**What happened?**\n\n\n**What did you expect to happen?**\n\n\n**Steps to reproduce**\n1. \n\n"
                 + $"---\nWisp {CurrentVersion} · Windows {Environment.OSVersion.Version}";
        var url = $"{RepoUrl}/issues/new?labels=bug&body={Uri.EscapeDataString(body)}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>The newer release on GitHub, or null if this is the latest (or can't update).</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!CanUpdate) return null;
        return await _manager.CheckForUpdatesAsync();
    }

    public Task DownloadAsync(UpdateInfo update, Action<int> progress, CancellationToken ct = default) =>
        _manager.DownloadUpdatesAsync(update, progress, ct);

    /// <summary>
    /// Arms the installer to swap versions as soon as this process exits, then relaunch. The caller
    /// then shuts Wisp down normally, so the taskbar and tray are restored first.
    /// </summary>
    public void ApplyWhenWispExits(UpdateInfo update) =>
        _manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: true, Array.Empty<string>());
}
