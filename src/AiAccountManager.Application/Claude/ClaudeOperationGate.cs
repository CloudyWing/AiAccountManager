using CloudyWing.AiAccountManager.Application.Operations;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 串行化 Claude profile、額度及 Desktop 切換操作，並取得應用程式更新租約。
/// </summary>
public sealed class ClaudeOperationGate {
    private readonly SemaphoreSlim operationSemaphore = new(1, 1);
    private readonly ExclusiveOperationCoordinator operationCoordinator;

    /// <summary>
    /// 建立 Claude 操作閘門。
    /// </summary>
    /// <param name="operationCoordinator">應用程式更新與 profile 作業協調器。</param>
    public ClaudeOperationGate(ExclusiveOperationCoordinator operationCoordinator) {
        ArgumentNullException.ThrowIfNull(operationCoordinator);
        this.operationCoordinator = operationCoordinator;
    }

    /// <summary>
    /// 取得唯一 Claude 操作租約。
    /// </summary>
    /// <param name="cancellationToken">取消等待或操作的權杖。</param>
    /// <returns>提供作業取消權杖並於釋放時還原 gate 的租約。</returns>
    public async ValueTask<Lease> EnterAsync(CancellationToken cancellationToken) {
        await operationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        IOperationLease? operationLease = null;
        CancellationTokenSource? linkedCancellation = null;
        try {
            operationLease = await operationCoordinator
                .EnterProfileOrQuotaOperationAsync(cancellationToken)
                .ConfigureAwait(false);
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                operationLease.CancellationToken
            );
            return new Lease(this, operationLease, linkedCancellation);
        } catch {
            linkedCancellation?.Dispose();
            if (operationLease is not null) {
                await operationLease.DisposeAsync().ConfigureAwait(false);
            }

            operationSemaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// 管理 Claude 作業的取消權杖與應用程式操作租約。
    /// </summary>
    public sealed class Lease : IAsyncDisposable {
        private ClaudeOperationGate? owner;
        private IOperationLease? operationLease;
        private CancellationTokenSource? linkedCancellation;

        internal Lease(
            ClaudeOperationGate owner,
            IOperationLease operationLease,
            CancellationTokenSource linkedCancellation
        ) {
            this.owner = owner;
            this.operationLease = operationLease;
            this.linkedCancellation = linkedCancellation;
        }

        /// <summary>
        /// 取得連結呼叫端與應用程式停止狀態的取消權杖。
        /// </summary>
        /// <value>目前 Claude 操作使用的取消權杖。</value>
        public CancellationToken CancellationToken
            => linkedCancellation?.Token ?? new CancellationToken(canceled: true);

        /// <summary>
        /// 釋放應用程式租約並允許下一個 Claude 操作進入。
        /// </summary>
        /// <returns>已完成的釋放作業。</returns>
        public async ValueTask DisposeAsync() {
            ClaudeOperationGate? currentOwner = Interlocked.Exchange(ref owner, null);
            CancellationTokenSource? cancellation = Interlocked.Exchange(ref linkedCancellation, null);
            IOperationLease? lease = Interlocked.Exchange(ref operationLease, null);
            cancellation?.Dispose();

            try {
                if (lease is not null) {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            } finally {
                currentOwner?.operationSemaphore.Release();
            }
        }
    }
}
