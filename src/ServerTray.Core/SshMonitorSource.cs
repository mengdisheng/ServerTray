using System.Diagnostics;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace ServerTray.Core;

public sealed record Credential(string? Password = null, string? PrivateKey = null, string? Passphrase = null);
public interface ICredentialStore { Credential? Get(string serverId); }
public sealed record HostKeyChallenge(string Host, int Port, string Fingerprint, string? Expected);
public interface IHostKeyStore { string? Get(string host, int port); }
public sealed class HostKeyException(HostKeyChallenge challenge) : Exception("SSH host key requires verification.") { public HostKeyChallenge Challenge { get; } = challenge; }
public sealed class MissingCredentialException() : Exception("SSH credentials are not configured.");
public sealed class CredentialFormatException() : Exception("Invalid private key or passphrase.");
public interface IMonitorSource : IAsyncDisposable
{
    StaticInfo? Info { get; }
    int ConnectionCount => 0;
    Task<RawMetrics> CollectAsync(bool includeDisk, CancellationToken token);
    void Abort();
}
public sealed class SshMonitorSource(ServerProfile profile, AppConfiguration configuration, ICredentialStore credentials, IHostKeyStore hostKeys) : IMonitorSource
{
    private SshClient? client, jumpClient;
    private ForwardedPortLocal? forward;
    private readonly List<PrivateKeyFile> keys = [];
    private readonly int timeout = profile.TimeoutSeconds ?? configuration.Monitoring.TimeoutSeconds;
    public StaticInfo? Info { get; private set; }
    public int ConnectionCount => (client?.IsConnected == true ? 1 : 0) + (jumpClient?.IsConnected == true ? 1 : 0);
    public async Task<RawMetrics> CollectAsync(bool includeDisk, CancellationToken token)
    {
        if (client?.IsConnected != true)
        {
            Abort();
            try
            {
                if (profile.JumpServerId is { } jumpId)
                {
                    var jump = configuration.Servers.Single(s => s.Id == jumpId);
                    jumpClient = await Connect(jump, jump.Host, jump.Port, token).ConfigureAwait(false);
                    forward = new ForwardedPortLocal("127.0.0.1", 0, profile.Host, (uint)profile.Port);
                    jumpClient.AddForwardedPort(forward); forward.Start();
                    client = await Connect(profile, "127.0.0.1", (int)forward.BoundPort, token).ConfigureAwait(false);
                }
                else client = await Connect(profile, profile.Host, profile.Port, token).ConfigureAwait(false);
                Info = LinuxCollector.ParseStatic(await Execute(LinuxCollector.StaticScript, token).ConfigureAwait(false));
            }
            catch { Abort(); throw; }
            includeDisk = true;
        }
        var raw = await Execute(LinuxCollector.Script + "\n" + (includeDisk ? LinuxCollector.DiskScript : ""), token).ConfigureAwait(false);
        return LinuxCollector.Parse(raw, DateTimeOffset.UtcNow);
    }
    private async Task<SshClient> Connect(ServerProfile server, string host, int port, CancellationToken token)
    {
        var secret = credentials.Get(server.Id) ?? throw new MissingCredentialException();
        AuthenticationMethod method;
        if (server.Authentication == AuthenticationKind.Password) method = new PasswordAuthenticationMethod(server.Username, secret.Password ?? "");
        else
        {
            if (string.IsNullOrWhiteSpace(secret.PrivateKey)) throw new MissingCredentialException();
            var bytes = Encoding.UTF8.GetBytes(secret.PrivateKey);
            try { using var input = new MemoryStream(bytes); var key = new PrivateKeyFile(input, secret.Passphrase); keys.Add(key); method = new PrivateKeyAuthenticationMethod(server.Username, new ModernPrivateKey(key)); }
            catch (Exception e) when (e is SshException or System.Security.Cryptography.CryptographicException or ArgumentException or FormatException) { throw new CredentialFormatException(); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        var connection = new ConnectionInfo(host, port, server.Username, method) { Timeout = TimeSpan.FromSeconds(timeout) };
        var ssh = new SshClient(connection) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        HostKeyChallenge? challenge = null;
        ssh.HostKeyReceived += (_, e) =>
        {
            var actual = "SHA256:" + e.FingerPrintSHA256.TrimEnd('='); var expected = hostKeys.Get(server.Host, server.Port);
            e.CanTrust = expected is not null && string.Equals(expected, actual, StringComparison.Ordinal);
            if (!e.CanTrust) challenge = new(server.Host, server.Port, actual, expected);
        };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(timeout));
        try { await ssh.ConnectAsync(limit.Token).ConfigureAwait(false); return ssh; }
        catch { ssh.Dispose(); if (challenge is not null) throw new HostKeyException(challenge); throw; }
    }
    private async Task<string> Execute(string script, CancellationToken token)
    {
        var active = client ?? throw new InvalidOperationException("Not connected.");
        using var command = active.CreateCommand(script, Encoding.UTF8);
        command.CommandTimeout = TimeSpan.FromSeconds(timeout);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(timeout));
        using var abort = limit.Token.Register(() => { try { active.Dispose(); } catch (ObjectDisposedException) { } });
        try
        {
            var run = command.ExecuteAsync(limit.Token);
            var stdout = BoundedRead(command.OutputStream, LinuxCollector.MaxOutputBytes, limit.Token, active);
            var stderr = BoundedRead(command.ExtendedOutputStream, 4096, limit.Token, active);
            await Task.WhenAll(run, stdout, stderr).ConfigureAwait(false);
            if (command.ExitStatus != 0) throw new MetricFormatException("Collector command failed; required Linux /proc utilities may be unavailable.");
            return Encoding.UTF8.GetString(await stdout.ConfigureAwait(false));
        }
        catch when (limit.IsCancellationRequested && !token.IsCancellationRequested) { Abort(); throw new TimeoutException("SSH collection timed out."); }
        catch { Abort(); throw; }
    }
    private static async Task<byte[]> BoundedRead(Stream stream, int max, CancellationToken token, SshClient owner)
    {
        using var output = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) break;
            if (output.Length + count > max) { owner.Dispose(); throw new MetricFormatException("SSH output limit exceeded."); }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public void Abort()
    {
        try { client?.Dispose(); } catch (ObjectDisposedException) { } client = null;
        try { forward?.Dispose(); } catch (ObjectDisposedException) { } forward = null;
        try { jumpClient?.Dispose(); } catch (ObjectDisposedException) { } jumpClient = null;
        foreach (var key in keys) key.Dispose(); keys.Clear();
    }
    public ValueTask DisposeAsync() { Abort(); return ValueTask.CompletedTask; }
    // SSH.NET lists SHA-1 RSA first. Modern SSH servers can disconnect on that
    // first offer, so offer SHA-2 RSA signatures before any authentication attempt.
    private sealed class ModernPrivateKey(PrivateKeyFile key) : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = key.HostKeyAlgorithms.Where(a => a.Name is not ("ssh-rsa" or "ssh-dss" or "ssh-rsa-cert-v01@openssh.com")).ToArray();
    }
}
