using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Claude;

namespace CloudyWing.AiAccountManager.Windows.Claude;

/// <summary>
/// 使用固定 Claude OAuth HTTPS endpoints 驗證帳號與查詢額度。
/// </summary>
public sealed class WindowsClaudeOAuthClient : IClaudeOAuthClient {
    private readonly Func<ClaudeOAuthEndpoint, string, CancellationToken, Task<ClaudeOAuthHttpResponse>> sendRequest;

    /// <summary>
    /// 建立使用 Windows WinHTTP 傳輸的 Claude OAuth client。
    /// </summary>
    public WindowsClaudeOAuthClient()
        : this(CreateRequestDelegate(new WindowsClaudeWinHttpTransport())) {
    }

    internal WindowsClaudeOAuthClient(
        Func<ClaudeOAuthEndpoint, string, CancellationToken, Task<ClaudeOAuthHttpResponse>> sendRequest
    ) {
        this.sendRequest = sendRequest ?? throw new ArgumentNullException(nameof(sendRequest));
    }

    /// <inheritdoc />
    public async Task<IClaudeOAuthClient.ProfileResult> GetProfileAsync(
        string bearerToken,
        CancellationToken cancellationToken
    ) {
        try {
            ClaudeOAuthHttpResponse response = await sendRequest(
                ClaudeOAuthEndpoint.Profile,
                bearerToken,
                cancellationToken
            ).ConfigureAwait(false);
            try {
                TimeSpan? retryAfter = response.StatusCode == 429 ? ParseRetryAfter(response.RetryAfter) : null;
                if (response.StatusCode != 200) {
                    return new IClaudeOAuthClient.ProfileResult(
                        GetOutcome(response.StatusCode),
                        null,
                        null,
                        null,
                        retryAfter,
                        GetFailureCode(response.StatusCode)
                    );
                }

                return ParseProfileResponse(response.Body);
            } finally {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(response.Body);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (TimeoutException) {
            return ProfileFailure("timeout");
        } catch (Win32Exception) {
            return ProfileFailure("transport-error");
        } catch (IOException) {
            return ProfileFailure("transport-error");
        } catch (InvalidDataException) {
            return ProfileFailure("response-too-large");
        } catch (ArgumentException) {
            return ProfileFailure("invalid-token");
        }
    }

    /// <inheritdoc />
    public async Task<IClaudeOAuthClient.UsageResult> GetUsageAsync(
        string bearerToken,
        CancellationToken cancellationToken
    ) {
        try {
            ClaudeOAuthHttpResponse response = await sendRequest(
                ClaudeOAuthEndpoint.Usage,
                bearerToken,
                cancellationToken
            ).ConfigureAwait(false);
            try {
                TimeSpan? retryAfter = response.StatusCode == 429 ? ParseRetryAfter(response.RetryAfter) : null;
                if (response.StatusCode != 200) {
                    return new IClaudeOAuthClient.UsageResult(
                        GetOutcome(response.StatusCode),
                        null,
                        retryAfter,
                        GetFailureCode(response.StatusCode)
                    );
                }

                return ParseUsageResponse(response.Body);
            } finally {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(response.Body);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (TimeoutException) {
            return UsageFailure("timeout");
        } catch (Win32Exception) {
            return UsageFailure("transport-error");
        } catch (IOException) {
            return UsageFailure("transport-error");
        } catch (InvalidDataException) {
            return UsageFailure("response-too-large");
        } catch (ArgumentException) {
            return UsageFailure("invalid-token");
        }
    }

    internal static IClaudeOAuthClient.ProfileResult ParseProfileResponse(byte[] body) {
        try {
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("account", out JsonElement account)
                || account.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("organization", out JsonElement organization)
                || organization.ValueKind != JsonValueKind.Object
                || !TryGetUuid(account, "uuid", out string? accountUuid)
                || !TryGetUuid(organization, "uuid", out string? organizationUuid)) {
                return ProfileFailure("invalid-profile-response");
            }

            string? email = GetOptionalString(account, "email_address")
                ?? GetOptionalString(account, "email");
            return new IClaudeOAuthClient.ProfileResult(
                IClaudeOAuthClient.Outcome.Success,
                accountUuid,
                organizationUuid,
                email,
                null,
                null
            );
        } catch (JsonException) {
            return ProfileFailure("invalid-profile-response");
        }
    }

    internal static IClaudeOAuthClient.UsageResult ParseUsageResponse(byte[] body) {
        try {
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                return UsageFailure("invalid-usage-response");
            }

            return new IClaudeOAuthClient.UsageResult(
                IClaudeOAuthClient.Outcome.Success,
                document.RootElement.Clone(),
                null,
                null
            );
        } catch (JsonException) {
            return UsageFailure("invalid-usage-response");
        }
    }

    internal static TimeSpan? ParseRetryAfter(string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return null;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            && seconds >= 0) {
            return TimeSpan.FromSeconds(seconds);
        }

        if (!DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset retryAt
        )) {
            return null;
        }

        TimeSpan remaining = retryAt - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static Func<ClaudeOAuthEndpoint, string, CancellationToken, Task<ClaudeOAuthHttpResponse>> CreateRequestDelegate(
        WindowsClaudeWinHttpTransport transport
    ) {
        ArgumentNullException.ThrowIfNull(transport);
        return (endpoint, token, cancellationToken) => endpoint switch {
            ClaudeOAuthEndpoint.Profile => transport.GetProfileAsync(token, cancellationToken),
            ClaudeOAuthEndpoint.Usage => transport.GetUsageAsync(token, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };
    }

    private static IClaudeOAuthClient.Outcome GetOutcome(int statusCode) {
        return statusCode switch {
            401 => IClaudeOAuthClient.Outcome.Unauthorized,
            403 => IClaudeOAuthClient.Outcome.Forbidden,
            429 => IClaudeOAuthClient.Outcome.RateLimited,
            _ => IClaudeOAuthClient.Outcome.Error
        };
    }

    private static string GetFailureCode(int statusCode) {
        return statusCode switch {
            401 => "unauthorized",
            403 => "forbidden",
            429 => "rate-limited",
            _ => "http-error"
        };
    }

    private static bool TryGetUuid(JsonElement element, string propertyName, out string? value) {
        value = null;
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String) {
            return false;
        }

        string? candidate = property.GetString();
        if (candidate is null || !Guid.TryParse(candidate, out Guid parsed) || parsed == Guid.Empty) {
            return false;
        }

        value = parsed.ToString("D");
        return true;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) {
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static IClaudeOAuthClient.ProfileResult ProfileFailure(string failureCode) {
        return new IClaudeOAuthClient.ProfileResult(
            IClaudeOAuthClient.Outcome.Error,
            null,
            null,
            null,
            null,
            failureCode
        );
    }

    private static IClaudeOAuthClient.UsageResult UsageFailure(string failureCode) {
        return new IClaudeOAuthClient.UsageResult(
            IClaudeOAuthClient.Outcome.Error,
            null,
            null,
            failureCode
        );
    }
}
