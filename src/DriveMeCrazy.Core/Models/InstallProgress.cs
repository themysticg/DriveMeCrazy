namespace DriveMeCrazy.Core.Models;

public enum InstallState { Queued, Downloading, Installing, Installed, Failed, Skipped, Cancelled }

/// <summary>Progress or final result for one update.</summary>
public sealed record InstallProgress(
    string Id,
    InstallState State,
    string Message,
    double? Percent = null,
    bool RebootRequired = false)
{
    public bool IsFinal => State is InstallState.Installed or InstallState.Failed
        or InstallState.Skipped or InstallState.Cancelled;
}

public sealed record HistoryEntry(
    DateTime Timestamp,
    string Kind,
    string Title,
    string Result,
    bool Succeeded,
    bool RebootRequired = false);

public sealed record RestorePointResult(bool Success, string Message, bool ProtectionDisabled = false);
