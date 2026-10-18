using CloudyWing.AiAccountManager.Application.Runtime;

namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>
/// 使用系統時鐘提供 UTC 時間與延遲等待。
/// </summary>
public sealed class WindowsSystemClock : IClock {
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
        return Task.Delay(delay, cancellationToken);
    }
}
