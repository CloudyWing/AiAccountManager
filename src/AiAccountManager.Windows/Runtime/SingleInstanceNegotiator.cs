using System.Diagnostics;

namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>同步協商單一執行個體的使用權與喚醒要求。</summary>
public static class SingleInstanceNegotiator {
    /// <summary>重試取得使用權或通知現有程序，直到成功或逾時。</summary>
    public static NegotiationResult Negotiate(Func<IDisposable?> tryAcquire, Func<TimeSpan, ActivationAttempt> trySignal, TimeSpan totalTimeout, TimeSpan attemptTimeout, TimeSpan retryInterval, Action<TimeSpan> sleep) {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true) {
            IDisposable? instance = tryAcquire();
            if (instance is not null) {
                return new NegotiationResult(NegotiationOutcome.BecamePrimary, instance, 0);
            }

            ActivationAttempt attempt = trySignal(attemptTimeout);
            if (attempt.Outcome == ActivationOutcome.Accepted) {
                return new NegotiationResult(NegotiationOutcome.Signaled, null, attempt.ServerProcessId);
            }

            if (elapsed.Elapsed >= totalTimeout) {
                return new NegotiationResult(NegotiationOutcome.TimedOut, null, 0);
            }

            sleep(retryInterval);
        }
    }
}
