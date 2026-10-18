using System.Reflection;
using System.Text;
using System.Threading.Channels;
using CloudyWing.AiAccountManager.Windows.Codex;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class WindowsCodexLocalAuthFileChangeNotifierTests {
    [Test]
    public async Task StartAsync_AuthFileChangesRaiseDebouncedEventsForOnlyTargetFile() {
        using TemporaryDirectory directory = new(createCodexDirectory: true);
        WindowsCodexLocalAuthFileChangeNotifier notifier = CreateNotifier(directory.Path);
        Channel<bool> changes = Channel.CreateUnbounded<bool>();
        notifier.FileChanged += () => changes.Writer.TryWrite(true);
        await notifier.StartAsync(CancellationToken.None);

        File.WriteAllText(Path.Combine(directory.CodexPath, "unrelated.json"), "fixture");
        await Task.Delay(500);
        Assert.That(changes.Reader.TryRead(out _), Is.False);

        File.WriteAllText(directory.AuthPath, "fixture-auth-one");
        await ReadChangeAsync(changes);
        await Task.Delay(450);
        int afterCreate = Drain(changes);

        string stagingPath = Path.Combine(directory.CodexPath, "auth.json.tmp");
        File.Move(directory.AuthPath, stagingPath);
        File.Move(stagingPath, directory.AuthPath);
        await ReadChangeAsync(changes);
        await Task.Delay(450);
        int afterRename = Drain(changes);

        File.WriteAllText(directory.AuthPath, "fixture-auth-two");
        await ReadChangeAsync(changes);
        await Task.Delay(450);
        int afterWrite = Drain(changes);

        File.Delete(directory.AuthPath);
        await ReadChangeAsync(changes);
        await notifier.StopAsync();

        Assert.That(afterCreate, Is.EqualTo(0));
        Assert.That(afterRename, Is.EqualTo(0));
        Assert.That(afterWrite, Is.EqualTo(0));
    }

    [Test]
    public async Task StartAsync_WhenCodexDirectoryIsMissing_WatchesDirectoryCreation() {
        using TemporaryDirectory directory = new(createCodexDirectory: false);
        WindowsCodexLocalAuthFileChangeNotifier notifier = CreateNotifier(directory.Path);
        Channel<bool> changes = Channel.CreateUnbounded<bool>();
        Channel<bool> availability = Channel.CreateUnbounded<bool>();
        notifier.FileChanged += () => changes.Writer.TryWrite(true);
        notifier.MonitoringAvailabilityChanged += value => availability.Writer.TryWrite(value);

        await notifier.StartAsync(CancellationToken.None);
        bool availableAtStart = await ReadBooleanAsync(availability);
        Directory.CreateDirectory(directory.CodexPath);
        File.WriteAllText(directory.AuthPath, "fixture-auth-created");
        await ReadChangeAsync(changes);

        await notifier.StopAsync();
        Assert.That(availableAtStart, Is.True);
    }

    [Test]
    public async Task StartAsync_BurstOfEventsRaisesOneNotificationAfterQuietPeriod() {
        using TemporaryDirectory directory = new(createCodexDirectory: true);
        WindowsCodexLocalAuthFileChangeNotifier notifier = CreateNotifier(directory.Path);
        Channel<bool> changes = Channel.CreateUnbounded<bool>();
        notifier.FileChanged += () => changes.Writer.TryWrite(true);
        await notifier.StartAsync(CancellationToken.None);

        for (int index = 0; index < 3; index++) {
            File.WriteAllText(directory.AuthPath, $"fixture-auth-{index}");
            await Task.Delay(75);
        }

        await ReadChangeAsync(changes);
        await Task.Delay(450);
        int additionalNotifications = Drain(changes);
        await notifier.StopAsync();

        Assert.That(additionalNotifications, Is.EqualTo(0));
    }

    [Test]
    public async Task StopAndRestart_RecreatesWatcherAndHostDisposeReleasesIt() {
        using TemporaryDirectory directory = new(createCodexDirectory: true);
        WindowsCodexLocalAuthFileChangeNotifier notifier = CreateNotifier(directory.Path);
        Channel<bool> changes = Channel.CreateUnbounded<bool>();
        Channel<bool> availability = Channel.CreateUnbounded<bool>();
        notifier.FileChanged += () => changes.Writer.TryWrite(true);
        notifier.MonitoringAvailabilityChanged += value => availability.Writer.TryWrite(value);

        await notifier.StartAsync(CancellationToken.None);
        bool firstStart = await ReadBooleanAsync(availability);
        await notifier.StopAsync();
        bool stopped = await ReadBooleanAsync(availability);
        await notifier.StartAsync(CancellationToken.None);
        bool secondStart = await ReadBooleanAsync(availability);
        File.WriteAllText(directory.AuthPath, "fixture-auth-after-restart");
        await ReadChangeAsync(changes);
        await notifier.DisposeAsync();

        Assert.That(firstStart, Is.True);
        Assert.That(stopped, Is.False);
        Assert.That(secondStart, Is.True);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await notifier.StartAsync(CancellationToken.None)
        );
    }

    [Test]
    public async Task FileSystemWatcherError_RebuildsWatcherAndPublishesAvailability() {
        using TemporaryDirectory directory = new(createCodexDirectory: true);
        WindowsCodexLocalAuthFileChangeNotifier notifier = CreateNotifier(directory.Path);
        Channel<bool> availability = Channel.CreateUnbounded<bool>();
        notifier.MonitoringAvailabilityChanged += value => availability.Writer.TryWrite(value);
        await notifier.StartAsync(CancellationToken.None);
        bool initialAvailability = await ReadBooleanAsync(availability);

        MethodInfo? onError = typeof(WindowsCodexLocalAuthFileChangeNotifier).GetMethod(
            "OnError",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.That(onError, Is.Not.Null);
        onError!.Invoke(notifier, [new object(), new ErrorEventArgs(new IOException("fixture watcher error"))]);
        bool degraded = await ReadBooleanAsync(availability);
        bool restored = await ReadBooleanAsync(availability);

        await notifier.StopAsync();
        Assert.That(initialAvailability, Is.True);
        Assert.That(degraded, Is.False);
        Assert.That(restored, Is.True);
    }

    private static WindowsCodexLocalAuthFileChangeNotifier CreateNotifier(string userProfilePath) {
        ConstructorInfo? constructor = typeof(WindowsCodexLocalAuthFileChangeNotifier).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string)],
            modifiers: null
        );
        Assert.That(constructor, Is.Not.Null);
        return (WindowsCodexLocalAuthFileChangeNotifier)constructor!.Invoke([userProfilePath]);
    }

    private static async Task ReadChangeAsync(Channel<bool> changes) {
        await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<bool> ReadBooleanAsync(Channel<bool> values) {
        return await values.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static int Drain(Channel<bool> channel) {
        int count = 0;
        while (channel.Reader.TryRead(out _)) {
            count++;
        }

        return count;
    }

    private sealed class TemporaryDirectory : IDisposable {
        public TemporaryDirectory(bool createCodexDirectory) {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ai-account-manager-auth-watcher-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
            CodexPath = System.IO.Path.Combine(Path, ".codex");
            AuthPath = System.IO.Path.Combine(CodexPath, "auth.json");
            if (createCodexDirectory) {
                Directory.CreateDirectory(CodexPath);
            }
        }

        public string Path { get; }

        public string CodexPath { get; }

        public string AuthPath { get; }

        public void Dispose() {
            if (Directory.Exists(Path)) {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
