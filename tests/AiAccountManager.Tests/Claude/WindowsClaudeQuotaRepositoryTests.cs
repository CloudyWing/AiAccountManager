using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Windows.Claude;
using CloudyWing.AiAccountManager.Windows.Storage;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class WindowsClaudeQuotaRepositoryTests {
    [Test]
    public async Task SaveAndLoadAsync_StoresQuotaViewsByProfileId() {
        using WindowsClaudeTestWorkspace workspace = new();
        string appDataPath = Path.Combine(workspace.RootPath, "application-data");
        CurrentUserDataDirectory dataDirectory = new(appDataPath);
        JsonClaudeQuotaRepository repository = new(new AtomicJsonFileStore(dataDirectory));
        Guid firstProfileId = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        Guid secondProfileId = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        ClaudeQuotaSnapshot.View firstView = CreateFreshView(firstProfileId, 25m);
        ClaudeQuotaSnapshot.View secondView = CreateFreshView(secondProfileId, 63m);

        IClaudeQuotaRepository.SaveResult firstSave = await repository.SaveAsync(
            firstView,
            expectedSha256: null,
            CancellationToken.None
        );
        IClaudeQuotaRepository.SaveResult secondSave = await repository.SaveAsync(
            secondView,
            expectedSha256: null,
            CancellationToken.None
        );
        IClaudeQuotaRepository.Snapshot? firstLoaded = await repository.LoadAsync(firstProfileId, CancellationToken.None);
        IClaudeQuotaRepository.Snapshot? secondLoaded = await repository.LoadAsync(secondProfileId, CancellationToken.None);

        Assert.That(firstSave.Saved, Is.True);
        Assert.That(secondSave.Saved, Is.True);
        Assert.That(firstLoaded!.View.Snapshot!.Windows[0].UtilizationPercent, Is.EqualTo(25m));
        Assert.That(secondLoaded!.View.Snapshot!.Windows[0].UtilizationPercent, Is.EqualTo(63m));
        Assert.That(await repository.LoadAsync(Guid.NewGuid(), CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task SaveAsync_ReturnsConflictWhenExpectedProfileHashIsStale() {
        using WindowsClaudeTestWorkspace workspace = new();
        string appDataPath = Path.Combine(workspace.RootPath, "application-data");
        CurrentUserDataDirectory dataDirectory = new(appDataPath);
        JsonClaudeQuotaRepository repository = new(new AtomicJsonFileStore(dataDirectory));
        Guid profileId = new("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        ClaudeQuotaSnapshot.View first = CreateFreshView(profileId, 10m);
        ClaudeQuotaSnapshot.View second = CreateFreshView(profileId, 20m);
        IClaudeQuotaRepository.SaveResult initial = await repository.SaveAsync(
            first,
            expectedSha256: null,
            CancellationToken.None
        );

        IClaudeQuotaRepository.SaveResult conflict = await repository.SaveAsync(
            second,
            new string('a', 64),
            CancellationToken.None
        );
        IClaudeQuotaRepository.Snapshot? stored = await repository.LoadAsync(profileId, CancellationToken.None);

        Assert.That(initial.Saved, Is.True);
        Assert.That(conflict.Saved, Is.False);
        Assert.That(conflict.Conflict, Is.True);
        Assert.That(stored!.View.Snapshot!.Windows[0].UtilizationPercent, Is.EqualTo(10m));
    }

    private static ClaudeQuotaSnapshot.View CreateFreshView(Guid profileId, decimal utilization) {
        ClaudeQuotaSnapshot snapshot = new(
            profileId,
            ClaudeTestData.Now,
            [new ClaudeQuotaSnapshot.Window("session", 300, utilization, ClaudeTestData.Now.AddHours(3))]
        );
        return new ClaudeQuotaSnapshot.View(
            profileId,
            snapshot,
            ClaudeQuotaSnapshot.RefreshStatus.Fresh,
            ClaudeTestData.Now,
            null
        );
    }
}
