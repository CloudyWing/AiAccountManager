using System.Diagnostics;
using System.Reflection;
using System.Text;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Windows.Codex;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class CodexAppServerSessionTests {
    [Test]
    public async Task HeaderlessMessagesAndLateCanceledResponseAreAcceptedWhileUnknownIdFailsClosed() {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "AiAccountManagerTests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(temporaryDirectory);
        string scriptPath = Path.Combine(temporaryDirectory, "fake-app-server.ps1");
        await File.WriteAllTextAsync(scriptPath, FakeAppServerScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        ProcessStartInfo startInfo = new("powershell.exe") {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        try {
            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The fake app-server process did not start.");
            await using CodexAppServerSession session = new(process, Guid.NewGuid(), 1);
            await session.InitializeAsync(CancellationToken.None);
            await using IAsyncEnumerator<CodexAppServerNotification> notifications = session
                .ReadNotificationsAsync(CancellationToken.None)
                .GetAsyncEnumerator();
            using CancellationTokenSource cancellation = new();

            Task<CodexLoginStart> login = session.StartChatGptLoginAsync(cancellation.Token);
            Assert.That(await notifications.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(notifications.Current.Method, Is.EqualTo("account/updated"));
            cancellation.Cancel();
            await Assert.CatchAsync<OperationCanceledException>(async () => await login.WaitAsync(TimeSpan.FromSeconds(5)));

            await session.LogoutAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await notifications.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(notifications.Current.Method, Is.EqualTo("account/rateLimits/updated"));
            Assert.That(await notifications.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(notifications.Current.Method, Is.EqualTo("account/updated"));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => {
                _ = await notifications.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            });
        } finally {
            if (Directory.Exists(temporaryDirectory)) {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task InitializeOptsOutOfRemoteControlNotificationsAndRejectsOtherUnknownNotifications() {
        (Process process, string temporaryDirectory) = await StartFakeAppServerAsync(
            FakeAppServerNotificationOptOutScript
        );
        try {
            await using CodexAppServerSession session = new(process, Guid.NewGuid(), 1);
            await session.InitializeAsync(CancellationToken.None);
            await using IAsyncEnumerator<CodexAppServerNotification> notifications = session
                .ReadNotificationsAsync(CancellationToken.None)
                .GetAsyncEnumerator();

            Assert.That(await notifications.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(notifications.Current.Method, Is.EqualTo("account/updated"));
            await session.LogoutAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAsync<InvalidOperationException>(async () => {
                _ = await notifications.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            });
        } finally {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Test]
    public async Task CancellationBeforeWritingLeavesSessionUsable() {
        (Process process, string temporaryDirectory) = await StartFakeAppServerAsync(FakeAppServerLogoutScript);
        try {
            await using CodexAppServerSession session = new(process, Guid.NewGuid(), 1);
            await session.InitializeAsync(CancellationToken.None);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            await Assert.CatchAsync<OperationCanceledException>(async () =>
                await session.StartChatGptLoginAsync(cancellation.Token)
            );
            await session.LogoutAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        } finally {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Test]
    public async Task CancellationDuringPartialWriteDiscardsSessionAndTerminatesChild() {
        (Process process, string temporaryDirectory) = await StartFakeAppServerAsync(FakeAppServerScript);
        try {
            TaskCompletionSource<bool> partialWriteFlushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeLineAsync = async (line, cancellationToken) => {
                if (line.ToString().Contains("\"method\":\"account/login/start\"", StringComparison.Ordinal)) {
                    await WritePartialLineAsync(process, line);
                    partialWriteFlushed.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                await process.StandardInput.WriteLineAsync(line, cancellationToken);
            };
            CodexAppServerSession session = CreateSessionWithWriter(
                process,
                Guid.NewGuid(),
                writeLineAsync,
                cancellationToken => process.StandardInput.FlushAsync(cancellationToken)
            );
            await using (session) {
                await session.InitializeAsync(CancellationToken.None);
                using CancellationTokenSource cancellation = new();
                Task<CodexLoginStart> login = session.StartChatGptLoginAsync(cancellation.Token);
                await partialWriteFlushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();

                await Assert.CatchAsync<OperationCanceledException>(async () =>
                    await login.WaitAsync(TimeSpan.FromSeconds(5))
                );
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await session.LogoutAsync(CancellationToken.None)
                );
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        } finally {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Test]
    public async Task WriteFailureAfterPartialLineDiscardsSessionAndTerminatesChild() {
        (Process process, string temporaryDirectory) = await StartFakeAppServerAsync(FakeAppServerScript);
        try {
            Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeLineAsync = async (line, cancellationToken) => {
                if (line.ToString().Contains("\"method\":\"account/login/start\"", StringComparison.Ordinal)) {
                    await WritePartialLineAsync(process, line);
                    throw new IOException("Controlled partial-write failure.");
                }

                await process.StandardInput.WriteLineAsync(line, cancellationToken);
            };
            CodexAppServerSession session = CreateSessionWithWriter(
                process,
                Guid.NewGuid(),
                writeLineAsync,
                cancellationToken => process.StandardInput.FlushAsync(cancellationToken)
            );
            await using (session) {
                await session.InitializeAsync(CancellationToken.None);

                IOException failure = (await Assert.ThrowsAsync<IOException>(async () =>
                    await session.StartChatGptLoginAsync(CancellationToken.None)
                ))!;

                Assert.That(failure.Message, Is.EqualTo("Controlled partial-write failure."));
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await session.LogoutAsync(CancellationToken.None)
                );
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        } finally {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Test]
    public async Task ValidLateErrorForCanceledRequestIsIgnoredAfterValidation() {
        const string lateError = "{\"jsonrpc\":\"2.0\",\"id\":__LOGIN_ID__,\"error\":{\"code\":-32000,\"message\":\"cancelled\"}}";
        (Process process, string temporaryDirectory) = await StartFakeAppServerAsync(
            BuildRetiredResponseScript(lateError, duplicateResponse: false)
        );
        try {
            await using CodexAppServerSession session = new(process, Guid.NewGuid(), 1);
            await session.InitializeAsync(CancellationToken.None);
            await CancelLoginAfterServerAcceptedAsync(session);

            await session.LogoutAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        } finally {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":__LOGIN_ID__,\"result\":{},\"error\":{\"code\":-1,\"message\":\"cancelled\"}}")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":__LOGIN_ID__}")]
    [TestCase("{\"jsonrpc\":null,\"id\":__LOGIN_ID__,\"result\":{}}")]
    [TestCase("{\"jsonrpc\":2,\"id\":__LOGIN_ID__,\"result\":{}}")]
    [TestCase("{\"jsonrpc\":\"1.0\",\"id\":__LOGIN_ID__,\"result\":{}}")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":__LOGIN_ID__,\"method\":\"account/login/completed\",\"result\":{}}")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":__LOGIN_ID__,\"error\":{\"code\":\"invalid\",\"message\":\"cancelled\"}}")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":__LOGIN_ID__,\"error\":{\"code\":-1,\"message\":null}}")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":true,\"result\":{}}")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":\"\",\"result\":{}}")]
    public async Task MalformedLateResponseForCanceledRequestFailsClosed(string responseLine) {
        (Process process, string temporaryDirectory) = await StartFakeAppServerAsync(
            BuildRetiredResponseScript(responseLine, duplicateResponse: false)
        );
        try {
            await using CodexAppServerSession session = new(process, Guid.NewGuid(), 1);
            await session.InitializeAsync(CancellationToken.None);
            await CancelLoginAfterServerAcceptedAsync(session);

            await Assert.CatchAsync<InvalidOperationException>(async () =>
                await session.LogoutAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))
            );
        } finally {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Test]
    public async Task DuplicateLateResponseForCanceledRequestFailsClosed() {
        const string lateResult = "{\"jsonrpc\":\"2.0\",\"id\":__LOGIN_ID__,\"result\":{}}";
        (Process process, string temporaryDirectory) = await StartFakeAppServerAsync(
            BuildRetiredResponseScript(lateResult, duplicateResponse: true)
        );
        try {
            await using CodexAppServerSession session = new(process, Guid.NewGuid(), 1);
            await session.InitializeAsync(CancellationToken.None);
            await CancelLoginAfterServerAcceptedAsync(session);

            await Assert.CatchAsync<InvalidOperationException>(async () =>
                await session.LogoutAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))
            );
        } finally {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private static async Task<(Process Process, string TemporaryDirectory)> StartFakeAppServerAsync(string script) {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "AiAccountManagerTests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(temporaryDirectory);
        string scriptPath = Path.Combine(temporaryDirectory, "fake-app-server.ps1");
        try {
            await File.WriteAllTextAsync(
                scriptPath,
                script,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            );
            ProcessStartInfo startInfo = new("powershell.exe") {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The fake app-server process did not start.");
            return (process, temporaryDirectory);
        } catch {
            DeleteTemporaryDirectory(temporaryDirectory);
            throw;
        }
    }

    private static CodexAppServerSession CreateSessionWithWriter(
        Process process,
        Guid accountId,
        Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeLineAsync,
        Func<CancellationToken, Task> flushAsync
    ) {
        ConstructorInfo constructor = typeof(CodexAppServerSession).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [
                typeof(Process),
                typeof(Guid),
                typeof(long),
                typeof(Func<ReadOnlyMemory<char>, CancellationToken, ValueTask>),
                typeof(Func<CancellationToken, Task>)
            ],
            modifiers: null
        ) ?? throw new InvalidOperationException("The controlled app-server writer constructor was not found.");
        return (CodexAppServerSession)constructor.Invoke([
            process,
            accountId,
            1L,
            writeLineAsync,
            flushAsync
        ]);
    }

    private static async Task WritePartialLineAsync(Process process, ReadOnlyMemory<char> line) {
        int partialLength = Math.Max(1, line.Length / 2);
        await process.StandardInput.WriteAsync(line[..partialLength], CancellationToken.None);
        await process.StandardInput.FlushAsync(CancellationToken.None);
    }

    private static async Task CancelLoginAfterServerAcceptedAsync(CodexAppServerSession session) {
        await using IAsyncEnumerator<CodexAppServerNotification> notifications = session
            .ReadNotificationsAsync(CancellationToken.None)
            .GetAsyncEnumerator();
        using CancellationTokenSource cancellation = new();
        Task<CodexLoginStart> login = session.StartChatGptLoginAsync(cancellation.Token);
        Assert.That(await notifications.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(notifications.Current.Method, Is.EqualTo("account/updated"));
        cancellation.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(async () =>
            await login.WaitAsync(TimeSpan.FromSeconds(5))
        );
    }

    private static string BuildRetiredResponseScript(string responseTemplate, bool duplicateResponse) {
        string encodedResponse = Convert.ToBase64String(Encoding.UTF8.GetBytes(responseTemplate));
        string duplicateLine = duplicateResponse ? "Write-RpcMessage $retiredResponse" : "";
        return FakeAppServerRetiredResponseScript
            .Replace("__RESPONSE_BASE64__", encodedResponse, StringComparison.Ordinal)
            .Replace("__DUPLICATE_RESPONSE__", duplicateLine, StringComparison.Ordinal);
    }

    private static void DeleteTemporaryDirectory(string temporaryDirectory) {
        if (Directory.Exists(temporaryDirectory)) {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private const string FakeAppServerScript = """
        $ErrorActionPreference = 'Stop'
        function Read-RpcMessage {
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) {
                throw 'The app-server input stream closed.'
            }
            return ConvertFrom-Json -InputObject $line
        }
        function Write-RpcMessage([string]$message) {
            [Console]::Out.WriteLine($message)
            [Console]::Out.Flush()
        }

        $initialize = Read-RpcMessage
        $initializeId = ConvertTo-Json -InputObject $initialize.id -Compress
        Write-RpcMessage ('{"id":' + $initializeId + ',"result":{}}')
        $initialized = Read-RpcMessage
        $login = Read-RpcMessage
        Write-RpcMessage '{"method":"account/updated","params":{}}'
        $logout = Read-RpcMessage
        $loginId = ConvertTo-Json -InputObject $login.id -Compress
        Write-RpcMessage ('{"id":' + $loginId + ',"result":{"type":"chatgpt","loginId":"late-login","authUrl":"https://example.test/auth"}}')
        Write-RpcMessage '{"method":"account/rateLimits/updated","params":{}}'
        $logoutId = ConvertTo-Json -InputObject $logout.id -Compress
        Write-RpcMessage ('{"id":' + $logoutId + ',"result":{}}')
        Write-RpcMessage '{"method":"account/updated","params":{}}'
        Write-RpcMessage '{"id":"future-unknown-id","result":{}}'
        """;

    private const string FakeAppServerLogoutScript = """
        $ErrorActionPreference = 'Stop'
        function Read-RpcMessage {
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) {
                throw 'The app-server input stream closed.'
            }
            return ConvertFrom-Json -InputObject $line
        }
        function Write-RpcMessage([string]$message) {
            [Console]::Out.WriteLine($message)
            [Console]::Out.Flush()
        }

        $initialize = Read-RpcMessage
        $initializeId = ConvertTo-Json -InputObject $initialize.id -Compress
        Write-RpcMessage ('{"jsonrpc":"2.0","id":' + $initializeId + ',"result":{}}')
        $initialized = Read-RpcMessage
        $logout = Read-RpcMessage
        $logoutId = ConvertTo-Json -InputObject $logout.id -Compress
        Write-RpcMessage ('{"jsonrpc":"2.0","id":' + $logoutId + ',"result":{}}')
        """;

    private const string FakeAppServerNotificationOptOutScript = """
        $ErrorActionPreference = 'Stop'
        function Read-RpcMessage {
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) {
                throw 'The app-server input stream closed.'
            }
            return ConvertFrom-Json -InputObject $line
        }
        function Write-RpcMessage([string]$message) {
            [Console]::Out.WriteLine($message)
            [Console]::Out.Flush()
        }

        $initialize = Read-RpcMessage
        $initializeId = ConvertTo-Json -InputObject $initialize.id -Compress
        $optOutMethods = $initialize.params.capabilities.optOutNotificationMethods
        $hasOptOut = $optOutMethods -is [System.Array] -and $optOutMethods.Count -eq 1 -and $optOutMethods[0] -ceq 'remoteControl/status/changed'
        Write-RpcMessage ('{"id":' + $initializeId + ',"result":{}}')
        $initialized = Read-RpcMessage
        if ($hasOptOut) {
            Write-RpcMessage '{"method":"account/updated","params":{}}'
        } else {
            Write-RpcMessage '{"method":"remoteControl/status/changed","params":{"status":"unknown"}}'
            exit 0
        }
        $logout = Read-RpcMessage
        $logoutId = ConvertTo-Json -InputObject $logout.id -Compress
        Write-RpcMessage ('{"id":' + $logoutId + ',"result":{}}')
        Write-RpcMessage '{"method":"unexpected/status/changed","params":{}}'
        """;

    private const string FakeAppServerRetiredResponseScript = """
        $ErrorActionPreference = 'Stop'
        function Read-RpcMessage {
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) {
                throw 'The app-server input stream closed.'
            }
            return ConvertFrom-Json -InputObject $line
        }
        function Write-RpcMessage([string]$message) {
            [Console]::Out.WriteLine($message)
            [Console]::Out.Flush()
        }

        $initialize = Read-RpcMessage
        $initializeId = ConvertTo-Json -InputObject $initialize.id -Compress
        Write-RpcMessage ('{"jsonrpc":"2.0","id":' + $initializeId + ',"result":{}}')
        $initialized = Read-RpcMessage
        $login = Read-RpcMessage
        Write-RpcMessage '{"jsonrpc":"2.0","method":"account/updated","params":{}}'
        $logout = Read-RpcMessage
        $loginId = ConvertTo-Json -InputObject $login.id -Compress
        $responseTemplate = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('__RESPONSE_BASE64__'))
        $retiredResponse = $responseTemplate.Replace('__LOGIN_ID__', $loginId)
        Write-RpcMessage $retiredResponse
        __DUPLICATE_RESPONSE__
        $logoutId = ConvertTo-Json -InputObject $logout.id -Compress
        Write-RpcMessage ('{"jsonrpc":"2.0","id":' + $logoutId + ',"result":{}}')
        """;
}
