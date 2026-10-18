using CloudyWing.AiAccountManager.Windows.Notifications;
using CloudyWing.AiAccountManager.Windows.Storage;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class NotificationDedupStoreTests {
    [Test]
    public async Task SaveAsync_NewStoreInstance_RestoresSentCycles() {
        string root = Path.Combine(Path.GetTempPath(), "AiAccountManagerTests", Guid.NewGuid().ToString("N"));
        try {
            AtomicJsonFileStore files = new(new CurrentUserDataDirectory(root));
            JsonNotificationDedupStore first = new(files);
            _ = await first.LoadAsync(CancellationToken.None);
            await first.SaveAsync(["codex/example/five-hour/cycle"], CancellationToken.None);
            JsonNotificationDedupStore second = new(files);

            IReadOnlyCollection<string> loaded = await second.LoadAsync(CancellationToken.None);

            Assert.That(loaded, Is.EquivalentTo(new[] { "codex/example/five-hour/cycle" }));
        } finally {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
