using System.Text.Json;

namespace SceneGallery.Plugin.BepisDb.Tests;

public sealed class PluginSettingsSecretStorageTests
{
    private const string TestSecret = "test-secret-value";

    [Fact]
    public void LegacyPlaintext_LoadsWithoutRewriteAndNextSaveEncryptsIt()
    {
        using var directory = new SecretStorageTempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var legacyJson = $$"""
            {
              "cfClearanceCookie": "{{TestSecret}}",
              "userAgent": "test-agent",
              "destinationFolderName": "TestBepis"
            }
            """;
        File.WriteAllText(path, legacyJson);

        var settings = PluginSettings.Load(directory.Path, _ => { });

        Assert.Equal(TestSecret, settings.CfClearanceCookie);
        Assert.Equal("test-agent", settings.UserAgent);
        Assert.Equal("TestBepis", settings.DestinationFolderName);
        Assert.Equal(legacyJson, File.ReadAllText(path));

        settings.Save(directory.Path, _ => { });

        var persistedValue = ReadSecret(path);
        Assert.StartsWith(DpapiSecretProtector.Prefix, persistedValue);
        Assert.DoesNotContain(TestSecret, File.ReadAllText(path));
        Assert.Equal(TestSecret, PluginSettings.Load(directory.Path, _ => { }).CfClearanceCookie);
    }

    [Fact]
    public void TamperedCiphertext_LoadsAsUnsetAndLogsSafeWarning()
    {
        using var directory = new SecretStorageTempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var encryptedValue = DpapiSecretProtector.Protect(TestSecret)!;
        var bytes = Convert.FromBase64String(encryptedValue[DpapiSecretProtector.Prefix.Length..]);
        bytes[^1] ^= 0xff;
        var tamperedValue = DpapiSecretProtector.Prefix + Convert.ToBase64String(bytes);
        File.WriteAllText(path, $$"""{"cfClearanceCookie":"{{tamperedValue}}"}""");
        var logs = new List<string>();

        var settings = PluginSettings.Load(directory.Path, logs.Add);

        Assert.Null(settings.CfClearanceCookie);
        var warning = Assert.Single(logs);
        Assert.Equal(
            "WARNING: Secret setting 'cfClearanceCookie' could not be decrypted (DPAPI decryption failed). It may belong to a different computer or Windows user; configure it again.",
            warning);
        Assert.DoesNotContain(TestSecret, warning);
        Assert.DoesNotContain(tamperedValue, warning);
    }

    [Fact]
    public void NullAndEmpty_AreNotTaggedAsProtectedValues()
    {
        using var directory = new SecretStorageTempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");

        new PluginSettings { CfClearanceCookie = null }.Save(directory.Path, _ => { });
        using (var nullDocument = JsonDocument.Parse(File.ReadAllText(path)))
            Assert.False(nullDocument.RootElement.TryGetProperty("cfClearanceCookie", out _));

        new PluginSettings { CfClearanceCookie = "" }.Save(directory.Path, _ => { });
        Assert.Equal("", ReadSecret(path));
    }

    private static string ReadSecret(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cfClearanceCookie").GetString()!;
    }

    private sealed class SecretStorageTempDirectory : IDisposable
    {
        public SecretStorageTempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SceneGallery.Plugin.BepisDb.Tests",
                "SecretStorage",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for a test-only temporary directory.
            }
        }
    }
}
