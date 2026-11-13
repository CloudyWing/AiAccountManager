using System.IO;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Windows.Storage;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Storage;

[TestFixture]
public sealed class JsonQuotaSnapshotRepositoryTests {
    private static readonly Guid AccountId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private string rootPath = "";
    private JsonQuotaSnapshotRepository repository = null!;

    private string SnapshotFilePath => Path.Combine(rootPath, "quota", "quota-" + AccountId.ToString("N") + ".json");

    [SetUp]
    public void SetUp() {
        rootPath = Path.Combine(Path.GetTempPath(), "quota-repository-tests-" + Guid.NewGuid().ToString("N"));
        repository = new JsonQuotaSnapshotRepository(new AtomicJsonFileStore(new CurrentUserDataDirectory(rootPath)));
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(rootPath)) {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Test]
    public async Task LoadAsync_FileWithoutFreePlanField_ReadsStandardWindows() {
        WriteSnapshotFile("""
            {
              "schemaVersion": 1,
              "snapshot": {
                "accountId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                "identityFingerprint": "fingerprint",
                "profileRevision": 1,
                "lastSuccessfulSnapshot": {
                  "retrievedAtUtc": "2026-11-01T00:00:00+00:00",
                  "fiveHour": { "durationMinutes": 300, "remainingPercent": 80, "resetsAtUtc": "2026-11-01T03:00:00+00:00" },
                  "weekly": { "durationMinutes": 10080, "remainingPercent": 65, "resetsAtUtc": null },
                  "resetCredits": null
                },
                "lastAttempt": null
              }
            }
            """);

        IQuotaSnapshotRepository.Snapshot? loaded = await repository.LoadAsync(AccountId, CancellationToken.None);

        QuotaSuccessfulSnapshot? success = loaded?.Data.LastSuccessfulSnapshot;
        Assert.Multiple(() => {
            Assert.That(success?.FiveHour?.RemainingPercent, Is.EqualTo(80));
            Assert.That(success?.Weekly?.RemainingPercent, Is.EqualTo(65));
            Assert.That(success?.FreePlanWindow, Is.Null);
            Assert.That(success?.IsFreePlan, Is.False);
        });
    }

    [Test]
    public async Task SaveAndLoadAsync_FreePlanSnapshot_RoundTripsWithoutSerializingDerivedProperty() {
        DateTimeOffset reset = DateTimeOffset.FromUnixTimeSeconds(1796893535);
        QuotaSnapshotRecord record = new(
            AccountId,
            "fingerprint",
            1,
            new QuotaSuccessfulSnapshot(
                DateTimeOffset.Parse("2026-11-01T00:00:00Z"),
                null,
                null,
                new ResetCreditSummary(0, []),
                new QuotaWindow(43200, 99, reset)
            ),
            null
        );

        IQuotaSnapshotRepository.SaveResult saved = await repository.SaveAsync(record, null, CancellationToken.None);
        IQuotaSnapshotRepository.Snapshot? loaded = await repository.LoadAsync(AccountId, CancellationToken.None);

        string json = await File.ReadAllTextAsync(SnapshotFilePath);
        Assert.Multiple(() => {
            Assert.That(saved.Saved, Is.True);
            Assert.That(loaded?.Data.LastSuccessfulSnapshot?.FreePlanWindow, Is.EqualTo(new QuotaWindow(43200, 99, reset)));
            Assert.That(loaded?.Data.LastSuccessfulSnapshot?.IsFreePlan, Is.True);
            Assert.That(json, Does.Not.Contain("isFreePlan"));
        });
    }

    [TestCase(300, 43200)]
    [TestCase(null, 300)]
    public void SaveAsync_InconsistentFreePlanWindows_Throws(int? fiveHourDuration, int freeDuration) {
        QuotaSnapshotRecord record = new(
            AccountId,
            "fingerprint",
            1,
            new QuotaSuccessfulSnapshot(
                DateTimeOffset.Parse("2026-11-01T00:00:00Z"),
                fiveHourDuration is int duration ? new QuotaWindow(duration, 50, null) : null,
                null,
                null,
                new QuotaWindow(freeDuration, 99, null)
            ),
            null
        );

        Assert.ThrowsAsync<ArgumentException>(
            async () => await repository.SaveAsync(record, null, CancellationToken.None)
        );
    }

    private void WriteSnapshotFile(string json) {
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotFilePath)!);
        File.WriteAllText(SnapshotFilePath, json);
    }
}
