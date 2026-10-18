using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Tests.Codex;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class StartupRegistrationTests {
    [Test]
    public async Task SaveAsync_Conflict_RestoresPreviousStartupValue() {
        ConflictingSettingsRepository repository = new();
        FakeStartupRegistration startup = new() { Value = "previous command" };
        SettingsService service = new(repository, new FakeAccountCatalogRepository(), startup);
        await service.InitializeAsync(CancellationToken.None);

        bool saved = await service.SaveAsync(AppSettings.Defaults with { StartWithWindows = true }, CancellationToken.None);

        Assert.That(saved, Is.False);
        Assert.That(startup.Value, Is.EqualTo("previous command"));
        Assert.That(service.IsStartupRegistered, Is.True);
        Assert.That(service.SettingsError, Does.Contain("其他位置更新"));
    }

    [Test]
    public async Task SaveAsync_StartupWriteFails_DoesNotSaveSettings() {
        ConflictingSettingsRepository repository = new();
        FakeStartupRegistration startup = new() { FailSet = true };
        SettingsService service = new(repository, new FakeAccountCatalogRepository(), startup);
        await service.InitializeAsync(CancellationToken.None);

        bool saved = await service.SaveAsync(AppSettings.Defaults with { StartWithWindows = true }, CancellationToken.None);

        Assert.That(saved, Is.False);
        Assert.That(repository.SaveCount, Is.Zero);
        Assert.That(service.StartupError, Is.Not.Null);
    }

    [Test]
    public async Task SaveAsync_RollbackFails_ReportsActualStartupState() {
        ConflictingSettingsRepository repository = new();
        FakeStartupRegistration startup = new() { FailRestore = true };
        SettingsService service = new(repository, new FakeAccountCatalogRepository(), startup);
        await service.InitializeAsync(CancellationToken.None);

        bool saved = await service.SaveAsync(AppSettings.Defaults with { StartWithWindows = true }, CancellationToken.None);

        Assert.That(saved, Is.False);
        Assert.That(service.StartupError, Does.Contain("無法還原"));
        Assert.That(service.IsStartupRegistered, Is.True);
    }

    private sealed class ConflictingSettingsRepository : ISettingsRepository {
        public int SaveCount { get; private set; }

        public Task<ISettingsRepository.Snapshot> LoadAsync(CancellationToken cancellationToken) {
            return Task.FromResult(new ISettingsRepository.Snapshot(AppSettings.Defaults, "baseline"));
        }

        public Task<ISettingsRepository.SaveResult> SaveAsync(AppSettings settings, string expectedSha256, CancellationToken cancellationToken) {
            SaveCount++;
            return Task.FromResult(new ISettingsRepository.SaveResult(false, null, true));
        }
    }

    private sealed class FakeStartupRegistration : SettingsService.IStartupRegistration {
        public string? Value { get; set; }

        public bool FailSet { get; init; }

        public bool FailRestore { get; init; }

        public string? Read() => Value;

        public void SetEnabled(bool enabled) {
            if (FailSet) {
                throw new InvalidOperationException("synthetic failure");
            }
            Value = enabled ? "new command" : null;
        }

        public void Restore(string? value) {
            if (FailRestore) {
                throw new InvalidOperationException("synthetic rollback failure");
            }
            Value = value;
        }
    }
}
