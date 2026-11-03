using CloudyWing.AiAccountManager.Desktop;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class ReleaseMetadataConsistencyTests {
    [Test]
    public void ReleaseMetadata_MatchesAppIdentity() {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiAccountManager.slnx"))) {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "找不到方案根目錄。");
        string root = directory!.FullName;
        string script = File.ReadAllText(Path.Combine(root, "scripts", "package-windows.ps1"));
        string zh = File.ReadAllText(Path.Combine(root, "packaging", "winget", "CloudyWing.AiAccountManager.locale.zh-TW.yaml"));
        string en = File.ReadAllText(Path.Combine(root, "packaging", "winget", "CloudyWing.AiAccountManager.locale.en-US.yaml"));
        string installer = File.ReadAllText(Path.Combine(root, "packaging", "winget", "CloudyWing.AiAccountManager.installer.yaml"));

        Assert.Multiple(() => {
            Assert.That(script, Does.Contain($"'--packTitle', '{AppIdentity.DisplayName}'"));
            Assert.That(script, Does.Contain($"'--packAuthors', '{AppIdentity.Publisher}'"));
            Assert.That(zh, Does.Contain($"PackageName: {AppIdentity.DisplayName}"));
            Assert.That(zh, Does.Contain($"Publisher: {AppIdentity.Publisher}"));
            Assert.That(installer, Does.Contain($"DisplayName: {AppIdentity.DisplayName}"));
            Assert.That(installer, Does.Contain($"Publisher: {AppIdentity.Publisher}"));
            Assert.That(en, Does.Contain("PackageName: AI Account Manager"));
            Assert.That(en, Does.Contain("Publisher: CloudyWing"));
        });
    }
}
