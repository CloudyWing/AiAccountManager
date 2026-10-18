using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CloudyWing.AiAccountManager.Windows.Claude;

internal enum ClaudeOAuthEndpoint {
    Profile,
    Usage
}

internal sealed record ClaudeOAuthHttpResponse(int StatusCode, string? RetryAfter, byte[] Body);

internal sealed class WindowsClaudeWinHttpTransport {
    private const string HostName = "api.anthropic.com";
    private const string UserAgent = "AiAccountManager";
    private const string AnthropicBetaHeader = "anthropic-beta: oauth-2025-04-20\r\n";
    private const string AcceptHeader = "Accept: application/json\r\n";
    private const string AuthorizationHeaderPrefix = "Authorization: Bearer ";
    private const int RequestTimeoutMilliseconds = 20_000;
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int ReadBufferBytes = 8192;
    private const ushort HttpsPort = 443;
    private const uint WinHttpAccessTypeDefaultProxy = 0;
    private const uint WinHttpFlagSecure = 0x00800000;
    private const uint WinHttpOptionSecureProtocols = 84;
    private const uint WinHttpOptionRedirectPolicy = 88;
    private const uint WinHttpOptionDisableFeature = 63;
    private const uint WinHttpSecureProtocolTls12 = 0x00000800;
    private const uint WinHttpRedirectPolicyNever = 0;
    private const uint WinHttpDisableAuthentication = 0x00000004;
    private const uint WinHttpDisableRedirects = 0x00000002;
    private const uint WinHttpDisableCookies = 0x00000001;
    private const uint WinHttpQueryStatusCode = 19;
    private const uint WinHttpQueryRetryAfter = 36;
    private const uint WinHttpQueryFlagNumber = 0x20000000;
    private const uint WinHttpAddRequestHeader = 0x20000000;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorWinHttpHeaderNotFound = 12150;

    internal Task<ClaudeOAuthHttpResponse> GetProfileAsync(
        string accessToken,
        CancellationToken cancellationToken
    ) => GetAsync(ClaudeOAuthEndpoint.Profile, accessToken, cancellationToken);

    internal Task<ClaudeOAuthHttpResponse> GetUsageAsync(
        string accessToken,
        CancellationToken cancellationToken
    ) => GetAsync(ClaudeOAuthEndpoint.Usage, accessToken, cancellationToken);

    private static Task<ClaudeOAuthHttpResponse> GetAsync(
        ClaudeOAuthEndpoint endpoint,
        string accessToken,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(accessToken);
        ValidateAccessToken(accessToken);

        return Task.Run(
            () => SendRequest(endpoint, accessToken, cancellationToken),
            CancellationToken.None
        );
    }

    private static ClaudeOAuthHttpResponse SendRequest(
        ClaudeOAuthEndpoint endpoint,
        string accessToken,
        CancellationToken cancellationToken
    ) {
        using CancellationTokenSource timeoutSource = new(TimeSpan.FromMilliseconds(RequestTimeoutMilliseconds));
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );
        RequestHandleState requestState = new();
        using CancellationTokenRegistration cancellationRegistration = linkedSource.Token.Register(
            static state => ((RequestHandleState)state!).CloseRequest(),
            requestState
        );

        try {
            return SendRequestCore(endpoint, accessToken, linkedSource.Token, requestState);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw new OperationCanceledException(cancellationToken);
        } catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested) {
            throw new TimeoutException("The Claude API request exceeded its 20-second time limit.");
        } catch (Win32Exception) when (cancellationToken.IsCancellationRequested) {
            throw new OperationCanceledException(cancellationToken);
        } catch (Win32Exception) when (timeoutSource.IsCancellationRequested) {
            throw new TimeoutException("The Claude API request exceeded its 20-second time limit.");
        } finally {
            requestState.CloseRequest();
        }
    }

    private static ClaudeOAuthHttpResponse SendRequestCore(
        ClaudeOAuthEndpoint endpoint,
        string accessToken,
        CancellationToken cancellationToken,
        RequestHandleState requestState
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        nint session = WinHttpNative.WinHttpOpen(
            UserAgent,
            WinHttpAccessTypeDefaultProxy,
            null,
            null,
            0
        );
        if (session == nint.Zero) {
            ThrowLastWinHttpError(nameof(WinHttpNative.WinHttpOpen));
        }

        nint connection = nint.Zero;
        try {
            EnsureWinHttpCall(
                WinHttpNative.WinHttpSetTimeouts(
                    session,
                    RequestTimeoutMilliseconds,
                    RequestTimeoutMilliseconds,
                    RequestTimeoutMilliseconds,
                    RequestTimeoutMilliseconds
                ),
                nameof(WinHttpNative.WinHttpSetTimeouts)
            );

            uint secureProtocols = WinHttpSecureProtocolTls12;
            EnsureWinHttpCall(
                WinHttpNative.WinHttpSetOption(
                    session,
                    WinHttpOptionSecureProtocols,
                    ref secureProtocols,
                    sizeof(uint)
                ),
                nameof(WinHttpNative.WinHttpSetOption)
            );

            cancellationToken.ThrowIfCancellationRequested();
            connection = WinHttpNative.WinHttpConnect(session, HostName, HttpsPort, 0);
            if (connection == nint.Zero) {
                ThrowLastWinHttpError(nameof(WinHttpNative.WinHttpConnect));
            }

            nint request = WinHttpNative.WinHttpOpenRequest(
                connection,
                "GET",
                GetPath(endpoint),
                null,
                null,
                nint.Zero,
                WinHttpFlagSecure
            );
            if (request == nint.Zero) {
                ThrowLastWinHttpError(nameof(WinHttpNative.WinHttpOpenRequest));
            }

            requestState.SetRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            uint redirectPolicy = WinHttpRedirectPolicyNever;
            EnsureWinHttpCall(
                WinHttpNative.WinHttpSetOption(
                    request,
                    WinHttpOptionRedirectPolicy,
                    ref redirectPolicy,
                    sizeof(uint)
                ),
                nameof(WinHttpNative.WinHttpSetOption)
            );

            uint disabledFeatures = WinHttpDisableAuthentication | WinHttpDisableRedirects | WinHttpDisableCookies;
            EnsureWinHttpCall(
                WinHttpNative.WinHttpSetOption(
                    request,
                    WinHttpOptionDisableFeature,
                    ref disabledFeatures,
                    sizeof(uint)
                ),
                nameof(WinHttpNative.WinHttpSetOption)
            );

            string headers = string.Concat(
                AuthorizationHeaderPrefix,
                accessToken,
                "\r\n",
                AnthropicBetaHeader,
                AcceptHeader
            );
            EnsureWinHttpCall(
                WinHttpNative.WinHttpAddRequestHeaders(
                    request,
                    headers,
                    checked((uint)headers.Length),
                    WinHttpAddRequestHeader
                ),
                nameof(WinHttpNative.WinHttpAddRequestHeaders)
            );

            cancellationToken.ThrowIfCancellationRequested();
            EnsureWinHttpCall(
                WinHttpNative.WinHttpSendRequest(
                    request,
                    null,
                    0,
                    nint.Zero,
                    0,
                    0,
                    0
                ),
                nameof(WinHttpNative.WinHttpSendRequest)
            );
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWinHttpCall(
                WinHttpNative.WinHttpReceiveResponse(request, nint.Zero),
                nameof(WinHttpNative.WinHttpReceiveResponse)
            );

            int statusCode = ReadStatusCode(request);
            string? retryAfter = ReadRetryAfter(request);
            byte[] body = ReadBoundedBody(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new ClaudeOAuthHttpResponse(statusCode, retryAfter, body);
        } finally {
            requestState.CloseRequest();
            if (connection != nint.Zero) {
                WinHttpNative.WinHttpCloseHandle(connection);
            }

            WinHttpNative.WinHttpCloseHandle(session);
        }
    }

    private static int ReadStatusCode(nint request) {
        uint statusCode = 0;
        uint bufferLength = sizeof(uint);
        EnsureWinHttpCall(
            WinHttpNative.WinHttpQueryHeaders(
                request,
                WinHttpQueryStatusCode | WinHttpQueryFlagNumber,
                null,
                ref statusCode,
                ref bufferLength,
                nint.Zero
            ),
            nameof(WinHttpNative.WinHttpQueryHeaders)
        );

        return checked((int)statusCode);
    }

    private static string? ReadRetryAfter(nint request) {
        char[] buffer = new char[256];
        uint bufferLength = checked((uint)(buffer.Length * sizeof(char)));
        if (!WinHttpNative.WinHttpQueryStringHeader(
            request,
            WinHttpQueryRetryAfter,
            null,
            buffer,
            ref bufferLength,
            nint.Zero
        )) {
            int errorCode = Marshal.GetLastWin32Error();
            if (errorCode is ErrorInsufficientBuffer or ErrorWinHttpHeaderNotFound) {
                return null;
            }

            return null;
        }

        int terminator = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, terminator < 0 ? buffer.Length : terminator);
    }

    private static byte[] ReadBoundedBody(nint request, CancellationToken cancellationToken) {
        byte[] buffer = new byte[MaximumResponseBytes + 1];
        byte[] readBuffer = new byte[ReadBufferBytes];
        try {
            int bytesReadTotal = 0;
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                int bytesRemaining = buffer.Length - bytesReadTotal;
                int bytesToRead = Math.Min(readBuffer.Length, bytesRemaining);
                EnsureWinHttpCall(
                    WinHttpNative.WinHttpReadData(
                        request,
                        readBuffer,
                        checked((uint)bytesToRead),
                        out uint bytesRead
                    ),
                    nameof(WinHttpNative.WinHttpReadData)
                );

                if (bytesRead == 0) {
                    return buffer.AsSpan(0, bytesReadTotal).ToArray();
                }

                readBuffer.AsSpan(0, checked((int)bytesRead)).CopyTo(buffer.AsSpan(bytesReadTotal));
                bytesReadTotal = checked(bytesReadTotal + (int)bytesRead);
                if (bytesReadTotal > MaximumResponseBytes) {
                    throw new InvalidDataException("The Claude API response exceeded the 1 MiB limit.");
                }
            }
        } finally {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(readBuffer);
        }
    }

    private static string GetPath(ClaudeOAuthEndpoint endpoint) => endpoint switch {
        ClaudeOAuthEndpoint.Profile => "/api/oauth/profile",
        ClaudeOAuthEndpoint.Usage => "/api/oauth/usage?cedar_ember=1&skip_spend=1",
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
    };

    private static void ValidateAccessToken(string accessToken) {
        if (string.IsNullOrWhiteSpace(accessToken)) {
            throw new ArgumentException("The Claude access token is required.", nameof(accessToken));
        }

        foreach (char character in accessToken) {
            if (character < '!' || character > '~') {
                throw new ArgumentException("The Claude access token contains an invalid HTTP header character.", nameof(accessToken));
            }
        }
    }

    private static void EnsureWinHttpCall(bool succeeded, string apiName) {
        if (!succeeded) {
            ThrowLastWinHttpError(apiName);
        }
    }

    private static void ThrowLastWinHttpError(string apiName) {
        int errorCode = Marshal.GetLastWin32Error();
        throw new Win32Exception(errorCode, $"{apiName} failed while calling the Claude API.");
    }

    private sealed class RequestHandleState {
        private nint requestHandle;

        internal void SetRequest(nint request) {
            Interlocked.Exchange(ref requestHandle, request);
        }

        internal void CloseRequest() {
            nint request = Interlocked.Exchange(ref requestHandle, nint.Zero);
            if (request != nint.Zero) {
                WinHttpNative.WinHttpCloseHandle(request);
            }
        }
    }

    private static class WinHttpNative {
        [DllImport("winhttp.dll", EntryPoint = "WinHttpSetTimeouts", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpSetTimeouts(
            nint session,
            int resolveTimeout,
            int connectTimeout,
            int sendTimeout,
            int receiveTimeout
        );

        [DllImport("winhttp.dll", EntryPoint = "WinHttpOpen", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint WinHttpOpen(
            string userAgent,
            uint accessType,
            string? proxyName,
            string? proxyBypass,
            uint flags
        );

        [DllImport("winhttp.dll", EntryPoint = "WinHttpSetOption", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpSetOption(nint handle, uint option, ref uint buffer, uint bufferLength);

        [DllImport("winhttp.dll", EntryPoint = "WinHttpConnect", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint WinHttpConnect(nint session, string serverName, ushort serverPort, uint reserved);

        [DllImport("winhttp.dll", EntryPoint = "WinHttpOpenRequest", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint WinHttpOpenRequest(
            nint connection,
            string verb,
            string objectName,
            string? version,
            string? referrer,
            nint acceptTypes,
            uint flags
        );

        [DllImport("winhttp.dll", EntryPoint = "WinHttpAddRequestHeaders", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpAddRequestHeaders(
            nint request,
            string headers,
            uint headersLength,
            uint modifiers
        );

        [DllImport("winhttp.dll", EntryPoint = "WinHttpSendRequest", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpSendRequest(
            nint request,
            string? headers,
            uint headersLength,
            nint optional,
            uint optionalLength,
            uint totalLength,
            nuint context
        );

        [DllImport("winhttp.dll", EntryPoint = "WinHttpReceiveResponse", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpReceiveResponse(nint request, nint reserved);

        [DllImport("winhttp.dll", EntryPoint = "WinHttpQueryHeaders", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpQueryHeaders(
            nint request,
            uint infoLevel,
            string? name,
            ref uint buffer,
            ref uint bufferLength,
            nint index
        );

        [DllImport("winhttp.dll", EntryPoint = "WinHttpQueryHeaders", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpQueryStringHeader(
            nint request,
            uint infoLevel,
            string? name,
            [Out] char[] buffer,
            ref uint bufferLength,
            nint index
        );

        [DllImport("winhttp.dll", EntryPoint = "WinHttpReadData", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpReadData(
            nint request,
            [Out] byte[] buffer,
            uint bytesToRead,
            out uint bytesRead
        );

        [DllImport("winhttp.dll", EntryPoint = "WinHttpCloseHandle", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpCloseHandle(nint handle);
    }
}
