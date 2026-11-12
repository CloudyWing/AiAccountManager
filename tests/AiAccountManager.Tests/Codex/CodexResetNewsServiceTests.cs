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

    [Test]
    public async Task ActiveWatchIsParsedFromActualResponse() {
        NewsFixture fixture = await NewsFixture.CreateAsync();
        using CodexResetNewsService service = fixture.Service;
        fixture.Client.Result = Response("""
            {"data":{"latest_reset":{"id":"2117035794678546764","reset_type":"banked","announced_at":"2026-11-01T23:27:20.000Z","text":"Day 28/ Banked 2/2","source":{"type":"x_post","author":"thsottiaux","url":"https://x.com/thsottiaux/status/2117035794678546764"}},"scheduled_reset":null,"active_watch":{"level":"elevated","reset_chance_percent":65,"forecast_window":"by end of Thursday","observed_at":"2026-11-11T06:57:08.000Z","expires_at":"2026-11-13T08:00:00.000Z","text":"Thursday is feeling codexy to me.","source":{"type":"x_post","author":"thsottiaux","url":"https://x.com/thsottiaux/status/2120410480204525936"}},"stats":{"total":71,"last_reset_at":"2026-11-01T23:27:20.000Z","days_since_last":10.6,"avg_interval_days":5.9}},"meta":{"api_version":"v1","generated_at":"2026-11-12T13:45:55.527Z"}}
            """);
        await service.RefreshAsync(CancellationToken.None);
        CodexResetNewsService.State state = service.Current;
        Assert.That(state.IsUnavailable, Is.False);
        Assert.That(state.WatchLevel, Is.EqualTo("elevated"));
        Assert.That(state.WatchChancePercent, Is.EqualTo(65));
        Assert.That(state.WatchForecastWindow, Is.EqualTo("by end of Thursday"));
        Assert.That(state.WatchExpiresAtUtc, Is.EqualTo(DateTimeOffset.Parse("2026-11-13T08:00:00Z")));
        Assert.That(state.WatchUrl?.AbsoluteUri, Is.EqualTo("https://x.com/thsottiaux/status/2120410480204525936"));
        Assert.That(state.AverageDays, Is.EqualTo(5.9));
        Assert.That(state.LatestAtUtc, Is.EqualTo(DateTimeOffset.Parse("2026-11-01T23:27:20Z")));
    }

    [Test]
    public async Task NullOrMissingActiveWatchLeavesWatchFieldsNull() {
        NewsFixture fixture = await NewsFixture.CreateAsync();
        using CodexResetNewsService service = fixture.Service;
        fixture.Client.Result = Response("""{"data":{"scheduled_reset":null,"latest_reset":null,"active_watch":null}}""");
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(service.Current.IsUnavailable, Is.False);
        AssertNoWatch(service.Current);

        fixture.Clock.UtcNow += TimeSpan.FromMinutes(15);
        fixture.Client.Result = Response("""{"data":{"scheduled_reset":null,"latest_reset":null}}""");
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(service.Current.IsUnavailable, Is.False);
        AssertNoWatch(service.Current);
    }

    [TestCase("\"text\"")]
    [TestCase("[1,2]")]
    [TestCase("{\"level\":\"elevated\",\"reset_chance_percent\":65,\"expires_at\":\"not-a-date\"}")]
    [TestCase("{\"level\":\"elevated\",\"reset_chance_percent\":65,\"expires_at\":\"2026-11-13T08:00:00+08:00\"}")]
    [TestCase("{\"forecast_window\":\"by end of Thursday\"}")]
    [TestCase("{\"level\":\"elevated\",\"reset_chance_percent\":65}")]
    [TestCase("{\"level\":\"elevated\",\"reset_chance_percent\":65,\"expires_at\":null}")]
    public async Task MalformedActiveWatchIsIgnoredWithoutMakingStateUnavailable(string activeWatch) {
        NewsFixture fixture = await NewsFixture.CreateAsync();
        using CodexResetNewsService service = fixture.Service;
        fixture.Client.Result = Response("""
            {"data":{"scheduled_reset":{"scheduled_for":"2026-10-28T06:29:38Z"},"latest_reset":null,"stats":{"avg_interval_days":14.5},"active_watch":
            """ + activeWatch + "}}");
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(service.Current.IsUnavailable, Is.False);
        Assert.That(service.Current.HasScheduled, Is.True);
        Assert.That(service.Current.AverageDays, Is.EqualTo(14.5));
        AssertNoWatch(service.Current);
    }

    [TestCase("-1")]
    [TestCase("101")]
    [TestCase("65.5")]
    [TestCase("\"65\"")]
    public async Task OutOfRangeChanceBecomesNullButLevelIsKept(string chance) {
        NewsFixture fixture = await NewsFixture.CreateAsync();
        using CodexResetNewsService service = fixture.Service;
        fixture.Client.Result = Response("{\"data\":{\"scheduled_reset\":null,\"active_watch\":{\"level\":\"watch\",\"expires_at\":\"2026-11-13T08:00:00Z\",\"reset_chance_percent\":" + chance + "}}}");
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(service.Current.IsUnavailable, Is.False);
        Assert.That(service.Current.WatchChancePercent, Is.Null);
        Assert.That(service.Current.WatchLevel, Is.EqualTo("watch"));
    }

    [Test]
    public async Task NonHttpsWatchSourceUrlIsIgnored() {
        NewsFixture fixture = await NewsFixture.CreateAsync();
        using CodexResetNewsService service = fixture.Service;
        fixture.Client.Result = Response("""
            {"data":{"scheduled_reset":null,"active_watch":{"level":"elevated","reset_chance_percent":10,"expires_at":"2026-11-13T08:00:00Z","source":{"url":"http://example.test/watch"}}}}
            """);
        await service.RefreshAsync(CancellationToken.None);
        Assert.That(service.Current.WatchChancePercent, Is.EqualTo(10));
        Assert.That(service.Current.WatchUrl, Is.Null);
    }

    private static void AssertNoWatch(CodexResetNewsService.State state) {
        Assert.That(state.WatchLevel, Is.Null);
        Assert.That(state.WatchChancePercent, Is.Null);
        Assert.That(state.WatchForecastWindow, Is.Null);
        Assert.That(state.WatchExpiresAtUtc, Is.Null);
        Assert.That(state.WatchUrl, Is.Null);
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
