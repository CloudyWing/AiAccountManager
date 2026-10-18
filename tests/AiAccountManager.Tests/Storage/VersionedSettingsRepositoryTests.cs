using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Windows.Storage;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Storage;

[TestFixture]
public sealed class VersionedSettingsRepositoryTests {
    private string? dataRoot;
    private JsonSettingsRepository? repository;
    private JsonAccountCatalogRepository? catalogRepository;

    private string DataRoot => dataRoot ?? throw new InvalidOperationException("Test data directory is not initialized.");

    private JsonSettingsRepository Repository
        => repository ?? throw new InvalidOperationException("Settings repository is not initialized.");

    private JsonAccountCatalogRepository CatalogRepository
        => catalogRepository ?? throw new InvalidOperationException("Account catalog repository is not initialized.");

    [SetUp]
    public void SetUp() {
        dataRoot = Path.Combine(Path.GetTempPath(), "AiAccountManager.Tests", Guid.NewGuid().ToString("N"));
        CurrentUserDataDirectory directory = new(DataRoot);
        repository = new JsonSettingsRepository(new AtomicJsonFileStore(directory));
        catalogRepository = new JsonAccountCatalogRepository(new AtomicJsonFileStore(directory));
    }

    [TearDown]
    public void TearDown() {
        if (dataRoot is not null && Directory.Exists(dataRoot)) {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Test]
    public async Task FirstRunCreatesVersionedDefaults() {
        ISettingsRepository.Snapshot snapshot = await Repository.LoadAsync(CancellationToken.None);

        Assert.That(snapshot.Settings, Is.EqualTo(AppSettings.Defaults));
        Assert.That(File.Exists(Path.Combine(DataRoot, "settings.json")), Is.True);
        Assert.That(snapshot.Sha256, Is.Not.Empty);
    }

    [Test]
    public async Task LoadAsync_MapsMissingThemeModeToSystemAndRoundTripsSelection() {
        await File.WriteAllTextAsync(
            Path.Combine(DataRoot, "settings.json"),
            """{"schemaVersion":1,"quotaRefreshIntervalMinutes":5,"updateFeedUrl":null}"""
        );

        ISettingsRepository.Snapshot legacy = await Repository.LoadAsync(CancellationToken.None);
        ISettingsRepository.SaveResult saved = await Repository.SaveAsync(
            legacy.Settings with { ThemeMode = AppThemeMode.Dark },
            legacy.Sha256,
            CancellationToken.None
        );
        ISettingsRepository.Snapshot reloaded = await Repository.LoadAsync(CancellationToken.None);
        string persisted = await File.ReadAllTextAsync(Path.Combine(DataRoot, "settings.json"));

        Assert.That(legacy.Settings.ThemeMode, Is.EqualTo(AppThemeMode.System));
        Assert.That(legacy.Settings.MinimizeToTray, Is.False);
        Assert.That(legacy.Settings.StartWithWindows, Is.False);
        Assert.That(legacy.Settings.LowQuotaNotificationsEnabled, Is.True);
        Assert.That(legacy.Settings.LowQuotaThresholdPercent, Is.EqualTo(20));
        Assert.That(legacy.Settings.IsCodexResetNewsEnabled, Is.True);
        Assert.That(legacy.Settings.AutoUseResetCredits, Is.False);
        Assert.That(legacy.Settings.AutoUseResetCreditsMinutes, Is.EqualTo(30));
        Assert.That(saved.Saved, Is.True);
        Assert.That(reloaded.Settings.ThemeMode, Is.EqualTo(AppThemeMode.Dark));
        Assert.That(persisted, Does.Contain("\"Dark\""));
    }

    [TestCase(4)]
    [TestCase(51)]
    public async Task SaveAsync_InvalidNotificationThreshold_PreservesOriginalFile(int threshold) {
        ISettingsRepository.Snapshot initial = await Repository.LoadAsync(CancellationToken.None);
        string path = Path.Combine(DataRoot, "settings.json");
        byte[] before = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<ArgumentException>(async () => await Repository.SaveAsync(
            initial.Settings with { LowQuotaThresholdPercent = threshold }, initial.Sha256, CancellationToken.None
        ));

        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(before));
    }

    [Test]
    public async Task FirstRunCreatesVersionedEmptyCatalog() {
        IAccountCatalogRepository.Snapshot snapshot = await CatalogRepository.LoadAsync(CancellationToken.None);

        Assert.That(snapshot.Accounts, Is.Empty);
        Assert.That(snapshot.Sha256, Is.Not.Empty);
        Assert.That(File.Exists(Path.Combine(DataRoot, "catalog.json")), Is.True);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(DataRoot, "catalog.json")));
        Assert.That(document.RootElement.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
        Assert.That(document.RootElement.GetProperty("accounts").GetArrayLength(), Is.Zero);
    }

    [Test]
    public async Task MalformedSettingsArePreserved() {
        string path = Path.Combine(DataRoot, "settings.json");
        Directory.CreateDirectory(DataRoot);
        byte[] malformed = "{not-json"u8.ToArray();
        await File.WriteAllBytesAsync(path, malformed);

        await Assert.ThrowsAsync<JsonException>(async () => await Repository.LoadAsync(CancellationToken.None));
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(malformed));
    }

    [Test]
    public async Task UnknownSchemaIsPreserved() {
        string path = Path.Combine(DataRoot, "settings.json");
        Directory.CreateDirectory(DataRoot);
        byte[] unknown = "{\"schemaVersion\":99,\"quotaRefreshIntervalMinutes\":5,\"updateFeedUrl\":null}"u8.ToArray();
        await File.WriteAllBytesAsync(path, unknown);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Repository.LoadAsync(CancellationToken.None));
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(unknown));
    }

    [Test]
    public async Task MalformedCatalogIsPreserved() {
        string path = Path.Combine(DataRoot, "catalog.json");
        Directory.CreateDirectory(DataRoot);
        byte[] malformed = "{not-json"u8.ToArray();
        await File.WriteAllBytesAsync(path, malformed);

        await Assert.ThrowsAsync<JsonException>(async () => await CatalogRepository.LoadAsync(CancellationToken.None));
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(malformed));
    }

    [Test]
    public async Task SaveWithChangedHashReturnsConflict() {
        ISettingsRepository.Snapshot snapshot = await Repository.LoadAsync(CancellationToken.None);
        string path = Path.Combine(DataRoot, "settings.json");
        byte[] external = JsonSerializer.SerializeToUtf8Bytes(AppSettings.Defaults with {
            QuotaRefreshIntervalMinutes = 15
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllBytesAsync(path, external);

        ISettingsRepository.SaveResult result = await Repository.SaveAsync(
            AppSettings.Defaults with { QuotaRefreshIntervalMinutes = 30 },
            snapshot.Sha256,
            CancellationToken.None
        );

        Assert.That(result.Saved, Is.False);
        Assert.That(result.Conflict, Is.True);
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(external));
    }

    [Test]
    public async Task SaveWithChangedCatalogHashReturnsConflictAndPreservesExternalCatalog() {
        IAccountCatalogRepository.Snapshot snapshot = await CatalogRepository.LoadAsync(CancellationToken.None);
        string path = Path.Combine(DataRoot, "catalog.json");
        CodexAccountProfile externalAccount = new(
            Guid.NewGuid(),
            null,
            "a•••@example.test",
            "FINGERPRINT",
            1,
            DateTimeOffset.UtcNow,
            "connected"
        );
        byte[] external = JsonSerializer.SerializeToUtf8Bytes(
            new { schemaVersion = 1, accounts = new[] { externalAccount } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        await File.WriteAllBytesAsync(path, external);

        IAccountCatalogRepository.SaveResult result = await CatalogRepository.SaveAsync(
            [],
            snapshot.Sha256,
            CancellationToken.None
        );

        Assert.That(result.Saved, Is.False);
        Assert.That(result.Conflict, Is.True);
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(external));
    }

    [TestCase("http://updates.example.test/feed", false)]
    [TestCase("https:relative", false)]
    [TestCase("https://user:password@updates.example.test/feed", false)]
    [TestCase("https://updates.example.test/feed", true)]
    public void UpdateFeedMustBeAnAbsoluteHttpsUriWithoutCredentials(string? feedUrl, bool expectedValid) {
        bool valid = (AppSettings.Defaults with { UpdateFeedUrl = feedUrl }).IsValid(out _);

        Assert.That(valid, Is.EqualTo(expectedValid));
    }
}
