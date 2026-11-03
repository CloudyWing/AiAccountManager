using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>向現有程序傳送視窗喚醒要求。</summary>
public static class ActivationPipeClient {
    /// <summary>在指定時間內等待有效回覆。</summary>
    public static ActivationAttempt TrySend(string pipeName, TimeSpan timeout) {
        try {
            return SendAsync(pipeName, timeout).GetAwaiter().GetResult();
        } catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException) {
            return new ActivationAttempt(ActivationOutcome.NoResponse, 0);
        }
    }

    private static async Task<ActivationAttempt> SendAsync(string pipeName, TimeSpan timeout) {
        using CancellationTokenSource cancellation = new(timeout);
        using NamedPipeClientStream stream = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(cancellation.Token).ConfigureAwait(false);
        byte[] request = Encoding.UTF8.GetBytes("AiAccountManager.Activate/1\n");
        await stream.WriteAsync(request, cancellation.Token).ConfigureAwait(false);
        await stream.FlushAsync(cancellation.Token).ConfigureAwait(false);
        byte[] response = new byte[256];
        int count = 0;
        while (count < response.Length) {
            int read = await stream.ReadAsync(response.AsMemory(count, 1), cancellation.Token).ConfigureAwait(false);
            if (read == 0) {
                break;
            }
            if (response[count++] == (byte)'\n') {
                string line = Encoding.UTF8.GetString(response, 0, count - 1);
                if (line.StartsWith("OK ", StringComparison.Ordinal)
                    && int.TryParse(line.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                    && pid > 0
                ) {
                    return new ActivationAttempt(ActivationOutcome.Accepted, pid);
                }
                break;
            }
        }
        return new ActivationAttempt(ActivationOutcome.NoResponse, 0);
    }
}
