namespace CloudyWing.AiAccountManager.Application.Updates;

/// <summary>
/// 協調更新來源提交與更新套用交接。
/// </summary>
public sealed class UpdateFeedApplyCoordinator {
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool applying;

    internal async Task<IDisposable> EnterAsync(CancellationToken cancellationToken) {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    internal bool IsApplying => applying;

    internal void BeginApply() => applying = true;

    internal async Task CancelApplyAsync() {
        using IDisposable lease = await EnterAsync(CancellationToken.None).ConfigureAwait(false);
        applying = false;
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable {
        private SemaphoreSlim? gate = gate;

        public void Dispose() => Interlocked.Exchange(ref gate, null)?.Release();
    }
}
