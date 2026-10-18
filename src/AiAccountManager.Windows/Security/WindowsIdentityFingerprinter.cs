using System.Security.Cryptography;
using CloudyWing.AiAccountManager.Application.Security;
using CloudyWing.AiAccountManager.Windows.Storage;

namespace CloudyWing.AiAccountManager.Windows.Security;

internal sealed class WindowsIdentityFingerprinter : IIdentityFingerprinter, IDisposable {
    private readonly CurrentUserDataDirectory dataDirectory;
    private readonly DpapiSecretProtector secretProtector;
    private readonly Lock sync = new();
    private byte[]? identityKey;

    internal WindowsIdentityFingerprinter(
        CurrentUserDataDirectory dataDirectory,
        DpapiSecretProtector secretProtector
    ) {
        this.dataDirectory = dataDirectory;
        this.secretProtector = secretProtector;
    }

    public string CreateFingerprint(string? accountId, string? email, string authMode) {
        ArgumentException.ThrowIfNullOrWhiteSpace(authMode);
        string identity = !string.IsNullOrWhiteSpace(accountId)
            ? "account:" + accountId.Trim()
            : "email:" + NormalizeEmail(email) + "|mode:" + authMode.Trim().ToLowerInvariant();
        byte[] key = GetIdentityKeyCopy();
        byte[] input = System.Text.Encoding.UTF8.GetBytes(identity);
        byte[] digest;
        try {
            digest = HMACSHA256.HashData(key, input);
        } finally {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(key);
        }

        try {
            return Convert.ToHexString(digest);
        } finally {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public string MaskEmail(string? email) {
        if (string.IsNullOrWhiteSpace(email)) {
            return "Codex 帳號";
        }

        string normalized = NormalizeEmail(email);
        int separator = normalized.IndexOf('@');
        if (separator <= 0 || separator == normalized.Length - 1) {
            return "Codex 帳號";
        }

        return normalized[..1] + "•••@" + normalized[(separator + 1)..];
    }

    public void Dispose() {
        lock (sync) {
            if (identityKey is not null) {
                CryptographicOperations.ZeroMemory(identityKey);
                identityKey = null;
            }
        }
    }

    private byte[] GetIdentityKeyCopy() {
        lock (sync) {
            identityKey ??= LoadOrCreateIdentityKey();
            return (byte[])identityKey.Clone();
        }
    }

    private byte[] LoadOrCreateIdentityKey() {
        string root = dataDirectory.EnsureCreated();
        string directory = Path.Combine(root, "secrets");
        Directory.CreateDirectory(directory);
        string keyPath = Path.Combine(directory, "identity-key.dpapi");
        if (File.Exists(keyPath)) {
            return ReadAndValidateKey(keyPath);
        }

        byte[] newKey = RandomNumberGenerator.GetBytes(32);
        byte[]? protectedNewKey = null;
        string temporaryPath = keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool committed = false;
        Exception? operationFailure = null;
        try {
            protectedNewKey = secretProtector.Protect(newKey);
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough
            )) {
                stream.Write(protectedNewKey);
                stream.Flush(flushToDisk: true);
            }

            try {
                File.Move(temporaryPath, keyPath);
                committed = true;
                return newKey;
            } catch (IOException) when (File.Exists(keyPath)) {
                CryptographicOperations.ZeroMemory(newKey);
                return ReadAndValidateKey(keyPath);
            }
        } catch (Exception failure) {
            operationFailure = failure;
            if (!committed) {
                CryptographicOperations.ZeroMemory(newKey);
            }

            throw;
        } finally {
            if (protectedNewKey is not null) {
                CryptographicOperations.ZeroMemory(protectedNewKey);
            }

            if (File.Exists(temporaryPath)) {
                try {
                    File.Delete(temporaryPath);
                } catch (Exception cleanupFailure) {
                    if (committed) {
                        CryptographicOperations.ZeroMemory(newKey);
                    }

                    if (operationFailure is not null) {
                        throw new AggregateException(
                            "Identity-key creation failed and its staging file could not be removed.",
                            operationFailure,
                            cleanupFailure
                        );
                    }

                    throw new IOException("Identity-key staging file could not be removed.", cleanupFailure);
                }
            }
        }
    }

    private byte[] ReadAndValidateKey(string keyPath) {
        byte[] protectedKey = File.ReadAllBytes(keyPath);
        byte[] key;
        try {
            key = secretProtector.Unprotect(protectedKey);
        } finally {
            CryptographicOperations.ZeroMemory(protectedKey);
        }

        if (key.Length == 32) {
            return key;
        }

        CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException("產品 identity key 長度無效。");
    }

    private static string NormalizeEmail(string? email) {
        return (email ?? "").Trim().ToLowerInvariant();
    }
}
