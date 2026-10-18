using System.Text;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Windows.Codex;
using CloudyWing.AiAccountManager.Windows.Storage;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class WindowsCodexDesktopAdapterTests {
    [Test]
    public async Task AuthStore_ReplaceAsync_WritesNewContentReadableWithIdentity() {
        using TemporaryDirectory directory = new();
        Directory.CreateDirectory(Path.Combine(directory.Path, ".codex"));
        File.WriteAllText(Path.Combine(directory.Path, ".codex", "auth.json"), AuthJson("fixture-account-a"));
        WindowsCodexDesktopAuthStore store = new(new WindowsCodexLocalAuthIdentityReader(directory.Path));

        await store.ReplaceAsync(Encoding.UTF8.GetBytes(AuthJson("fixture-account-b")), CancellationToken.None);
        ICodexDesktopAuthStore.ReadResult result = await store.ReadAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(result.Status, Is.EqualTo(CodexLocalAuthReadStatus.Success));
            Assert.That(result.Identity!.AccountId, Is.EqualTo("fixture-account-b"));
            Assert.That(Encoding.UTF8.GetString(result.Content!), Is.EqualTo(AuthJson("fixture-account-b")));
            Assert.That(
                Directory.EnumerateFiles(Path.Combine(directory.Path, ".codex"), "*.tmp"),
                Is.Empty
            );
        });
    }

    [Test]
    public void AuthStore_Parse_RejectsContentWithoutIdentity() {
        using TemporaryDirectory directory = new();
        WindowsCodexDesktopAuthStore store = new(new WindowsCodexLocalAuthIdentityReader(directory.Path));

        CodexLocalAuthReadResult result = store.Parse(Encoding.UTF8.GetBytes("{\"auth_mode\":\"chatgpt\"}"));

        Assert.That(result.Status, Is.Not.EqualTo(CodexLocalAuthReadStatus.Success));
    }

    [Test]
    public async Task SnapshotRepository_RoundTripsSnapshotsAndJournalWithoutPlaintextOnDisk() {
        using TemporaryDirectory directory = new();
        CurrentUserDataDirectory dataDirectory = new(directory.Path);
        WindowsCodexDesktopSnapshotRepository repository = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        Guid accountId = Guid.NewGuid();
        byte[] content = Encoding.UTF8.GetBytes(AuthJson("fixture-secret-account"));

        await repository.SaveAsync(accountId, content, CancellationToken.None);
        await repository.SaveAsync(accountId, content, CancellationToken.None);
        await repository.SaveJournalAsync(
            new(Guid.NewGuid(), accountId, Guid.NewGuid(), content, true, "OpenAI.Codex_fixture!App", DateTimeOffset.UtcNow),
            CancellationToken.None
        );
        byte[]? loaded = await repository.LoadAsync(accountId, CancellationToken.None);
        ICodexDesktopSnapshotRepository.SwitchJournal? journal = await repository.LoadJournalAsync(CancellationToken.None);
        IReadOnlySet<Guid> listed = await repository.ListAsync(CancellationToken.None);
        string[] diskContents = Directory.EnumerateFiles(directory.Path, "*.json", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Multiple(() => {
            Assert.That(loaded, Is.EqualTo(content));
            Assert.That(journal!.OriginalAuthContent, Is.EqualTo(content));
            Assert.That(journal.AppUserModelId, Is.EqualTo("OpenAI.Codex_fixture!App"));
            Assert.That(listed, Is.EquivalentTo(new[] { accountId }));
            Assert.That(diskContents, Has.None.Contains("fixture-secret-account"));
        });

        await repository.DeleteAsync(accountId, CancellationToken.None);
        await repository.DeleteJournalAsync(CancellationToken.None);

        byte[]? deleted = await repository.LoadAsync(accountId, CancellationToken.None);
        ICodexDesktopSnapshotRepository.SwitchJournal? deletedJournal = await repository.LoadJournalAsync(CancellationToken.None);

        Assert.That(deleted, Is.Null);
        Assert.That(deletedJournal, Is.Null);
    }

    [TestCase("OpenAI.Codex_26.1028.100.0_x64__2p2nqsd0c76g0", "App", "OpenAI.Codex_2p2nqsd0c76g0!App")]
    [TestCase("OpenAI.ChatGPT_1.0.0.0_x64__2p2nqsd0c76g0", "App", null)]
    [TestCase("OpenAI.Codex_bad", "App", null)]
    public void ProcessController_GetAppUserModelId_UsesPackageFamilyName(
        string packageDirectoryName,
        string applicationId,
        string? expected
    ) {
        Assert.That(
            WindowsCodexDesktopProcessController.GetAppUserModelId(packageDirectoryName, applicationId),
            Is.EqualTo(expected)
        );
    }

    private static string AuthJson(string accountId) {
        return $"{{\"auth_mode\":\"chatgpt\",\"account_id\":{JsonSerializer.Serialize(accountId)}}}";
    }

    private sealed class TemporaryDirectory : IDisposable {
        public TemporaryDirectory() {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ai-account-manager-codex-desktop-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() {
            if (Directory.Exists(Path)) {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
