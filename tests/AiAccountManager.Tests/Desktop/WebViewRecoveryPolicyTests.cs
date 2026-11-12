using System.IO;
using System.Runtime.InteropServices;
using CloudyWing.AiAccountManager.Desktop;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class WebViewRecoveryPolicyTests {
    private const string GraphicsItem = "Microsoft.Web.WebView2.Wpf.GraphicsItemD3DImage";
    private const string Direct3DHelper = "Microsoft.Web.WebView2.Wpf.Direct3DHelper";
    private const string Marshal = "System.Runtime.InteropServices.Marshal";

    private DateTimeOffset now;
    private string tempDirectory = "";

    [SetUp]
    public void SetUp() {
        now = DateTimeOffset.Parse("2026-11-12T10:00:00Z");
        tempDirectory = Path.Combine(Path.GetTempPath(), "webview-recovery-tests-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(tempDirectory)) {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    // 重建排程

    [Test]
    public void Trigger_SchedulesDebounceDelayAndLogsReason() {
        SchedulerHarness harness = new(() => now);

        harness.Scheduler.Trigger("session-unlock");

        Assert.That(harness.Delays, Is.EqualTo(new[] { WebViewRebuildScheduler.DebounceDelay }));
        Assert.That(harness.Logs, Is.EqualTo(new[] { "trigger:session-unlock" }));
        Assert.That(harness.Scheduler.IsPending, Is.True);
        Assert.That(harness.Rebuilds, Is.Empty);
    }

    [Test]
    public void OnTick_DebouncesFromLastTrigger() {
        SchedulerHarness harness = new(() => now);
        harness.Scheduler.Trigger("first");
        now += TimeSpan.FromSeconds(2);
        harness.Scheduler.Trigger("second");

        now += TimeSpan.FromSeconds(1);
        harness.Scheduler.OnTick();

        Assert.That(harness.Rebuilds, Is.Empty);
        Assert.That(harness.Delays[^1], Is.EqualTo(TimeSpan.FromSeconds(2)));

        now += TimeSpan.FromSeconds(2);
        harness.Scheduler.OnTick();

        Assert.That(harness.Rebuilds, Is.EqualTo(new[] { "second" }));
        Assert.That(harness.Scheduler.IsPending, Is.False);
    }

    [Test]
    public void OnTick_DefersWhileExclusiveOperationIsActive() {
        SchedulerHarness harness = new(() => now) { HasActiveOperations = true };
        harness.Scheduler.Trigger("power-resume");
        now += WebViewRebuildScheduler.DebounceDelay;

        harness.Scheduler.OnTick();
        now += WebViewRebuildScheduler.RecheckDelay;
        harness.Scheduler.OnTick();

        Assert.That(harness.Rebuilds, Is.Empty);
        Assert.That(harness.Delays[^1], Is.EqualTo(WebViewRebuildScheduler.RecheckDelay));
        Assert.That(harness.Logs.Count(log => log == "rebuild.deferred"), Is.EqualTo(1));
        Assert.That(harness.Scheduler.IsPending, Is.True);

        harness.HasActiveOperations = false;
        now += WebViewRebuildScheduler.RecheckDelay;
        harness.Scheduler.OnTick();

        Assert.That(harness.Rebuilds, Is.EqualTo(new[] { "power-resume" }));
    }

    [Test]
    public void OnTick_WithoutPendingRequestDoesNothing() {
        SchedulerHarness harness = new(() => now);

        harness.Scheduler.OnTick();
        harness.Scheduler.Trigger("display-settings-changed");
        harness.Scheduler.Cancel();
        now += TimeSpan.FromMinutes(1);
        harness.Scheduler.OnTick();

        Assert.That(harness.Rebuilds, Is.Empty);
    }

    [Test]
    public void OnTick_RebuildFailureIsLoggedAndNotThrown() {
        SchedulerHarness harness = new(() => now) { RebuildException = new InvalidOperationException("boom") };
        harness.Scheduler.Trigger("tray-show");
        now += WebViewRebuildScheduler.DebounceDelay;

        Assert.DoesNotThrow(() => harness.Scheduler.OnTick());

        Assert.That(harness.Logs[^1], Does.StartWith("rebuild.failed:System.InvalidOperationException:0x"));
        Assert.That(harness.Scheduler.IsPending, Is.False);
    }

    // 例外過濾

    [Test]
    public void ShouldSwallow_ArgumentExceptionWithRenderFrame_Swallows() {
        WebViewRecoveryPolicy policy = CreatePolicy(_ => [GraphicsItem]);

        bool result = policy.ShouldSwallow(new ArgumentException("E_INVALIDARG"), out string reason);

        Assert.That(result, Is.True);
        Assert.That(reason, Is.EqualTo("swallowed"));
    }

    [Test]
    public void ShouldSwallow_MarshalFramesAreSkipped() {
        WebViewRecoveryPolicy policy = CreatePolicy(_ => [Marshal, Marshal, Direct3DHelper]);

        Assert.That(policy.ShouldSwallow(new COMException("device lost"), out _), Is.True);
    }

    [TestCase(GraphicsItem + "+<>c")]
    [TestCase(Direct3DHelper + "+<>c__DisplayClass3_0")]
    [TestCase(GraphicsItem + "+Nested+<>c")]
    public void ShouldSwallow_NestedTypesUseOutermostDeclaringType(string frameType) {
        WebViewRecoveryPolicy policy = CreatePolicy(_ => [Marshal, frameType]);

        Assert.That(policy.ShouldSwallow(new ArgumentException(), out _), Is.True);
    }

    [TestCase("Microsoft.Web.WebView2.Wpf.WebView2CompositionControl")]
    [TestCase("Microsoft.Web.WebView2.Wpf.GraphicsItemD3DImageFactory")]
    [TestCase("MyApp.Direct3DHelper")]
    public void ShouldSwallow_FrameFromOtherTypeIsNotSwallowed(string frameType) {
        WebViewRecoveryPolicy policy = CreatePolicy(_ => [Marshal, frameType, GraphicsItem]);

        bool result = policy.ShouldSwallow(new ArgumentException(), out string reason);

        Assert.That(result, Is.False);
        Assert.That(reason, Is.EqualTo("frame-mismatch"));
    }

    [Test]
    public void ShouldSwallow_OnlyMarshalOrEmptyFramesAreNotSwallowed() {
        Assert.That(CreatePolicy(_ => [Marshal]).ShouldSwallow(new ArgumentException(), out _), Is.False);
        Assert.That(CreatePolicy(_ => []).ShouldSwallow(new ArgumentException(), out _), Is.False);
    }

    [Test]
    public void ShouldSwallow_NullFrameTypeIsNotSwallowed() {
        WebViewRecoveryPolicy policy = CreatePolicy(_ => [Marshal, null, GraphicsItem]);

        bool result = policy.ShouldSwallow(new ArgumentException(), out string reason);

        Assert.That(result, Is.False);
        Assert.That(reason, Is.EqualTo("frame-mismatch"));
    }

    [Test]
    public void IsRenderFrameSequence_NullInSequenceIsNotMatch() {
        Assert.That(WebViewRecoveryPolicy.IsRenderFrameSequence([null, GraphicsItem]), Is.False);
        Assert.That(WebViewRecoveryPolicy.IsRenderFrameSequence([Marshal, null, Direct3DHelper]), Is.False);
        Assert.That(WebViewRecoveryPolicy.IsRenderFrameSequence([null]), Is.False);
        Assert.That(WebViewRecoveryPolicy.IsRenderFrameSequence([GraphicsItem, null]), Is.True);
    }

    [Test]
    public void IsRenderFrameSequence_UsesFirstNonMarshalFrame() {
        Assert.That(WebViewRecoveryPolicy.IsRenderFrameSequence([Marshal, Direct3DHelper, "Other"]), Is.True);
        Assert.That(WebViewRecoveryPolicy.IsRenderFrameSequence(["Other", Direct3DHelper]), Is.False);
        Assert.That(WebViewRecoveryPolicy.IsRenderFrameSequence([]), Is.False);
    }

    [Test]
    public void ShouldSwallow_InspectsTheMatchedInnerExceptionNotTheOutermost() {
        Exception? inspected = null;
        ArgumentException inner = new("inner");
        InvalidOperationException outer = new("outer", inner);
        WebViewRecoveryPolicy policy = CreatePolicy(ex => {
            inspected = ex;
            return [GraphicsItem];
        });

        bool result = policy.ShouldSwallow(outer, out _);

        Assert.That(result, Is.True);
        Assert.That(inspected, Is.SameAs(inner));
    }

    [Test]
    public void ShouldSwallow_ExceptionWithoutArgumentOrComExceptionIsNotSwallowed() {
        WebViewRecoveryPolicy policy = CreatePolicy(_ => [GraphicsItem]);

        bool result = policy.ShouldSwallow(new InvalidOperationException(), out string reason);

        Assert.That(result, Is.False);
        Assert.That(reason, Is.EqualTo("exception-type-mismatch"));
    }

    [Test]
    public void ShouldSwallow_LoadFailureAnywhereInChainIsNotSwallowed() {
        WebViewRecoveryPolicy policy = CreatePolicy(_ => [GraphicsItem]);
        Exception[] exceptions = [
            new FileNotFoundException(),
            new FileLoadException(),
            new TypeLoadException(),
            new BadImageFormatException(),
            new ArgumentException("outer", new FileNotFoundException()),
            new InvalidOperationException("outer", new ArgumentException("middle", new TypeLoadException()))
        ];

        foreach (Exception exception in exceptions) {
            Assert.That(policy.ShouldSwallow(exception, out string reason), Is.False, exception.ToString());
            Assert.That(reason, Is.EqualTo("load-failure"));
        }
    }

    [Test]
    public void ShouldSwallow_BeforeFirstSuccessfulRenderIsNotSwallowed() {
        WebViewRecoveryPolicy policy = new(() => now, _ => [GraphicsItem]);

        bool before = policy.ShouldSwallow(new ArgumentException(), out string reason);
        policy.MarkRendered();
        bool after = policy.ShouldSwallow(new ArgumentException(), out _);

        Assert.That(before, Is.False);
        Assert.That(reason, Is.EqualTo("not-rendered"));
        Assert.That(after, Is.True);
    }

    [Test]
    public void ShouldSwallow_FourthSwallowWithinWindowIsRejectedAndRecoversAfterWindow() {
        WebViewRecoveryPolicy policy = CreatePolicy(_ => [GraphicsItem]);

        for (int i = 0; i < WebViewRecoveryPolicy.MaxSwallowsInWindow; i++) {
            Assert.That(policy.ShouldSwallow(new ArgumentException(), out _), Is.True);
            now += TimeSpan.FromSeconds(10);
        }

        bool fourth = policy.ShouldSwallow(new ArgumentException(), out string reason);

        Assert.That(fourth, Is.False);
        Assert.That(reason, Is.EqualTo("rate-limit"));

        now += TimeSpan.FromSeconds(31);

        Assert.That(policy.ShouldSwallow(new ArgumentException(), out _), Is.True);
    }

    [Test]
    public void ShouldSwallow_RejectedExceptionsDoNotConsumeTheRateLimit() {
        bool validFrames = false;
        WebViewRecoveryPolicy policy = CreatePolicy(_ => validFrames ? [GraphicsItem] : ["Other"]);

        for (int i = 0; i < 10; i++) {
            Assert.That(policy.ShouldSwallow(new ArgumentException(), out _), Is.False);
        }
        validFrames = true;

        Assert.That(policy.ShouldSwallow(new ArgumentException(), out _), Is.True);
    }

    [Test]
    public void GetFrameTypeNames_ReturnsOutermostDeclaringTypeFromInnermostFrame() {
        Exception? thrown = null;
        Action action = () => throw new ArgumentException("lambda");
        try {
            action();
        } catch (ArgumentException exception) {
            thrown = exception;
        }

        IReadOnlyList<string?> names = WebViewRecoveryPolicy.GetFrameTypeNames(thrown!);

        Assert.That(names[0], Is.EqualTo(typeof(WebViewRecoveryPolicyTests).FullName));
    }

    [Test]
    public void GetFrameTypeNames_ExceptionThatWasNeverThrownHasNoFrames() {
        Assert.That(WebViewRecoveryPolicy.GetFrameTypeNames(new ArgumentException()), Is.Empty);
    }

    // 本機記錄

    [Test]
    public void Log_WritesUtcTimestampedLineUnderLogsFolder() {
        WebViewRecoveryLog log = new(tempDirectory, () => DateTimeOffset.Parse("2026-11-12T10:20:30.123+08:00"));

        log.Write("rebuild.started:debug-hotkey");
        log.Write("multi\r\nline");

        string[] lines = File.ReadAllLines(Path.Combine(tempDirectory, "logs", "webview-recovery.log"));
        Assert.That(lines, Is.EqualTo(new[] {
            "2026-11-12T02:20:30.123Z rebuild.started:debug-hotkey",
            "2026-11-12T02:20:30.123Z multi line"
        }));
    }

    [Test]
    public void Log_RotatesToSingleBackupWhenFileExceedsLimit() {
        string logsDirectory = Path.Combine(tempDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);
        string path = Path.Combine(logsDirectory, "webview-recovery.log");
        File.WriteAllBytes(path, new byte[WebViewRecoveryLog.MaxFileBytes + 1]);
        File.WriteAllText(path + ".1", "stale");
        WebViewRecoveryLog log = new(tempDirectory);

        log.Write("trigger:power-resume");

        Assert.That(new FileInfo(path + ".1").Length, Is.EqualTo(WebViewRecoveryLog.MaxFileBytes + 1));
        Assert.That(File.ReadAllLines(path), Has.Length.EqualTo(1));
        Assert.That(Directory.GetFiles(logsDirectory), Has.Length.EqualTo(2));
    }

    [Test]
    public void Log_WriteFailureIsSwallowed() {
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(Path.Combine(tempDirectory, "logs"), "not a directory");
        WebViewRecoveryLog log = new(tempDirectory);

        Assert.DoesNotThrow(() => log.Write("trigger:power-resume"));
    }

    private WebViewRecoveryPolicy CreatePolicy(Func<Exception, IReadOnlyList<string?>> frames) {
        WebViewRecoveryPolicy policy = new(() => now, frames);
        policy.MarkRendered();
        return policy;
    }

    private sealed class SchedulerHarness {
        public SchedulerHarness(Func<DateTimeOffset> clock) {
            Scheduler = new WebViewRebuildScheduler(
                clock,
                Delays.Add,
                () => HasActiveOperations,
                reason => {
                    if (RebuildException is not null) {
                        throw RebuildException;
                    }
                    Rebuilds.Add(reason);
                },
                Logs.Add
            );
        }

        public WebViewRebuildScheduler Scheduler { get; }
        public List<TimeSpan> Delays { get; } = [];
        public List<string> Rebuilds { get; } = [];
        public List<string> Logs { get; } = [];
        public bool HasActiveOperations { get; set; }
        public Exception? RebuildException { get; set; }
    }
}
