using System.Reflection;
using CloudyWing.AiAccountManager.Desktop;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class AppIdentityTests {
    [Test]
    public void AssemblyMetadata_MatchesAppIdentity() {
        Assembly assembly = typeof(AppIdentity).Assembly;

        Assert.Multiple(() => {
            Assert.That(assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product, Is.EqualTo(AppIdentity.DisplayName));
            Assert.That(assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title, Is.EqualTo(AppIdentity.DisplayName));
            Assert.That(assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company, Is.EqualTo(AppIdentity.Publisher));
            Assert.That(AppIdentity.Publisher, Is.EqualTo("雲翼"));
        });
    }

    [Test]
    public void IndexHtml_TitleMatchesDisplayName() {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiAccountManager.slnx"))) {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "找不到方案根目錄。");
        string html = File.ReadAllText(Path.Combine(directory!.FullName, "src", "AiAccountManager.Desktop", "wwwroot", "index.html"));

        Assert.That(html, Does.Contain($"<title>{AppIdentity.DisplayName}</title>"));
    }
}
