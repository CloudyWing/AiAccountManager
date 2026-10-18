using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Windows.Codex;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class WindowsCodexLocalAuthIdentityReaderTests {
    [Test]
    public async Task ReadCurrentAsync_ValidAccountFields_ReturnsIdentityWithoutExposingItInToString() {
        using TemporaryDirectory directory = new();
        directory.CreateCodexDirectory();
        directory.WriteAuth(AuthJson("fixture-account-0001", "reader@example.test"));
        WindowsCodexLocalAuthIdentityReader reader = CreateReader(directory.Path);

        CodexLocalAuthReadResult result = await reader.ReadCurrentAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CodexLocalAuthReadStatus.Success));
        CodexLocalAuthIdentity identity = result.Identity
            ?? throw new InvalidOperationException("Successful reader result did not contain fixture identity.");
        Assert.That(identity.AccountId, Is.EqualTo("fixture-account-0001"));
        Assert.That(identity.Email, Is.EqualTo("reader@example.test"));
        StringAssert.DoesNotContain("fixture-account-0001", identity.ToString());
        StringAssert.DoesNotContain("reader@example.test", identity.ToString());
    }

    [Test]
    public async Task ReadCurrentAsync_JwtClaims_ReturnsIdentityFromPayload() {
        using TemporaryDirectory directory = new();
        directory.CreateCodexDirectory();
        string token = JwtToken(
            "{\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"fixture-account-0002\"},"
                + "\"https://api.openai.com/profile\":{\"email\":\"jwt@example.test\"}}"
        );
        directory.WriteAuth($"{{\"auth_mode\":\"chatgpt\",\"tokens\":{{\"access_token\":\"{token}\"}}}}");
        WindowsCodexLocalAuthIdentityReader reader = CreateReader(directory.Path);

        CodexLocalAuthReadResult result = await reader.ReadCurrentAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CodexLocalAuthReadStatus.Success));
        Assert.That(result.Identity?.AccountId, Is.EqualTo("fixture-account-0002"));
        Assert.That(result.Identity?.Email, Is.EqualTo("jwt@example.test"));
    }

    [TestCase("{not-json", CodexLocalAuthReadStatus.InvalidFormat)]
    [TestCase("{\"auth_mode\":\"apikey\"}", CodexLocalAuthReadStatus.UnsupportedAuth)]
    [TestCase("{\"auth_mode\":\"chatgpt\",\"tokens\":{}}", CodexLocalAuthReadStatus.IdentityUnavailable)]
    public async Task ReadCurrentAsync_InvalidOrIncompleteData_ReturnsSafeStatus(
        string content,
        CodexLocalAuthReadStatus expectedStatus
    ) {
        using TemporaryDirectory directory = new();
        directory.CreateCodexDirectory();
        directory.WriteAuth(content);
        WindowsCodexLocalAuthIdentityReader reader = CreateReader(directory.Path);

        CodexLocalAuthReadResult result = await reader.ReadCurrentAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(expectedStatus));
        Assert.That(result.Identity, Is.Null);
    }

    [Test]
    public async Task ReadCurrentAsync_ConflictingClaims_ReturnsConflictingClaims() {
        using TemporaryDirectory directory = new();
        directory.CreateCodexDirectory();
        string token = JwtToken("{\"chatgpt_account_id\":\"fixture-account-0099\"}");
        directory.WriteAuth(
            $"{{\"auth_mode\":\"chatgpt\",\"account_id\":\"fixture-account-0003\","
                + $"\"tokens\":{{\"id_token\":\"{token}\"}}}}"
        );
        WindowsCodexLocalAuthIdentityReader reader = CreateReader(directory.Path);

        CodexLocalAuthReadResult result = await reader.ReadCurrentAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CodexLocalAuthReadStatus.ConflictingClaims));
        Assert.That(result.Identity, Is.Null);
    }

    [Test]
    public async Task ReadCurrentAsync_MissingAuthFile_ReturnsFileMissing() {
        using TemporaryDirectory directory = new();
        directory.CreateCodexDirectory();
        WindowsCodexLocalAuthIdentityReader reader = CreateReader(directory.Path);

        CodexLocalAuthReadResult result = await reader.ReadCurrentAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CodexLocalAuthReadStatus.FileMissing));
    }

    [Test]
    public async Task ReadCurrentAsync_AuthFileExceedsLimit_ReturnsFileTooLarge() {
        using TemporaryDirectory directory = new();
        directory.CreateCodexDirectory();
        directory.WriteAuth(new byte[(1024 * 1024) + 1]);
        WindowsCodexLocalAuthIdentityReader reader = CreateReader(directory.Path);

        CodexLocalAuthReadResult result = await reader.ReadCurrentAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CodexLocalAuthReadStatus.FileTooLarge));
    }

    [Test]
    public async Task ReadCurrentAsync_ExclusiveWriterHeld_ReturnsSharingViolation() {
        using TemporaryDirectory directory = new();
        directory.CreateCodexDirectory();
        directory.WriteAuth(AuthJson("fixture-account-0004"));
        using FileStream writer = new(
            directory.AuthPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        );
        WindowsCodexLocalAuthIdentityReader reader = CreateReader(directory.Path);

        CodexLocalAuthReadResult result = await reader.ReadCurrentAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CodexLocalAuthReadStatus.SharingViolation));
    }

    [Test]
    public async Task ReadCurrentAsync_PathIsReparsePoint_ReturnsUnsafePath() {
        using TemporaryDirectory directory = new();
        string targetDirectory = Path.Combine(directory.Path, "auth-target");
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(Path.Combine(targetDirectory, "auth.json"), AuthJson("fixture-account-0005"));
        string codexPath = Path.Combine(directory.Path, ".codex");
        try {
            Directory.CreateSymbolicLink(codexPath, targetDirectory);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException or System.Security.SecurityException) {
            Assert.Ignore("目前測試環境無法建立目錄 reparse point。");
        }

        WindowsCodexLocalAuthIdentityReader reader = CreateReader(directory.Path);

        CodexLocalAuthReadResult result = await reader.ReadCurrentAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CodexLocalAuthReadStatus.UnsafePath));
    }

    [Test]
    public async Task ReadCurrentAsync_FileIsReplacedDuringRead_RetriesThenReturnsChangedDuringRead() {
        using TemporaryDirectory directory = new();
        directory.CreateCodexDirectory();
        directory.WriteAuth(AuthJson("fixture-account-0006"));
        int replacementCount = 0;
        Action replaceFile = () => {
            int sequence = Interlocked.Increment(ref replacementCount);
            File.WriteAllText(directory.AuthPath, AuthJson($"fixture-account-{sequence:D4}"));
            File.SetLastWriteTimeUtc(directory.AuthPath, DateTime.UtcNow.AddMinutes(sequence));
        };
        WindowsCodexLocalAuthIdentityReader reader = CreateReader(directory.Path, replaceFile);

        CodexLocalAuthReadResult result = await reader.ReadCurrentAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CodexLocalAuthReadStatus.ChangedDuringRead));
        Assert.That(replacementCount, Is.EqualTo(2));
    }

    [Test]
    public async Task ReadCurrentAsync_JsonNestingExceedsLimit_ReturnsInvalidFormat() {
        using TemporaryDirectory directory = new();
        directory.CreateCodexDirectory();
        string content = "{\"auth_mode\":\"chatgpt\",\"account_id\":\"fixture-account-0007\",\"extra\":"
            + string.Concat(Enumerable.Repeat("{\"child\":", 34))
            + "null"
            + new string('}', 34)
            + "}";
        directory.WriteAuth(content);
        WindowsCodexLocalAuthIdentityReader reader = CreateReader(directory.Path);

        CodexLocalAuthReadResult result = await reader.ReadCurrentAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CodexLocalAuthReadStatus.InvalidFormat));
    }

    private static WindowsCodexLocalAuthIdentityReader CreateReader(
        string userProfilePath,
        Action? beforePathIdentityRecheck = null
    ) {
        ConstructorInfo? constructor = typeof(WindowsCodexLocalAuthIdentityReader).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(Action)],
            modifiers: null
        );
        Assert.That(constructor, Is.Not.Null);
        return (WindowsCodexLocalAuthIdentityReader)constructor!.Invoke([userProfilePath, beforePathIdentityRecheck]);
    }

    private static string AuthJson(string accountId, string? email = null) {
        string emailProperty = email is null
            ? ""
            : $",\"email\":{JsonSerializer.Serialize(email)}";
        return $"{{\"auth_mode\":\"chatgpt\",\"account_id\":{JsonSerializer.Serialize(accountId)}{emailProperty}}}";
    }

    private static string JwtToken(string payload) {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        try {
            string encodedPayload = Convert.ToBase64String(payloadBytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return $"fictional-header.{encodedPayload}.fictional-signature";
        } finally {
            CryptographicOperations.ZeroMemory(payloadBytes);
        }
    }

    private sealed class TemporaryDirectory : IDisposable {
        public TemporaryDirectory() {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ai-account-manager-auth-reader-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
            CodexPath = System.IO.Path.Combine(Path, ".codex");
            AuthPath = System.IO.Path.Combine(CodexPath, "auth.json");
        }

        public string Path { get; }

        public string CodexPath { get; }

        public string AuthPath { get; }

        public void CreateCodexDirectory() {
            Directory.CreateDirectory(CodexPath);
        }

        public void WriteAuth(string content) {
            File.WriteAllText(AuthPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public void WriteAuth(byte[] content) {
            File.WriteAllBytes(AuthPath, content);
        }

        public void Dispose() {
            if (Directory.Exists(Path)) {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
