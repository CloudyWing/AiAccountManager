using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>
/// 透過標準輸入輸出與 Codex app-server 程序交換 JSON-RPC 訊息。
/// </summary>
public sealed class CodexAppServerSession : ICodexAppServerSession {
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> KnownNotifications = new(StringComparer.Ordinal) {
        "account/login/completed",
        "account/updated",
        "account/rateLimits/updated"
    };
    private readonly Process process;
    private readonly StreamWriter input;
    private readonly StreamReader output;
    private readonly Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeLineAsync;
    private readonly Func<CancellationToken, Task> flushAsync;
    private readonly SemaphoreSlim writerGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pendingRequests = new(StringComparer.Ordinal);
    private readonly Lock responseGate = new();
    private readonly HashSet<string> retiredRequestIds = new(StringComparer.Ordinal);
    private readonly Channel<CodexAppServerNotification> notifications = Channel.CreateUnbounded<CodexAppServerNotification>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    private readonly Task readerTask;
    private readonly Task stderrDrainTask;
    private Exception? failure;
    private long nextRequestId;
    private int initialized;
    private int disposed;
    private int notificationReaderStarted;

    /// <summary>
    /// 初始化工作階段並開始讀取程序訊息。
    /// </summary>
    /// <param name="process">已啟動的 app-server 程序。</param>
    /// <param name="accountId">帳號識別碼。</param>
    /// <param name="profileRevision">帳號資料版本。</param>
    public CodexAppServerSession(Process process, Guid accountId, long profileRevision)
        : this(process, accountId, profileRevision, null, null) {
    }

    private CodexAppServerSession(
        Process process,
        Guid accountId,
        long profileRevision,
        Func<ReadOnlyMemory<char>, CancellationToken, ValueTask>? writeLineOverride,
        Func<CancellationToken, Task>? flushOverride
    ) {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        if (accountId == Guid.Empty || profileRevision < 1) {
            throw new ArgumentException("Codex session identity is invalid.");
        }

        AccountId = accountId;
        ProfileRevision = profileRevision;
        input = process.StandardInput;
        output = process.StandardOutput;
        writeLineAsync = writeLineOverride ?? ((line, cancellationToken) => new ValueTask(input.WriteLineAsync(line, cancellationToken)));
        flushAsync = flushOverride ?? (cancellationToken => input.FlushAsync(cancellationToken));
        readerTask = ReadMessagesAsync();
        stderrDrainTask = DrainStandardErrorAsync();
    }

    /// <inheritdoc />
    public Guid AccountId { get; }

    /// <inheritdoc />
    public long ProfileRevision { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<CodexAppServerNotification> ReadNotificationsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    ) {
        if (Interlocked.Exchange(ref notificationReaderStarted, 1) != 0) {
            throw new InvalidOperationException("Only one app-server notification reader is allowed per session.");
        }

        await foreach (CodexAppServerNotification notification in notifications.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false)) {
            yield return notification;
        }
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken) {
        if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0) {
            throw new InvalidOperationException("Codex app-server session was already initialized.");
        }

        try {
            _ = await RequestAsync(
                "initialize",
                new {
                    clientInfo = new {
                        name = "ai_account_manager",
                        title = "AI Account Manager",
                        version = "0.1.0"
                    },
                    capabilities = new {
                        experimentalApi = true,
                        optOutNotificationMethods = new[] { "remoteControl/status/changed" }
                    }
                },
                cancellationToken,
                allowBeforeInitialize: true
            ).ConfigureAwait(false);
            await SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);
        } catch {
            Interlocked.Exchange(ref initialized, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CodexLoginStart> StartChatGptLoginAsync(CancellationToken cancellationToken) {
        EnsureInitialized();
        JsonElement result = await RequestAsync(
            "account/login/start",
            new { type = "chatgpt" },
            cancellationToken
        ).ConfigureAwait(false);
        RequireObject(result, "login response");
        string type = ReadRequiredString(result, "type");
        string loginId = ReadRequiredString(result, "loginId");
        string authUrlText = ReadRequiredString(result, "authUrl");
        if (!string.Equals(type, "chatgpt", StringComparison.Ordinal)
            || !Uri.TryCreate(authUrlText, UriKind.Absolute, out Uri? authUrl)
            || !string.Equals(authUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authUrl.Host)
            || !string.IsNullOrEmpty(authUrl.UserInfo)) {
            throw new InvalidDataException("Codex returned an invalid ChatGPT OAuth response.");
        }

        return new CodexLoginStart(loginId, authUrl);
    }

    /// <inheritdoc />
    public async Task<CodexLoginCompletion> WaitForLoginCompletionAsync(
        string loginId,
        TimeSpan timeout,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginId);
        if (timeout <= TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        EnsureInitialized();
        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );
        try {
            await foreach (CodexAppServerNotification notification in ReadNotificationsAsync(linkedSource.Token)
                .ConfigureAwait(false)) {
                if (!string.Equals(notification.Method, "account/login/completed", StringComparison.Ordinal)) {
                    continue;
                }

                RequireObject(notification.Parameters, "login completion notification");
                string? completedLoginId = ReadOptionalString(notification.Parameters, "loginId");
                if (!string.Equals(completedLoginId, loginId, StringComparison.Ordinal)) {
                    throw new InvalidDataException("Codex login completion did not match the active login.");
                }

                if (!notification.Parameters.TryGetProperty("success", out JsonElement success)
                    || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
                    throw new InvalidDataException("Codex login completion status is invalid.");
                }

                return new CodexLoginCompletion(loginId, success.GetBoolean());
            }
        } catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested) {
            throw new TimeoutException("Codex OAuth did not complete before its timeout.");
        }

        throw new EndOfStreamException("Codex app-server closed before OAuth completion.");
    }

    /// <inheritdoc />
    public async Task<bool> CancelChatGptLoginAsync(string loginId, CancellationToken cancellationToken) {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(loginId);
        JsonElement result = await RequestAsync(
            "account/login/cancel",
            new { loginId },
            cancellationToken
        ).ConfigureAwait(false);
        RequireObject(result, "login cancellation response");
        string status = ReadRequiredString(result, "status");
        return status switch {
            "canceled" => true,
            "notFound" => false,
            _ => throw new InvalidDataException("Codex returned an unknown login cancellation status.")
        };
    }

    /// <inheritdoc />
    public async Task<CodexAccountRead> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken) {
        EnsureInitialized();
        JsonElement result = await RequestAsync(
            "account/read",
            new { refreshToken },
            cancellationToken
        ).ConfigureAwait(false);
        RequireObject(result, "account response");
        if (!result.TryGetProperty("requiresOpenaiAuth", out JsonElement requiresAuth)
            || requiresAuth.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
            throw new InvalidDataException("Codex account response is missing its auth capability state.");
        }

        if (!result.TryGetProperty("account", out JsonElement account)
            || account.ValueKind == JsonValueKind.Null) {
            return new CodexAccountRead("", null, null, null, requiresAuth.GetBoolean());
        }

        RequireObject(account, "account identity");
        return new CodexAccountRead(
            ReadRequiredString(account, "type"),
            ReadOptionalString(account, "accountId"),
            ReadOptionalString(account, "email"),
            ReadOptionalString(account, "planType"),
            requiresAuth.GetBoolean()
        );
    }

    /// <inheritdoc />
    public async Task<JsonElement> ReadRateLimitsAsync(CancellationToken cancellationToken) {
        EnsureInitialized();
        JsonElement result = await RequestAsync(
            "account/rateLimits/read",
            new { },
            cancellationToken
        ).ConfigureAwait(false);
        RequireObject(result, "rate-limit response");
        return result.Clone();
    }

    /// <inheritdoc />
    public async Task<string> ConsumeResetCreditAsync(string idempotencyKey, string? creditId, CancellationToken cancellationToken) {
        EnsureInitialized();
        object parameters = creditId is null
            ? new { idempotencyKey }
            : new { idempotencyKey, creditId };
        JsonElement result = await RequestAsync("account/rateLimitResetCredit/consume", parameters, cancellationToken)
            .ConfigureAwait(false);
        RequireObject(result, "reset credit response");
        return ReadRequiredString(result, "outcome");
    }

    /// <inheritdoc />
    public async Task LogoutAsync(CancellationToken cancellationToken) {
        EnsureInitialized();
        _ = await RequestAsync("account/logout", new { }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) {
            return;
        }

        try {
            input.Close();
            if (!process.HasExited) {
                using CancellationTokenSource gracefulExitTimeout = new(TimeSpan.FromSeconds(5));
                try {
                    await process.WaitForExitAsync(gracefulExitTimeout.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }

            try {
                await readerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            } catch (TimeoutException) {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            await stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        } finally {
            process.Dispose();
            writerGate.Dispose();
        }
    }

    private async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken,
        bool allowBeforeInitialize = false
    ) {
        if (!allowBeforeInitialize) {
            EnsureInitialized();
        }

        EnsureActive();
        using CancellationTokenSource timeoutSource = new(RequestTimeout);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );
        string id = Interlocked.Increment(ref nextRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        TaskCompletionSource<JsonElement> responseSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingRequests.TryAdd(id, responseSource)) {
            throw new InvalidOperationException("Codex app-server request ID collision.");
        }

        bool requestSent = false;
        try {
            await writerGate.WaitAsync(linkedSource.Token).ConfigureAwait(false);
            try {
                EnsureActive();
                string request = JsonSerializer.Serialize(new {
                    jsonrpc = "2.0",
                    id,
                    method,
                    @params = parameters
                }, JsonOptions);
                await WriteMessageAsync(request, linkedSource.Token).ConfigureAwait(false);
                requestSent = true;
            } finally {
                writerGate.Release();
            }

            return await responseSource.Task.WaitAsync(linkedSource.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested) {
            throw new TimeoutException("Codex app-server request timed out.");
        } finally {
            lock (responseGate) {
                if (pendingRequests.TryRemove(id, out _) && requestSent) {
                    retiredRequestIds.Add(id);
                }
            }
        }
    }

    private async Task SendNotificationAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken
    ) {
        EnsureActive();
        string notification = parameters is null
            ? JsonSerializer.Serialize(new { jsonrpc = "2.0", method }, JsonOptions)
            : JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }, JsonOptions);
        await writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            EnsureActive();
            await WriteMessageAsync(notification, cancellationToken).ConfigureAwait(false);
        } finally {
            writerGate.Release();
        }
    }

    private async Task WriteMessageAsync(string message, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            await writeLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
            await flushAsync(cancellationToken).ConfigureAwait(false);
        } catch (Exception exception) {
            Interlocked.CompareExchange(ref failure, exception, null);
            try {
                process.Kill(entireProcessTree: true);
            } catch (Exception terminationFailure) when (
                terminationFailure is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException
            ) {
                exception.Data["CodexAppServerTerminationFailure"] = terminationFailure;
            }

            throw;
        }
    }

    private async Task ReadMessagesAsync() {
        try {
            while (true) {
                string? line = await output.ReadLineAsync().ConfigureAwait(false);
                if (line is null) {
                    throw new EndOfStreamException("Codex app-server closed its protocol stream.");
                }

                ProcessMessage(line);
            }
        } catch (Exception exception) {
            Interlocked.CompareExchange(ref failure, exception, null);
            foreach (KeyValuePair<string, TaskCompletionSource<JsonElement>> pending in pendingRequests) {
                if (pendingRequests.TryRemove(pending.Key, out TaskCompletionSource<JsonElement>? response)) {
                    response.TrySetException(new InvalidOperationException("Codex app-server protocol failed."));
                }
            }

            notifications.Writer.TryComplete(new InvalidOperationException("Codex app-server protocol failed."));
        }
    }

    private void ProcessMessage(string line) {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        RequireObject(root, "JSON-RPC envelope");
        if (root.TryGetProperty("jsonrpc", out JsonElement protocol)
            && (protocol.ValueKind != JsonValueKind.String
                || !string.Equals(protocol.GetString(), "2.0", StringComparison.Ordinal))) {
            throw new InvalidDataException("Codex app-server returned an invalid JSON-RPC version.");
        }

        if (root.TryGetProperty("id", out JsonElement idElement)) {
            if (root.TryGetProperty("method", out _)) {
                throw new InvalidDataException("Codex app-server sent an unsupported server request.");
            }

            string id = idElement.ValueKind switch {
                JsonValueKind.String => idElement.GetString() ?? "",
                JsonValueKind.Number => idElement.GetRawText(),
                _ => ""
            };
            lock (responseGate) {
                if (id.Length == 0) {
                    throw new InvalidDataException("Codex app-server returned an unknown request ID.");
                }

                bool hasResult = root.TryGetProperty("result", out JsonElement result);
                bool hasError = root.TryGetProperty("error", out JsonElement error);
                if (hasResult == hasError) {
                    throw new InvalidDataException("Codex app-server response envelope is invalid.");
                }

                CodexAppServerRpcException? responseFailure = hasError ? ParseRpcException(error) : null;
                if (pendingRequests.TryRemove(id, out TaskCompletionSource<JsonElement>? response)) {
                    if (responseFailure is not null) {
                        response.TrySetException(responseFailure);
                    } else {
                        response.TrySetResult(result.Clone());
                    }

                    return;
                }

                if (retiredRequestIds.Remove(id)) {
                    return;
                }

                throw new InvalidDataException("Codex app-server returned an unknown request ID.");
            }

        }

        if (!root.TryGetProperty("method", out JsonElement methodElement)
            || methodElement.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException("Codex app-server notification method is missing.");
        }

        string method = methodElement.GetString() ?? "";
        if (!KnownNotifications.Contains(method)) {
            throw new InvalidDataException("Codex app-server sent an unknown notification.");
        }

        JsonElement parameters = root.TryGetProperty("params", out JsonElement paramsElement)
            ? paramsElement.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();
        if (parameters.ValueKind != JsonValueKind.Object || !notifications.Writer.TryWrite(new(method, parameters))) {
            throw new InvalidDataException("Codex app-server notification payload is invalid.");
        }
    }

    private static CodexAppServerRpcException ParseRpcException(JsonElement error) {
        RequireObject(error, "JSON-RPC error");
        if (!error.TryGetProperty("code", out JsonElement codeElement)
            || !codeElement.TryGetInt32(out int code)
            || !error.TryGetProperty("message", out JsonElement messageElement)
            || messageElement.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException("Codex app-server error envelope is invalid.");
        }

        bool unauthorized = code == 401;
        TimeSpan? retryAfter = null;
        if (error.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object) {
            unauthorized |= IsUnauthorizedStatus(data, "status") || IsUnauthorizedStatus(data, "statusCode");
            if (data.TryGetProperty("code", out JsonElement errorCode)
                && errorCode.ValueKind == JsonValueKind.String) {
                string value = errorCode.GetString() ?? "";
                unauthorized |= string.Equals(value, "unauthorized", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "authentication_expired", StringComparison.OrdinalIgnoreCase);
            }

            if (code == 429) {
                if (TryReadRetryAfter(data, "retryAfterMs", 1000, out TimeSpan milliseconds)) {
                    retryAfter = milliseconds;
                } else if (TryReadRetryAfter(data, "retryAfterSeconds", 1, out TimeSpan seconds)) {
                    retryAfter = seconds;
                }
            }
        }

        return new CodexAppServerRpcException(code, unauthorized, retryAfter);
    }

    private static bool TryReadRetryAfter(JsonElement data, string propertyName, double millisecondsPerUnit, out TimeSpan delay) {
        delay = TimeSpan.Zero;
        if (!data.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double amount)
            || !double.IsFinite(amount)
            || amount <= 0) {
            return false;
        }

        double milliseconds = amount * millisecondsPerUnit;
        if (milliseconds > TimeSpan.FromDays(1).TotalMilliseconds) {
            return false;
        }

        delay = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }
    private static bool IsUnauthorizedStatus(JsonElement data, string propertyName) {
        return data.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int status)
            && status == 401;
    }

    private static void RequireObject(JsonElement value, string description) {
        if (value.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException("Codex app-server returned an invalid " + description + ".");
        }
    }

    private static string ReadRequiredString(JsonElement value, string propertyName) {
        if (!value.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString())) {
            throw new InvalidDataException("Codex app-server response is missing a required field.");
        }

        return property.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement value, string propertyName) {
        if (!value.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind == JsonValueKind.Null) {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException("Codex app-server response has an invalid optional field.");
        }

        string? text = property.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private async Task DrainStandardErrorAsync() {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) {
        }
    }

    private void EnsureInitialized() {
        if (Volatile.Read(ref initialized) == 0) {
            throw new InvalidOperationException("Codex app-server session has not been initialized.");
        }
    }

    private void EnsureActive() {
        if (Volatile.Read(ref disposed) != 0) {
            throw new ObjectDisposedException(nameof(CodexAppServerSession));
        }

        Exception? currentFailure = Volatile.Read(ref failure);
        if (currentFailure is not null) {
            throw new InvalidOperationException("Codex app-server session failed.", currentFailure);
        }

        if (process.HasExited) {
            throw new InvalidOperationException("Codex app-server process exited unexpectedly.");
        }
    }
}
