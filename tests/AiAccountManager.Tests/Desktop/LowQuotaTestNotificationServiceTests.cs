using CloudyWing.AiAccountManager.Application.Notifications;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Desktop;
using CloudyWing.AiAccountManager.Tests.Codex;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class LowQuotaTestNotificationServiceTests {
    [Test]
    public async Task SendAsync_SendsFormalFormatWithSampleValues() {
        FakeSink sink = new();
        LowQuotaTestNotificationService service = await CreateServiceAsync(sink, new FakeTray(), AppSettings.Defaults);

        bool sent = await service.SendAsync(CancellationToken.None);

        Assert.That(sink.Requests, Has.Count.EqualTo(1));
        NotificationRequest request = sink.Requests[0];
        using (Assert.EnterMultipleScope()) {
            Assert.That(sent, Is.True);
            Assert.That(request.Title, Is.EqualTo("額度偏低"));
            Assert.That(request.Message, Is.EqualTo("Codex 5 小時額度剩 12%，建議切換至 範例帳號（測試通知）"));
            Assert.That(request.Type, Is.EqualTo("low-quota"));
            Assert.That(request.Provider, Is.EqualTo("codex"));
            Assert.That(request.Activatable, Is.False);
        }
    }

    [Test]
    public async Task SendAsync_SinkFails_ReturnsFalse() {
        FakeSink sink = new() { Result = false };
        LowQuotaTestNotificationService service = await CreateServiceAsync(sink, new FakeTray(), AppSettings.Defaults);

        bool sent = await service.SendAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(sent, Is.False);
            Assert.That(sink.Requests, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task SendAsync_SetVisibleThrows_ReturnsFalseWithoutSending() {
        FakeSink sink = new();
        FakeTray tray = new() { ThrowOnSetVisible = true };
        LowQuotaTestNotificationService service = await CreateServiceAsync(sink, tray, AppSettings.Defaults);

        bool sent = await service.SendAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(sent, Is.False);
            Assert.That(sink.Requests, Is.Empty);
        }
    }

    [Test]
    public async Task SendAsync_DoesNotTouchDedupStore() {
        FakeSink sink = new();
        FakeStore store = new();
        LowQuotaTestNotificationService service = await CreateServiceAsync(sink, new FakeTray(), AppSettings.Defaults);
        LowQuotaNotificationEvaluator evaluator = new(sink, store);
        NotificationSnapshot low = new("codex", Guid.NewGuid(), null, [new("five-hour", "5 小時", 10, null)]);

        await service.SendAsync(CancellationToken.None);
        int loadCountAfterTest = store.LoadCount;
        int saveCountAfterTest = store.SaveCount;
        await evaluator.EvaluateAsync([low], true, 20, CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(loadCountAfterTest, Is.Zero);
            Assert.That(saveCountAfterTest, Is.Zero);
            Assert.That(sink.Requests, Has.Count.EqualTo(2));
            Assert.That(sink.Requests[1].Activatable, Is.True);
            Assert.That(store.Keys, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task SendAsync_NotificationsAndTrayDisabled_RestoresIconToHidden() {
        FakeTray tray = new();
        AppSettings settings = AppSettings.Defaults with { LowQuotaNotificationsEnabled = false, MinimizeToTray = false };
        LowQuotaTestNotificationService service = await CreateServiceAsync(new FakeSink(), tray, settings, TimeSpan.Zero);

        await service.SendAsync(CancellationToken.None);

        Assert.That(() => tray.Calls, Is.EqualTo(new[] { true, false }).After(2000, 10));
    }

    [Test]
    public async Task SendAsync_NotificationsEnabled_RestoreKeepsIconVisible() {
        FakeTray tray = new();
        AppSettings settings = AppSettings.Defaults with { LowQuotaNotificationsEnabled = true, MinimizeToTray = false };
        LowQuotaTestNotificationService service = await CreateServiceAsync(new FakeSink(), tray, settings, TimeSpan.Zero);

        await service.SendAsync(CancellationToken.None);

        Assert.That(() => tray.Calls, Is.EqualTo(new[] { true, true }).After(2000, 10));
    }

    [Test]
    public async Task SendAsync_TwoQuickSends_OnlyLatestRestores() {
        FakeTray tray = new();
        AppSettings settings = AppSettings.Defaults with { LowQuotaNotificationsEnabled = false, MinimizeToTray = false };
        LowQuotaTestNotificationService service = await CreateServiceAsync(
            new FakeSink(),
            tray,
            settings,
            TimeSpan.FromSeconds(2)
        );

        await service.SendAsync(CancellationToken.None);
        await service.SendAsync(CancellationToken.None);

        Assert.That(() => tray.Calls, Has.Length.EqualTo(3).After(6000, 10));
        await Task.Delay(200);
        Assert.That(tray.Calls, Is.EqualTo(new[] { true, true, false }));
    }

    private static async Task<LowQuotaTestNotificationService> CreateServiceAsync(
        FakeSink sink,
        FakeTray tray,
        AppSettings settings,
        TimeSpan? visibilityRestoreDelay = null
    ) {
        SettingsService settingsService = new(new FakeSettingsRepository(settings), new FakeAccountCatalogRepository());
        await settingsService.InitializeAsync(CancellationToken.None);
        return new LowQuotaTestNotificationService(
            sink,
            tray,
            settingsService,
            visibilityRestoreDelay: visibilityRestoreDelay ?? TimeSpan.Zero
        );
    }

    private sealed class FakeSink : INotificationSink {
        public event Action<string>? Activated { add { } remove { } }
        public bool Result { get; set; } = true;
        public List<NotificationRequest> Requests { get; } = [];
        public Task<bool> SendAsync(NotificationRequest request, CancellationToken cancellationToken) {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeTray : ITrayService {
        private readonly object sync = new();
        private readonly List<bool> calls = [];
        public event EventHandler? OpenRequested { add { } remove { } }
        public event EventHandler? ExitRequested { add { } remove { } }
        public event EventHandler? BalloonClicked { add { } remove { } }
        public bool ThrowOnSetVisible { get; set; }
        public bool[] Calls {
            get {
                lock (sync) {
                    return calls.ToArray();
                }
            }
        }
        public void SetVisible(bool visible) {
            if (ThrowOnSetVisible) {
                throw new InvalidOperationException("synthetic tray failure");
            }
            lock (sync) {
                calls.Add(visible);
            }
        }
        public void ShowBalloon(string title, string message) { }
        public void Dispose() { }
    }

    private sealed class FakeStore : INotificationDedupStore {
        public IReadOnlyCollection<string> Keys { get; private set; } = [];
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }
        public Task<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken) {
            LoadCount++;
            return Task.FromResult(Keys);
        }
        public Task SaveAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) {
            SaveCount++;
            Keys = keys.ToArray();
            return Task.CompletedTask;
        }
    }
}
