using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Desktop.Components;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class CodexResetNewsServiceTests {
    [Test]
    public async Task ScheduledAndLatestAreParsedSeparatelyAndThrottled() {
        NewsFixture fixture = await NewsFixture.CreateAsync();
        using CodexResetNewsService service = fixture.Service;
        fixture.Client.Result = Response("""
            {"data":{"scheduled_reset":{"scheduled_for":"2026-10-28T06:29:38Z","source":{"url":"https://example.test/next"}},
             "latest_reset":{"announced_at":"2026-10-27T06:29:38Z","source":{"url":"http://example.test/bad"}},
             "stats":{"avg_interval_days":14.5}}}
            """);
        await service.RefreshAsync(CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(fixture.Client.Count, Is.EqualTo(1));
        Assert.That(service.Current.HasScheduled, Is.True);
        Assert.That(service.Current.ScheduledAtUtc, Is.EqualTo(DateTimeOffset.Parse("2026-10-28T06:29:38Z")));
        Assert.That(TaiwanTimeUtils.Format(service.Current.ScheduledAtUtc!.Value), Is.EqualTo("10/28 14:29"));
        Assert.That(service.Current.LatestAtUtc, Is.EqualTo(DateTimeOffset.Parse("2026-10-27T06:29:38Z")));
        Assert.That(service.Current.ScheduledUrl?.Scheme, Is.EqualTo("https"));
        Assert.That(service.Current.LatestUrl, Is.Null);
        Assert.That(service.Current.AverageDays, Is.EqualTo(14.5));
        fixture.Clock.UtcNow += TimeSpan.FromMinutes(15);
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(fixture.Client.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task RetryAfterExtendsFifteenMinuteFloor() {
        NewsFixture fixture = await NewsFixture.CreateAsync();
        using CodexResetNewsService service = fixture.Service;
        fixture.Client.Result = new(null, true, fixture.Clock.UtcNow.AddMinutes(30));
        await service.RefreshAsync(CancellationToken.None);
        fixture.Clock.UtcNow += TimeSpan.FromMinutes(20);
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(fixture.Client.Count, Is.EqualTo(1));
        Assert.That(service.Current.IsUnavailable, Is.True);
        fixture.Clock.UtcNow += TimeSpan.FromMinutes(10);
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(fixture.Client.Count, Is.EqualTo(2));
    }

    [TestCase("null", false)]
    [TestCase("{\"scheduled_for\":null}", true)]
    public async Task NullAndUnscheduledAnnouncementAreDistinct(string scheduled, bool hasScheduled) {
        NewsFixture fixture = await NewsFixture.CreateAsync();
        using CodexResetNewsService service = fixture.Service;
        fixture.Client.Result = Response("{\"data\":{\"scheduled_reset\":" + scheduled + ",\"latest_reset\":null}}");
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(service.Current.IsUnavailable, Is.False);
        Assert.That(service.Current.HasScheduled, Is.EqualTo(hasScheduled));
        Assert.That(service.Current.ScheduledAtUtc, Is.Null);
        Assert.That(service.Current.LatestAtUtc, Is.Null);
    }

    [Test]
    public async Task MalformedScheduleShowsUnavailable() {
        NewsFixture fixture = await NewsFixture.CreateAsync();
        using CodexResetNewsService service = fixture.Service;
        fixture.Client.Result = Response("""{"data":{"scheduled_reset":{"scheduled_for":"not-a-date"}}}""");
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(service.Current.IsUnavailable, Is.True);
    }

    [Test]
    public async Task DisableCancelsRequestAndDiscardsLateResponse() {
        NewsFixture fixture = await NewsFixture.CreateAsync();
        using CodexResetNewsService service = fixture.Service;
        fixture.Client.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task request = service.RefreshAsync(CancellationToken.None);
        await fixture.Settings.SaveAsync(fixture.Settings.Settings! with { IsCodexResetNewsEnabled = false }, CancellationToken.None);
        Assert.That(fixture.Client.Token.IsCancellationRequested, Is.True);
        fixture.Client.Pending.SetResult(Response("""{"data":{"scheduled_reset":{"scheduled_for":null},"latest_reset":null}}"""));
        await request;
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(fixture.Client.Count, Is.EqualTo(1));
        Assert.That(service.Current.HasScheduled, Is.False);
    }

    [Test]
    public async Task DisabledSettingAndConcurrentRefreshDoNotStartExtraRequests() {
        NewsFixture fixture = await NewsFixture.CreateAsync();
        using CodexResetNewsService service = fixture.Service;
        await fixture.Settings.SaveAsync(fixture.Settings.Settings! with { IsCodexResetNewsEnabled = false }, CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(fixture.Client.Count, Is.Zero);
        await fixture.Settings.SaveAsync(fixture.Settings.Settings! with { IsCodexResetNewsEnabled = true }, CancellationToken.None);
        fixture.Client.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = service.RefreshAsync(CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(fixture.Client.Count, Is.EqualTo(1));
        fixture.Client.Pending.SetResult(Response("""{"data":{"scheduled_reset":null,"latest_reset":null}}"""));
        await first;
    }

    [Test]
    public void OldSettingsDefaultToEnabled() {
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(
            """{"schemaVersion":1,"quotaRefreshIntervalMinutes":5,"updateFeedUrl":null}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.That(settings?.IsCodexResetNewsEnabled, Is.True);
    }

    private static ICodexResetNewsClient.Result Response(string json) {
        using JsonDocument document = JsonDocument.Parse(json);
        return new(document.RootElement.Clone(), false, null);
    }

    private sealed class NewsFixture {
        private NewsFixture(SettingsService settings) {
            Settings = settings;
            Service = new CodexResetNewsService(settings, Client, Clock);
        }

        public FakeClock Clock { get; } = new(DateTimeOffset.Parse("2026-10-28T06:00:00Z"));
        public FakeNewsClient Client { get; } = new();
        public SettingsService Settings { get; }
        public CodexResetNewsService Service { get; }

        public static async Task<NewsFixture> CreateAsync() {
            FakeSettingsRepository repository = new(AppSettings.Defaults);
            SettingsService settings = new(repository, new FakeAccountCatalogRepository());
            await settings.InitializeAsync(CancellationToken.None);
            return new NewsFixture(settings);
        }
    }

    private sealed class FakeNewsClient : ICodexResetNewsClient {
        public int Count { get; private set; }
        public CancellationToken Token { get; private set; }
        public ICodexResetNewsClient.Result Result { get; set; } = new(null, false, null);
        public TaskCompletionSource<ICodexResetNewsClient.Result>? Pending { get; set; }

        public Task<ICodexResetNewsClient.Result> GetAsync(CancellationToken cancellationToken) {
            Count++;
            Token = cancellationToken;
            return Pending?.Task ?? Task.FromResult(Result);
        }
    }
}
