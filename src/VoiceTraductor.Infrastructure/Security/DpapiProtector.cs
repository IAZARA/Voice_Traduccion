using System.Security.Cryptography;
using System.Text;
using VoiceTraductor.Core;

namespace VoiceTraductor.Infrastructure.Security;

public sealed class DpapiProtector
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("VoiceTraductor/v1/current-user");

    public byte[] Protect(string value) =>
        ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            Entropy,
            DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] protectedValue) =>
        Encoding.UTF8.GetString(
            ProtectedData.Unprotect(
                protectedValue,
                Entropy,
                DataProtectionScope.CurrentUser));
}

public sealed class DpapiCredentialStore(string applicationDataDirectory)
    : ICredentialStore
{
    private readonly string _path =
        Path.Combine(applicationDataDirectory, "credential.bin");
    private readonly DpapiProtector _protector = new();

    public bool Exists => File.Exists(_path);

    public async Task SaveAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("La API key no puede estar vacía.", nameof(apiKey));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllBytesAsync(
                _path,
                _protector.Protect(apiKey.Trim()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var protectedValue = await File.ReadAllBytesAsync(_path, cancellationToken)
            .ConfigureAwait(false);
        return _protector.Unprotect(protectedValue);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }
}
