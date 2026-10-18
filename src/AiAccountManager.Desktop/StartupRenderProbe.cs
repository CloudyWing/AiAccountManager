using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudyWing.AiAccountManager.Desktop;

public sealed class StartupRenderProbe {
    private readonly string? markerPath;
    private readonly string? dataRoot;
    private readonly string? diagnosticsPath;
    private readonly Lock diagnosticsLock = new();
    private int markerWritten;

    public StartupRenderProbe(string? markerPath, string? dataRoot) {
        this.markerPath = markerPath;
        this.dataRoot = dataRoot;
        diagnosticsPath = markerPath is null ? null : markerPath + ".diagnostics.jsonl";
    }

    public bool IsEnabled => diagnosticsPath is not null;

    public ILoggerProvider CreateLoggerProvider() => new StartupRenderProbeLoggerProvider(this);

    public void RecordEvent(
        string eventName,
        string? status = null,
        string? code = null,
        Exception? exception = null,
        int? eventId = null
    ) {
        if (diagnosticsPath is null) {
            return;
        }

        DiagnosticEvent diagnosticEvent = new(
            DateTimeOffset.UtcNow,
            eventName,
            status,
            code,
            exception?.GetType().FullName,
            exception?.HResult,
            eventId,
            Environment.ProcessId
        );

        try {
            lock (diagnosticsLock) {
                string? directory = Path.GetDirectoryName(diagnosticsPath);
                if (directory is not null) {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(
                    diagnosticsPath,
                    JsonSerializer.Serialize(diagnosticEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                );
            }
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        } catch (SecurityException) {
        } catch (ArgumentException) {
        } catch (NotSupportedException) {
        }
    }

    public void RecordLoggerEvent(LogLevel level, string category, int eventId, Exception? exception) {
        if (level < LogLevel.Warning) {
            return;
        }

        string categoryGroup = category.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal)
            ? "blazor"
            : category.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal)
                ? "webview2"
                : category.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal)
                    ? "host"
                    : "other";
        RecordEvent("logger.event", level.ToString(), categoryGroup, exception, eventId);
    }

    public async Task OnFirstRenderAsync(bool firstRender) {
        if (!firstRender || markerPath is null || dataRoot is null
            || Interlocked.Exchange(ref markerWritten, 1) != 0) {
            return;
        }

        string? directory = Path.GetDirectoryName(markerPath);
        if (directory is null) {
            throw new InvalidOperationException("啟動探針 marker 路徑缺少父目錄。");
        }

        Directory.CreateDirectory(directory);
        RenderMarker marker = new(
            "ai-account-manager.startup-render-probe.v1",
            "ready",
            true,
            true,
            "CloudyWing.AiAccountManager.Desktop.Components.App",
            Path.GetFullPath(dataRoot),
            Environment.ProcessId
        );
        RecordEvent("blazor.first-render.marker-write.started");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(marker, new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            WriteIndented = true
        });
        string temporaryPath = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough
            )) {
                await stream.WriteAsync(json).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, markerPath);
            RecordEvent("blazor.first-render.marker-write.completed", "success");
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException) {
            RecordEvent("blazor.first-render.marker-write.failed", "failure", exception: exception);
            throw;
        } finally {
            if (File.Exists(temporaryPath)) {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record RenderMarker(
        string Schema,
        string Status,
        bool Rendered,
        bool FirstRender,
        string Component,
        string DataRoot,
        int ProcessId
    );

    private sealed record DiagnosticEvent(
        DateTimeOffset TimestampUtc,
        string Event,
        string? Status,
        string? Code,
        string? ExceptionType,
        int? HResult,
        int? EventId,
        int ProcessId
    );

    private sealed class StartupRenderProbeLoggerProvider(StartupRenderProbe probe) : ILoggerProvider {
        public ILogger CreateLogger(string categoryName) => new StartupRenderProbeLogger(probe, categoryName);

        public void Dispose() {
        }
    }

    private sealed class StartupRenderProbeLogger(StartupRenderProbe probe, string categoryName) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) {
            if (IsEnabled(logLevel)) {
                probe.RecordLoggerEvent(logLevel, categoryName, eventId.Id, exception);
            }
        }
    }
}
