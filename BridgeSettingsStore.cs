using System.Text.Json;
using Windows.Security.Credentials;

namespace HueDesktop;

internal sealed class BridgeSettingsStore
{
    private const string CredentialResource = "HueDesktop.HueBridge";
    private const int CredentialNotFoundHResult = unchecked((int)0x80070490);
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HueDesktop",
        "connection.json");

    public SavedBridgeConnection? Load()
    {
        var storedSettings = ReadSettings();
        var bridgeIp = storedSettings.BridgeIp;
        if (string.IsNullOrWhiteSpace(bridgeIp))
        {
            return null;
        }

        string? applicationKey = null;
        try
        {
            var credential = new PasswordVault().Retrieve(CredentialResource, bridgeIp);
            credential.RetrievePassword();
            applicationKey = credential.Password;
        }
        catch (Exception exception) when (exception.HResult == CredentialNotFoundHResult)
        {
        }

        return new SavedBridgeConnection(bridgeIp, applicationKey);
    }

    public string? LoadTrustedFingerprint(string bridgeIp)
    {
        var fingerprints = ReadSettings().TrustedCertificates;
        return fingerprints.FirstOrDefault(
            item => string.Equals(item.Key, bridgeIp, StringComparison.OrdinalIgnoreCase)).Value;
    }

    public (int Width, int Height)? LoadWindowSize()
    {
        var settings = ReadSettings();
        if (settings.WindowWidth is > 0 and <= 16384 &&
            settings.WindowHeight is > 0 and <= 16384)
        {
            return (settings.WindowWidth.Value, settings.WindowHeight.Value);
        }

        return null;
    }

    public void SaveWindowSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var settings = ReadSettings();
        settings.WindowWidth = width;
        settings.WindowHeight = height;
        WriteSettings(settings);
    }

    public void Save(string bridgeIp, string applicationKey)
    {
        var vault = new PasswordVault();
        RemoveExistingCredentials(vault);
        vault.Add(new PasswordCredential(CredentialResource, bridgeIp, applicationKey));

        var settings = ReadSettings();
        settings.BridgeIp = bridgeIp;
        WriteSettings(settings);
    }

    public void SaveTrustedFingerprint(string bridgeIp, string fingerprint)
    {
        var settings = ReadSettings();
        settings.TrustedCertificates[bridgeIp] = fingerprint;
        WriteSettings(settings);
    }

    public void Forget()
    {
        RemoveExistingCredentials(new PasswordVault());
        var settings = ReadSettings();
        settings.BridgeIp = null;
        WriteSettings(settings);
    }

    private StoredSettings ReadSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return new StoredSettings();
        }

        return JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_settingsPath))
            ?? new StoredSettings();
    }

    private void WriteSettings(StoredSettings settings)
    {
        if (settings.BridgeIp is null &&
            settings.TrustedCertificates.Count == 0 &&
            settings.WindowWidth is null &&
            settings.WindowHeight is null)
        {
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings));
    }

    private static void RemoveExistingCredentials(PasswordVault vault)
    {
        IReadOnlyList<PasswordCredential> credentials;
        try
        {
            credentials = vault.FindAllByResource(CredentialResource);
        }
        catch (Exception exception) when (exception.HResult == CredentialNotFoundHResult)
        {
            return;
        }

        foreach (var credential in credentials)
        {
            vault.Remove(credential);
        }
    }

    private sealed class StoredSettings
    {
        public string? BridgeIp { get; set; }
        public Dictionary<string, string> TrustedCertificates { get; set; } = new();
        public int? WindowWidth { get; set; }
        public int? WindowHeight { get; set; }
    }
}

internal sealed record SavedBridgeConnection(string BridgeIp, string? ApplicationKey);
