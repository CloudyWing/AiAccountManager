using System.Collections.Concurrent;

namespace CloudyWing.AiAccountManager.Application.Operations;

/// <summary>
/// 表示可取消且可釋放的作業使用權。
/// </summary>
public interface IOperationLease : IAsyncDisposable {
    /// <summary>
    /// 取得作業的取消權杖。
    /// </summary>
    CancellationToken CancellationToken { get; }
}

/// <summary>
/// 協調帳號、額度與更新套用作業的互斥執行。
/// </summary>
public sealed class ExclusiveOperationCoordinator {
    private readonly Lock sync = new();
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> profileGates = new();
    private readonly CancellationTokenSource applicationStopping = new();
    private TaskCompletionSource<bool> updateReleased = CompletedSource();
    private TaskCompletionSource<bool>? operationsDrained;
    private TaskCompletionSource<bool> operationsIdle = CompletedSource();
    private int activeOperations;
    private bool updateReserved;
    private bool stopAccepting;

    /// <summary>
    /// 取得目前是否有進行中的帳號或額度作業，或已保留更新套用權。
    /// </summary>
    public bool HasActiveOperations {
        get {
            lock (sync) {
                return activeOperations > 0 || updateReserved;
            }
        }
    }

    /// <summary>
    /// 等待進入帳號或額度作業。
    /// </summary>
    /// <param name="cancellationToken">取消等待的權杖。</param>
    /// <returns>釋放時結束作業的使用權。</returns>
    public ValueTask<IOperationLease> EnterProfileOrQuotaOperationAsync(CancellationToken cancellationToken) {
        return EnterProfileOrQuotaOperationAsync(null, cancellationToken);
    }

    /// <summary>
    /// 等待進入指定帳號的帳號或額度作業。
    /// </summary>
    /// <param name="accountId">帳號識別碼。</param>
    /// <param name="cancellationToken">取消等待的權杖。</param>
    /// <returns>釋放時結束作業的使用權。</returns>
    /// <exception cref="ArgumentException">帳號識別碼為空的 GUID。</exception>
    public ValueTask<IOperationLease> EnterProfileOrQuotaOperationAsync(
        Guid accountId,
        CancellationToken cancellationToken
    ) {
        if (accountId == Guid.Empty) {
            throw new ArgumentException("Account ID must not be empty.", nameof(accountId));
        }

        return EnterProfileOrQuotaOperationAsync((Guid?)accountId, cancellationToken);
    }

    /// <summary>
    /// 保留更新套用權並等待進行中的作業結束。
    /// </summary>
    /// <param name="cancellationToken">取消等待的權杖。</param>
    /// <returns>釋放時允許其他作業繼續的使用權。</returns>
    /// <exception cref="OperationCanceledException">應用程式正在停止。</exception>
    public async ValueTask<IAsyncDisposable> EnterUpdateApplyAsync(CancellationToken cancellationToken) {
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            applicationStopping.Token
        );
        await updateGate.WaitAsync(linkedSource.Token).ConfigureAwait(false);
        Task waitTask;

        lock (sync) {
            if (stopAccepting) {
                updateGate.Release();
                throw new OperationCanceledException("The application is stopping.", linkedSource.Token);
            }

            updateReserved = true;
            updateReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            operationsDrained = activeOperations == 0
                ? CompletedSource()
                : new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            waitTask = operationsDrained.Task;
        }

        try {
            await waitTask.WaitAsync(linkedSource.Token).ConfigureAwait(false);
            return new UpdateLease(this);
        } catch {
            ReleaseUpdate();
            throw;
        }
    }

    /// <summary>
    /// 停止接受作業並取消進行中的作業。
    /// </summary>
    public void StopAccepting() {
        lock (sync) {
            if (stopAccepting) {
                return;
            }

            stopAccepting = true;
        }

        applicationStopping.Cancel();
    }

    /// <summary>
    /// 等待所有進行中的帳號與額度作業結束。
    /// </summary>
    /// <param name="cancellationToken">取消等待的權杖。</param>
    /// <returns>表示等待作業的工作。</returns>
    public async Task WaitForActiveOperationsAsync(CancellationToken cancellationToken) {
        while (true) {
            Task waitTask;
            lock (sync) {
                if (activeOperations == 0) {
                    return;
                }

                waitTask = operationsIdle.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<IOperationLease> EnterProfileOrQuotaOperationAsync(
        Guid? accountId,
        CancellationToken cancellationToken
    ) {
        SemaphoreSlim? profileGate = accountId is Guid id
            ? profileGates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1))
            : null;

        while (true) {
            Task waitTask;

            lock (sync) {
                if (stopAccepting) {
                    throw new OperationCanceledException("The application is stopping.", applicationStopping.Token);
                }

                if (!updateReserved) {
                    if (activeOperations == 0) {
                        operationsIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    activeOperations++;
                    break;
                }

                waitTask = updateReleased.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            applicationStopping.Token
        );
        try {
            if (profileGate is not null) {
                await profileGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
            }

            return new OperationLease(this, profileGate, operationCancellation);
        } catch {
            operationCancellation.Dispose();
            ReleaseOperation();
            throw;
        }
    }

    private static TaskCompletionSource<bool> CompletedSource() {
        TaskCompletionSource<bool> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }

    private void ReleaseOperation() {
        TaskCompletionSource<bool>? idle = null;
        TaskCompletionSource<bool>? drained = null;

        lock (sync) {
            activeOperations--;
            if (activeOperations == 0) {
                idle = operationsIdle;
                if (updateReserved) {
                    drained = operationsDrained;
                }
            }
        }

        idle?.TrySetResult(true);
        drained?.TrySetResult(true);
    }

    private void ReleaseUpdate() {
        TaskCompletionSource<bool>? released;

        lock (sync) {
            updateReserved = false;
            operationsDrained = null;
            released = updateReleased;
        }

        released.TrySetResult(true);
        updateGate.Release();
    }

    private sealed class OperationLease(
        ExclusiveOperationCoordinator owner,
        SemaphoreSlim? profileGate,
        CancellationTokenSource cancellation
    ) : IOperationLease {
        private ExclusiveOperationCoordinator? owner = owner;
        private SemaphoreSlim? profileGate = profileGate;
        private CancellationTokenSource? cancellation = cancellation;

        public CancellationToken CancellationToken => cancellation?.Token ?? new CancellationToken(canceled: true);

        public ValueTask DisposeAsync() {
            Interlocked.Exchange(ref profileGate, null)?.Release();
            Interlocked.Exchange(ref owner, null)?.ReleaseOperation();
            Interlocked.Exchange(ref cancellation, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UpdateLease(ExclusiveOperationCoordinator owner) : IAsyncDisposable {
        private ExclusiveOperationCoordinator? owner = owner;

        public ValueTask DisposeAsync() {
            Interlocked.Exchange(ref owner, null)?.ReleaseUpdate();
            return ValueTask.CompletedTask;
        }
    }
}

