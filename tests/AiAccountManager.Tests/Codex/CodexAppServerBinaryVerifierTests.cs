using System.Security.Cryptography;
using System.Text.Json;
using CloudyWing.AiAccountManager.Windows.Codex;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class CodexAppServerBinaryVerifierTests {
    private string? temporaryRoot;
    private string ExecutablePath => Path.Combine(VersionDirectory, "codex-app-server-x86_64-pc-windows-msvc.exe");
    private string ManifestPath => Path.Combine(VersionDirectory, "codex-app-server.manifest.json");
    private string VersionDirectory => Path.Combine(
        temporaryRoot ?? throw new InvalidOperationException("Test directory is not initialized."),
        "tools",
        "codex-app-server",
        "0.162.1"
    );

    [SetUp]
    public void SetUp() {
        temporaryRoot = Path.Combine(Path.GetTempPath(), "AiAccountManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(VersionDirectory);
    }

    [TearDown]
    public void TearDown() {
        if (temporaryRoot is not null && Directory.Exists(temporaryRoot)) {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Test]
    public void PinnedExecutableHashIsAccepted() {
        byte[] executable = [1, 4, 9, 16, 25];
        string executableHash = Convert.ToHexString(SHA256.HashData(executable));
        string archiveHash = new('A', 64);
        File.WriteAllBytes(ExecutablePath, executable);
        WriteManifest(archiveHash, executableHash);

        CodexAppServerBinaryVerifier verifier = new(temporaryRoot, archiveHash, executableHash);

        Assert.That(verifier.VerifyAndGetExecutablePath(), Is.EqualTo(ExecutablePath));
    }

    [Test]
    public void ExecutableHashMismatchIsRejected() {
        byte[] executable = [1, 4, 9, 16, 25];
        string executableHash = Convert.ToHexString(SHA256.HashData(executable));
        string archiveHash = new('A', 64);
        File.WriteAllBytes(ExecutablePath, executable);
        WriteManifest(archiveHash, executableHash);
        File.AppendAllText(ExecutablePath, "changed");

        CodexAppServerBinaryVerifier verifier = new(temporaryRoot, archiveHash, executableHash);

        Assert.Throws<InvalidDataException>(() => verifier.VerifyAndGetExecutablePath());
    }

    [Test]
    public void ArchiveHashOrManifestVersionMismatchIsRejected() {
        byte[] executable = [1, 4, 9, 16, 25];
        string executableHash = Convert.ToHexString(SHA256.HashData(executable));
        string archiveHash = new('A', 64);
        File.WriteAllBytes(ExecutablePath, executable);
        WriteManifest(new('B', 64), executableHash);

        CodexAppServerBinaryVerifier verifier = new(temporaryRoot, archiveHash, executableHash);

        Assert.Throws<InvalidDataException>(() => verifier.VerifyAndGetExecutablePath());
    }

    [Test]
    public void MissingManifestIsRejected() {
        File.WriteAllBytes(ExecutablePath, [1, 2, 3]);

        CodexAppServerBinaryVerifier verifier = new(temporaryRoot, new('A', 64), new('B', 64));

        Assert.Throws<FileNotFoundException>(() => verifier.VerifyAndGetExecutablePath());
    }

    private void WriteManifest(string archiveHash, string executableHash) {
        object manifest = new {
            schema = "ai-account-manager.codex-app-server-binary.v1",
            version = CodexAppServerBinaryVerifier.AppServerVersion,
            archiveSha256 = archiveHash,
            executableSha256 = executableHash
        };
        File.WriteAllBytes(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

