using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Tests.Codex;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class ClaudeQuotaSchedulerTests {
    [TestCase(false)]
    [TestCase(true)]
    public async Task StopDuringRefresh_StartupOrPeriodic_CompletesNormally(bool periodic) {
        ClaudeTestAccountRepository repository = new();
        SettingsService settings = new(new FakeSettingsRepository(AppSettings.Defaults), new FakeAccountCatalogRepository());
        await settings.InitializeAsync(CancellationToken.None);
        await settings.SaveAsync(AppSettings.Defaults with { QuotaRefreshIntervalMinutes = 1 }, CancellationToken.None);
        ClaudeQuotaService service = new(
            repository, new ClaudeTestProfileStore(), new ClaudeTestOAuthClient(), new ClaudeTestQuotaRepository(),
            ClaudeTestData.CreateOperationGate(), new ClaudeTestClock()
        );
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        repository.BeforeLoadProfilesAsync = async token => {
            if (Interlocked.Increment(ref calls) == (periodic ? 2 : 1)) {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        using ClaudeQuotaScheduler scheduler = new(settings, repository, service);

        await scheduler.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(periodic ? 75 : 5));
        await scheduler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(calls, Is.EqualTo(periodic ? 2 : 1));
    }
}
