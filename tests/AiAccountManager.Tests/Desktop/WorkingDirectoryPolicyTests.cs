using System.IO;
using CloudyWing.AiAccountManager.Desktop;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class WorkingDirectoryPolicyTests {
    private string root = "";
    private string applicationDirectory = "";
    private string fallbackDirectory = "";

    [SetUp]
    public void SetUp() {
        root = Path.Combine(Path.GetTempPath(), "WorkingDirectoryPolicyTests-" + Guid.NewGuid().ToString("N"));
        applicationDirectory = Path.Combine(root, "app", "current");
        fallbackDirectory = Path.Combine(root, "profile");
        Directory.CreateDirectory(fallbackDirectory);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void ResolveReplacement_CurrentEqualsApplication_ReturnsFallback() {
        string? result = WorkingDirectoryPolicy.ResolveReplacement(
            applicationDirectory,
            applicationDirectory,
            fallbackDirectory
        );

        Assert.That(result, Is.EqualTo(fallbackDirectory));
    }

    [Test]
    public void ResolveReplacement_CurrentInsideApplicationSubdirectory_ReturnsFallback() {
        string current = Path.Combine(applicationDirectory, "resources", "cache");

        string? result = WorkingDirectoryPolicy.ResolveReplacement(current, applicationDirectory, fallbackDirectory);

        Assert.That(result, Is.EqualTo(fallbackDirectory));
    }

    [Test]
    public void ResolveReplacement_CurrentOutsideApplication_ReturnsNull() {
        string current = Path.Combine(root, "elsewhere");

        string? result = WorkingDirectoryPolicy.ResolveReplacement(current, applicationDirectory, fallbackDirectory);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolveReplacement_CurrentIsSiblingWithSamePrefix_ReturnsNull() {
        string current = applicationDirectory + "2";

        string? result = WorkingDirectoryPolicy.ResolveReplacement(current, applicationDirectory, fallbackDirectory);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolveReplacement_DifferentCaseAndTrailingSeparator_ReturnsFallback() {
        string current = applicationDirectory.ToUpperInvariant() + Path.DirectorySeparatorChar;
        string application = applicationDirectory.ToLowerInvariant();

        string? result = WorkingDirectoryPolicy.ResolveReplacement(current, application, fallbackDirectory);

        Assert.That(result, Is.EqualTo(fallbackDirectory));
    }

    [Test]
    public void ResolveReplacement_ApplicationWithTrailingSeparator_ReturnsFallback() {
        string application = applicationDirectory + Path.DirectorySeparatorChar;

        string? result = WorkingDirectoryPolicy.ResolveReplacement(
            applicationDirectory,
            application,
            fallbackDirectory
        );

        Assert.That(result, Is.EqualTo(fallbackDirectory));
    }

    [Test]
    public void ResolveReplacement_FallbackDoesNotExist_ReturnsNull() {
        string missing = Path.Combine(root, "missing");

        string? result = WorkingDirectoryPolicy.ResolveReplacement(applicationDirectory, applicationDirectory, missing);

        Assert.That(result, Is.Null);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void ResolveReplacement_FallbackIsNullOrBlank_ReturnsNull(string? fallback) {
        string? result = WorkingDirectoryPolicy.ResolveReplacement(
            applicationDirectory,
            applicationDirectory,
            fallback
        );

        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolveReplacement_ApplicationIsDriveRoot_ReturnsFallback() {
        string driveRoot = Path.GetPathRoot(root)!;

        string? result = WorkingDirectoryPolicy.ResolveReplacement(root, driveRoot, fallbackDirectory);

        Assert.That(result, Is.EqualTo(fallbackDirectory));
    }

    [Test]
    public void ResolveReplacement_CurrentWithParentSegmentLeavingApplication_ReturnsNull() {
        string current = Path.Combine(applicationDirectory, "..", "current2");

        string? result = WorkingDirectoryPolicy.ResolveReplacement(
            current,
            applicationDirectory,
            fallbackDirectory
        );

        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolveReplacement_CurrentIsNull_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(
            () => WorkingDirectoryPolicy.ResolveReplacement(null!, applicationDirectory, fallbackDirectory)
        );
    }

    [Test]
    public void ResolveReplacement_ApplicationIsNull_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(
            () => WorkingDirectoryPolicy.ResolveReplacement(applicationDirectory, null!, fallbackDirectory)
        );
    }
}
