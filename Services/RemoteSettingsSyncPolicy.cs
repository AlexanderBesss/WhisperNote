using WhisperNote.Config;

namespace WhisperNote.Services;

public static class RemoteSettingsSyncPolicy
{
    public static bool ShouldSyncOnSave(
        bool useRemote,
        RemoteProviderMode providerMode,
        bool behaviorChanged) =>
        useRemote &&
        providerMode == RemoteProviderMode.RemoteExecution &&
        behaviorChanged;
}
