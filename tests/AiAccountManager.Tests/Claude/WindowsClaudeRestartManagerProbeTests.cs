using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Windows.Claude;
using NUnit.Framework;
using ListResult = CloudyWing.AiAccountManager.Windows.Claude.WindowsClaudeRestartManagerProbe.ListResult;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class WindowsClaudeRestartManagerProbeTests {
    private static readonly ClaudeDesktopRootBinding.ObservedProcess Process =
        new(123, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    [Test]
    public void Observe_RegistersOnlyOneResourceAndEndsSessionAfterMoreData() {
        FakeNative native = new();
        native.ListResults.Enqueue(new ListResult(234, 1, []));
        native.ListResults.Enqueue(new ListResult(0, 1, [Process]));
        WindowsClaudeRestartManagerProbe probe = new(native);

        WindowsClaudeRestartManagerProbe.Result result = probe.Observe("fixture/Cookies", CancellationToken.None);

        Assert.That(result.IsComplete, Is.True);
        Assert.That(result.Processes, Is.EqualTo(new[] { Process }));
        Assert.That(native.ResourcePaths, Is.EqualTo(new[] { "fixture/Cookies" }));
        Assert.That(native.Capacities, Is.EqualTo(new[] { 0, 1 }));
        Assert.That(native.EndCount, Is.EqualTo(1));
    }

    [Test]
    public void Observe_ExcludesCurrentProcessHoldingTheResource() {
        ClaudeDesktopRootBinding.ObservedProcess current = new(456, new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));
        FakeNative native = new();
        ClaudeDesktopRootBinding.ObservedProcess system = new(4, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        native.ListResults.Enqueue(new ListResult(234, 3, []));
        native.ListResults.Enqueue(new ListResult(0, 3, [system, Process, current]));
        WindowsClaudeRestartManagerProbe probe = new(native, current.ProcessId);

        WindowsClaudeRestartManagerProbe.Result result = probe.Observe("fixture/Cookies", CancellationToken.None);

        Assert.That(result.IsComplete, Is.True);
        Assert.That(result.Processes, Is.EqualTo(new[] { Process }));
    }

    [TestCase("register")]
    [TestCase("list")]
    [TestCase("end")]
    public void Observe_ReturnsUnknownAndEndsStartedSessionWhenApiFails(string stage) {
        FakeNative native = new() {
            RegisterStatus = stage == "register" ? 5 : 0,
            EndStatus = stage == "end" ? 5 : 0
        };
        native.ListResults.Enqueue(stage == "list" ? new ListResult(5, 0, []) : new ListResult(0, 0, []));
        WindowsClaudeRestartManagerProbe probe = new(native);

        WindowsClaudeRestartManagerProbe.Result result = probe.Observe("fixture/Cookies", CancellationToken.None);

        Assert.That(result.IsComplete, Is.False);
        Assert.That(result.Processes, Is.Empty);
        Assert.That(native.EndCount, Is.EqualTo(1));
    }

    [Test]
    public void Observe_RejectsRepeatedGrowthAndEndsSession() {
        FakeNative native = new();
        for (int count = 1; count <= 4; count++) {
            native.ListResults.Enqueue(new ListResult(234, count, []));
        }

        WindowsClaudeDesktopRestartManagerProbeResultAssertions(native);
    }

    [Test]
    public void Observe_RejectsIncompleteSuccessAndEndsSession() {
        FakeNative native = new();
        native.ListResults.Enqueue(new ListResult(0, 1, []));

        WindowsClaudeDesktopRestartManagerProbeResultAssertions(native);
    }

    [Test]
    public void Observe_CancelledDuringQueryEndsSessionAndDiscardsResults() {
        using CancellationTokenSource cancellation = new();
        FakeNative native = new() { OnGetList = cancellation.Cancel };
        native.ListResults.Enqueue(new ListResult(0, 0, []));
        WindowsClaudeRestartManagerProbe probe = new(native);

        WindowsClaudeRestartManagerProbe.Result result = probe.Observe("fixture/Cookies", cancellation.Token);

        Assert.That(result.IsComplete, Is.False);
        Assert.That(result.Processes, Is.Empty);
        Assert.That(native.EndCount, Is.EqualTo(1));
    }

    [Test]
    public void Observe_StartFailureDoesNotEndUnstartedSession() {
        FakeNative native = new() { StartStatus = 5 };
        WindowsClaudeRestartManagerProbe probe = new(native);

        WindowsClaudeRestartManagerProbe.Result result = probe.Observe("fixture/Cookies", CancellationToken.None);

        Assert.That(result.IsComplete, Is.False);
        Assert.That(native.EndCount, Is.Zero);
    }

    [Test]
    public async Task Observe_ConcurrentCallsKeepResourcesAndSessionsSeparate() {
        ConcurrentNative native = new();
        WindowsClaudeRestartManagerProbe probe = new(native);
        string[] resources = Enumerable.Range(0, 12).Select(index => $"fixture/{index}/Cookies").ToArray();

        WindowsClaudeRestartManagerProbe.Result[] results = await Task.WhenAll(resources.Select(path =>
            Task.Run(() => probe.Observe(path, CancellationToken.None))
        ));

        Assert.That(results.All(result => result.IsComplete), Is.True);
        Assert.That(native.Resources.Values, Is.EquivalentTo(resources));
        Assert.That(native.EndedSessions, Is.EquivalentTo(native.Resources.Keys));
    }

    private static void WindowsClaudeDesktopRestartManagerProbeResultAssertions(FakeNative native) {
        WindowsClaudeRestartManagerProbe probe = new(native);

        WindowsClaudeRestartManagerProbe.Result result = probe.Observe("fixture/Cookies", CancellationToken.None);

        Assert.That(result.IsComplete, Is.False);
        Assert.That(result.Processes, Is.Empty);
        Assert.That(native.EndCount, Is.EqualTo(1));
    }

    private sealed class FakeNative : WindowsClaudeRestartManagerProbe.IRestartManagerNative {
        public int StartStatus { get; init; }

        public int RegisterStatus { get; init; }

        public int EndStatus { get; init; }

        public Queue<ListResult> ListResults { get; } = new();

        public List<string> ResourcePaths { get; } = [];

        public List<int> Capacities { get; } = [];

        public int EndCount { get; private set; }

        public Action? OnGetList { get; init; }

        public int StartSession(out uint session) {
            session = 7;
            return StartStatus;
        }

        public int RegisterResource(uint session, string resourcePath) {
            ResourcePaths.Add(resourcePath);
            return RegisterStatus;
        }

        public ListResult GetList(uint session, int capacity) {
            Capacities.Add(capacity);
            OnGetList?.Invoke();
            return ListResults.Dequeue();
        }

        public int EndSession(uint session) {
            EndCount++;
            return EndStatus;
        }
    }

    private sealed class ConcurrentNative : WindowsClaudeRestartManagerProbe.IRestartManagerNative {
        private int nextSession;

        public System.Collections.Concurrent.ConcurrentDictionary<uint, string> Resources { get; } = new();

        public System.Collections.Concurrent.ConcurrentBag<uint> EndedSessions { get; } = [];

        public int StartSession(out uint session) {
            session = checked((uint)Interlocked.Increment(ref nextSession));
            return 0;
        }

        public int RegisterResource(uint session, string resourcePath) {
            return Resources.TryAdd(session, resourcePath) ? 0 : 1;
        }

        public ListResult GetList(uint session, int capacity) {
            return Resources.ContainsKey(session) ? new ListResult(0, 0, []) : new ListResult(1, 0, []);
        }

        public int EndSession(uint session) {
            EndedSessions.Add(session);
            return 0;
        }
    }
}
