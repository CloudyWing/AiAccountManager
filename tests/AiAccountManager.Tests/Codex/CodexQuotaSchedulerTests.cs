using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Settings;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class CodexQuotaSchedulerTests {
    [TestCase(false)]
    [TestCase(true)]
    public async Task StopDuringRefresh_StartupOrPeriodic_CompletesNormally(bool periodic) {
        QuotaTestContext context = new(includeAccount: false);
        SettingsService settings = new(context.SettingsRepository, context.Catalog);
        await settings.InitializeAsync(CancellationToken.None);
        await settings.SaveAsync(AppSettings.Defaults with { QuotaRefreshIntervalMinutes = 1 }, CancellationToken.None);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        context.Catalog.BeforeLoadAsync = async token => {
            if (Interlocked.Increment(ref calls) == (periodic ? 2 : 1)) {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        using CodexQuotaScheduler scheduler = new(settings, context.Catalog, context.Service);

        await scheduler.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(periodic ? 75 : 5));
        await scheduler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(calls, Is.EqualTo(periodic ? 2 : 1));
    }

    [Test]
    public async Task StartupRefresh_SettingsChange_DoesNotRefreshAgain() {
        QuotaTestContext context = new();
        SettingsService settings = new(context.SettingsRepository, context.Catalog);
        await settings.InitializeAsync(CancellationToken.None);
        using CodexQuotaScheduler scheduler = new(settings, context.Catalog, context.Service);

        await scheduler.StartAsync();
        await WaitUntilAsync(() => scheduler.CurrentIntervalMinutes == 5);
        bool saved = await settings.SaveAsync(
            AppSettings.Defaults with { QuotaRefreshIntervalMinutes = 1 }, CancellationToken.None
        );
        await WaitUntilAsync(() => scheduler.CurrentIntervalMinutes == 1);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.That(saved, Is.True);
        Assert.That(context.Factory.CreateCount, Is.EqualTo(1));
    }

    [Test]
    public async Task StartupRefresh_WhenPaused_DoesNotRefresh() {
        QuotaTestContext context = new();
        SettingsService settings = new(context.SettingsRepository, context.Catalog);
        await settings.InitializeAsync(CancellationToken.None);
        using CodexQuotaScheduler scheduler = new(settings, context.Catalog, context.Service);
        using IDisposable pause = scheduler.Pause();

        await scheduler.StartAsync();
        await WaitUntilAsync(() => scheduler.CurrentIntervalMinutes == 5);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.That(context.Factory.CreateCount, Is.Zero);
    }

    [Test]
    public async Task SettingsChangeReplacesActiveRefreshInterval() {
        QuotaTestContext context = new();
        SettingsService settings = new(context.SettingsRepository, context.Catalog);
        await settings.InitializeAsync(CancellationToken.None);
        using CodexQuotaScheduler scheduler = new(settings, context.Catalog, context.Service);
        await scheduler.StartAsync();

        await WaitUntilAsync(() => scheduler.CurrentIntervalMinutes == 5);
        bool saved = await settings.SaveAsync(
            AppSettings.Defaults with { QuotaRefreshIntervalMinutes = 1 },
            CancellationToken.None
        );

        Assert.That(saved, Is.True);
        await WaitUntilAsync(() => scheduler.CurrentIntervalMinutes == 1);

        await scheduler.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task PauseLeasePreventsSchedulerTicksUntilReleased() {
        QuotaTestContext context = new();
        SettingsService settings = new(context.SettingsRepository, context.Catalog);
        await settings.InitializeAsync(CancellationToken.None);
        using CodexQuotaScheduler scheduler = new(settings, context.Catalog, context.Service);

        IDisposable pause = scheduler.Pause();
        Assert.That(scheduler.IsPaused, Is.True);

        pause.Dispose();

        Assert.That(scheduler.IsPaused, Is.False);
    }

    private static async Task WaitUntilAsync(Func<bool> condition) {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition()) {
            if (DateTimeOffset.UtcNow >= deadline) {
                throw new AssertionException("The quota scheduler did not apply the expected interval.");
            }

            await Task.Delay(10);
        }
    }
}
