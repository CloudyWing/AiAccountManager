using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>接收目前使用者的視窗喚醒要求。</summary>
public sealed class ActivationPipeServer : IDisposable {
    private const string Command = "AiAccountManager.Activate/1";
    private readonly string pipeName;
    private readonly Action onActivate;
    private readonly Func<NamedPipeServerStream> createStream;
    private readonly CancellationTokenSource cancellation = new();
    private Task? listener;

    /// <summary>建立喚醒管道伺服器。</summary>
    public ActivationPipeServer(string pipeName, Action onActivate) {
        this.pipeName = pipeName;
        this.onActivate = onActivate;
        createStream = CreateStream;
    }

    internal ActivationPipeServer(string pipeName, Action onActivate, Func<NamedPipeServerStream> createStream)
        : this(pipeName, onActivate) {
        this.createStream = createStream;
    }

    /// <summary>建立首個管道並開始接收。</summary>
    public void Start() {
        NamedPipeServerStream first = createStream();
        listener = ListenAsync(first, cancellation.Token);
    }

    /// <summary>停止接收並釋放資源。</summary>
    public void Dispose() {
        cancellation.Cancel();
        try {
            listener?.GetAwaiter().GetResult();
        } catch (OperationCanceledException) {
        } catch (Exception) {
        }
        cancellation.Dispose();
    }

    private NamedPipeServerStream CreateStream() {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("無法取得目前 Windows 使用者 SID。");
        PipeSecurity security = new();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task ListenAsync(NamedPipeServerStream first, CancellationToken token) {
        NamedPipeServerStream? stream = first;
        try {
            while (!token.IsCancellationRequested) {
                if (stream is null) {
                    try {
                        stream = createStream();
                    } catch (Exception) {
                        try {
                            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
                        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                            break;
                        }
                        continue;
                    }
                }
                try {
                    using (stream) {
                        await stream.WaitForConnectionAsync(token).ConfigureAwait(false);
                        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(2));
                        byte[] bytes = new byte[256];
                        int count = 0;
                        while (count < bytes.Length) {
                            int read = await stream.ReadAsync(bytes.AsMemory(count, 1), timeout.Token).ConfigureAwait(false);
                            if (read == 0) {
                                break;
                            }
                            if (bytes[count++] == (byte)'\n') {
                                if (Encoding.UTF8.GetString(bytes, 0, count - 1) == Command) {
                                    onActivate();
                                    byte[] response = Encoding.UTF8.GetBytes($"OK {Environment.ProcessId}\n");
                                    await stream.WriteAsync(response, timeout.Token).ConfigureAwait(false);
                                    await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                                }
                                break;
                            }
                        }
                    }
                } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                    break;
                } catch (Exception) {
                    // 一個連線失敗時繼續接收下一個要求。
                } finally {
                    stream = null;
                }
            }
        } finally {
            stream?.Dispose();
        }
    }
}
