using ServerTray.App;
using ServerTray.Core;
using Xunit;
using System.IO;

namespace ServerTray.App.Tests;

public sealed class StorageTests
{
    [Fact] public void CredentialsAreEncryptedOnDiskAndRoundTripAcrossInstances()
    {
        var dir = Temp(); try { var secret = new Credential("fixture-password-only", "fixture-key-material-only", "fixture-passphrase-only"); new CredentialVault(dir).Put("fixture", secret); var raw = File.ReadAllText(Path.Combine(dir, "credentials.json")); Assert.DoesNotContain("fixture-password-only", raw); Assert.DoesNotContain("fixture-key-material-only", raw); Assert.Equal(secret, new CredentialVault(dir).Get("fixture")); } finally { Directory.Delete(dir, true); }
    }
    [Fact] public void RemovedCredentialsCannotBeRead()
    {
        var dir = Temp(); try { var vault = new CredentialVault(dir); vault.Put("fixture", new("fixture-only")); vault.Remove("fixture"); Assert.Null(new CredentialVault(dir).Get("fixture")); } finally { Directory.Delete(dir, true); }
    }
    [Fact] public void HostKeyTrustIsScopedToHostAndPort()
    {
        var dir = Temp(); try { var pins = new KnownHosts(dir); pins.Trust(new("server.example.invalid", 22, "SHA256:fixture", null)); Assert.Equal("SHA256:fixture", new KnownHosts(dir).Get("SERVER.EXAMPLE.INVALID", 22)); Assert.Null(pins.Get("other.example.invalid", 22)); Assert.Null(pins.Get("server.example.invalid", 2222)); } finally { Directory.Delete(dir, true); }
    }
    [Fact] public void ChineseResourcesAreEmbeddedAndResolvable() { Assert.Equal("内存", Text.Get("Memory")); Assert.Equal("主机密钥已变更", Text.Status(ConnectionStatus.HostKeyChanged)); }
    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "servertray-app-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
}
