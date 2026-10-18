using System.Security.Cryptography;
using System.Text.Json;

namespace CloudyWing.AiAccountManager.Windows.Claude;

/// <summary>
/// 解密 Claude Desktop 以 Electron safeStorage 加密的設定值。
/// </summary>
/// <remarks>
/// Claude Desktop 以 Local State 的 `os_crypt.encrypted_key` 保護 AES-256 key，設定值格式為 `v10` 或 `v11`、12 位元組 nonce、密文與 16 位元組 tag，整體再以 base64 儲存。
/// 解出的 key 與明文只在呼叫期間存在於記憶體，呼叫端負責清除回傳的緩衝區。
/// </remarks>
internal static class ClaudeSafeStorage {
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int VersionPrefixLength = 3;
    private static readonly byte[] DpapiPrefix = "DPAPI"u8.ToArray();

    /// <summary>
    /// 以目前 Windows 使用者的 DPAPI 解開 Local State 中的 AES key。
    /// </summary>
    /// <param name="localStateBytes">Local State 檔案內容。</param>
    /// <returns>32 位元組的 AES key；呼叫端用後須清除。</returns>
    /// <exception cref="InvalidDataException">Local State 缺少 key、格式不支援或無法解開。</exception>
    internal static byte[] UnprotectKey(byte[] localStateBytes) {
        ArgumentNullException.ThrowIfNull(localStateBytes);
        byte[] encrypted;
        try {
            using JsonDocument document = JsonDocument.Parse(localStateBytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("os_crypt", out JsonElement osCrypt)
                || osCrypt.ValueKind != JsonValueKind.Object
                || !osCrypt.TryGetProperty("encrypted_key", out JsonElement keyElement)
                || keyElement.ValueKind != JsonValueKind.String) {
                throw new InvalidDataException("Claude Local State encrypted key is missing.");
            }

            encrypted = Convert.FromBase64String(keyElement.GetString() ?? string.Empty);
        } catch (JsonException failure) {
            throw new InvalidDataException("Claude Local State is malformed.", failure);
        } catch (FormatException failure) {
            throw new InvalidDataException("Claude Local State encrypted key has an invalid format.", failure);
        }

        try {
            if (encrypted.Length <= DpapiPrefix.Length || !encrypted.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix)) {
                throw new InvalidDataException("Claude Local State encrypted key has an unsupported format.");
            }

            byte[] key;
            try {
                key = ProtectedData.Unprotect(encrypted[DpapiPrefix.Length..], null, DataProtectionScope.CurrentUser);
            } catch (CryptographicException failure) {
                throw new InvalidDataException("Claude Local State encrypted key cannot be unprotected.", failure);
            }

            if (key.Length != KeyLength) {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidDataException("Claude Local State key has an unsupported length.");
            }

            return key;
        } finally {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    /// <summary>
    /// 解密 safeStorage 加密的 base64 設定值。
    /// </summary>
    /// <param name="encryptedValue">設定檔中的 base64 加密字串。</param>
    /// <param name="key">由 <see cref="UnprotectKey(byte[])" /> 取得的 AES key。</param>
    /// <returns>明文的 UTF-8 位元組；呼叫端用後須清除。</returns>
    /// <exception cref="InvalidDataException">格式不支援或驗證失敗。</exception>
    internal static byte[] Decrypt(string encryptedValue, ReadOnlySpan<byte> key) {
        ArgumentNullException.ThrowIfNull(encryptedValue);
        if (key.Length != KeyLength) {
            throw new ArgumentException("The AES key must be 32 bytes.", nameof(key));
        }

        byte[] payload;
        try {
            payload = Convert.FromBase64String(encryptedValue);
        } catch (FormatException failure) {
            throw new InvalidDataException("Claude encrypted value has an invalid format.", failure);
        }

        try {
            if (payload.Length < VersionPrefixLength + NonceLength + TagLength
                || !IsSupportedVersion(payload.AsSpan(0, VersionPrefixLength))) {
                throw new InvalidDataException("Claude encrypted value has an unsupported format.");
            }

            ReadOnlySpan<byte> nonce = payload.AsSpan(VersionPrefixLength, NonceLength);
            int cipherLength = payload.Length - VersionPrefixLength - NonceLength - TagLength;
            ReadOnlySpan<byte> cipher = payload.AsSpan(VersionPrefixLength + NonceLength, cipherLength);
            ReadOnlySpan<byte> tag = payload.AsSpan(payload.Length - TagLength, TagLength);
            byte[] plaintext = new byte[cipherLength];
            try {
                using AesGcm aes = new(key, TagLength);
                aes.Decrypt(nonce, cipher, tag, plaintext);
                return plaintext;
            } catch (CryptographicException failure) {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new InvalidDataException("Claude encrypted value failed authentication.", failure);
            }
        } finally {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// 以 safeStorage 格式加密明文，供測試產生與實機相同格式的 fixture。
    /// </summary>
    /// <param name="plaintext">明文的 UTF-8 位元組。</param>
    /// <param name="key">32 位元組 AES key。</param>
    /// <returns>base64 加密字串。</returns>
    internal static string Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key) {
        byte[] payload = new byte[VersionPrefixLength + NonceLength + plaintext.Length + TagLength];
        try {
            "v10"u8.CopyTo(payload);
            RandomNumberGenerator.Fill(payload.AsSpan(VersionPrefixLength, NonceLength));
            using AesGcm aes = new(key, TagLength);
            aes.Encrypt(
                payload.AsSpan(VersionPrefixLength, NonceLength),
                plaintext,
                payload.AsSpan(VersionPrefixLength + NonceLength, plaintext.Length),
                payload.AsSpan(payload.Length - TagLength, TagLength)
            );
            return Convert.ToBase64String(payload);
        } finally {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// 判斷設定欄位的原始 JSON 是否為 safeStorage 加密字串。
    /// </summary>
    /// <param name="rawJson">設定欄位的原始 JSON。</param>
    /// <param name="encryptedValue">加密字串；不是加密字串時為 <see langword="null" />。</param>
    /// <returns>欄位值為 JSON 字串時為 <see langword="true" />。</returns>
    internal static bool TryReadEncryptedValue(string rawJson, out string? encryptedValue) {
        using JsonDocument document = JsonDocument.Parse(rawJson, new JsonDocumentOptions { MaxDepth = 64 });
        encryptedValue = document.RootElement.ValueKind == JsonValueKind.String
            ? document.RootElement.GetString()
            : null;
        return encryptedValue is not null;
    }

    private static bool IsSupportedVersion(ReadOnlySpan<byte> prefix) {
        return prefix.SequenceEqual("v10"u8) || prefix.SequenceEqual("v11"u8);
    }
}
