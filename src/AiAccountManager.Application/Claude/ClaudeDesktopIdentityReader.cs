using System.Security.Cryptography;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Security;

namespace CloudyWing.AiAccountManager.Application.Claude;

internal sealed class ClaudeDesktopIdentityReader {
    private readonly IClaudeDesktopProfileStore profileStore;
    private readonly IClaudeOAuthClient oauthClient;
    private readonly IIdentityFingerprinter identityFingerprinter;
    private readonly IClaudeAccountRepository accountRepository;

    public ClaudeDesktopIdentityReader(
        IClaudeDesktopProfileStore profileStore,
        IClaudeOAuthClient oauthClient,
        IIdentityFingerprinter identityFingerprinter,
        IClaudeAccountRepository accountRepository
    ) {
        this.profileStore = profileStore;
        this.oauthClient = oauthClient;
        this.identityFingerprinter = identityFingerprinter;
        this.accountRepository = accountRepository;
    }

    public async Task<Result> ReadAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        IReadOnlyList<ClaudeAccountProfile> profiles,
        CancellationToken cancellationToken
    ) {
        IClaudeDesktopProfileStore.ReadResult readResult = await profileStore
            .ReadCurrentAsync(installation, cancellationToken)
            .ConfigureAwait(false);

        Result CreateResult(
            ClaudeAccountService.IdentityStatus status,
            string? accountUuid,
            string? organizationUuid,
            string? maskedEmail,
            string? message,
            IClaudeDesktopProfileStore.TokenCandidate? verifiedToken = null,
            ClaudeAccountProfile? matchedProfile = null
        ) {
            return new(
                status,
                accountUuid,
                organizationUuid,
                maskedEmail,
                message,
                readResult.LastKnownAccountUuid,
                readResult.TokenCandidates,
                verifiedToken,
                matchedProfile
            );
        }

        if (string.IsNullOrWhiteSpace(readResult.LastKnownAccountUuid)) {
            return CreateResult(
                ClaudeAccountService.IdentityStatus.Mismatch,
                null,
                null,
                null,
                "Claude Desktop 設定沒有可核對的帳號 UUID。"
            );
        }

        if (readResult.TokenCandidates.Count == 0) {
            return CreateResult(
                ClaudeAccountService.IdentityStatus.ReauthenticationRequired,
                null,
                null,
                null,
                "請在 Claude Desktop 重新登入。"
            );
        }

        bool receivedUnauthorized = false;
        bool receivedForbidden = false;
        bool receivedApiError = false;
        bool receivedIdentityMismatch = false;
        Dictionary<string, VerifiedCandidate> verifiedCandidates = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> evaluatedTokens = new(StringComparer.Ordinal);
        foreach (IClaudeDesktopProfileStore.TokenCandidate tokenCandidate in readResult.TokenCandidates) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!evaluatedTokens.Add(tokenCandidate.AccessToken)) {
                continue;
            }

            IClaudeOAuthClient.ProfileResult profileResult;
            try {
                profileResult = await oauthClient
                    .GetProfileAsync(tokenCandidate.AccessToken, cancellationToken)
                    .ConfigureAwait(false);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception) {
                receivedApiError = true;
                continue;
            }

            switch (profileResult.Outcome) {
                case IClaudeOAuthClient.Outcome.Unauthorized:
                    receivedUnauthorized = true;
                    continue;
                case IClaudeOAuthClient.Outcome.Forbidden:
                    receivedForbidden = true;
                    continue;
                case IClaudeOAuthClient.Outcome.Error:
                case IClaudeOAuthClient.Outcome.RateLimited:
                    receivedApiError = true;
                    continue;
                case IClaudeOAuthClient.Outcome.Success:
                    break;
                default:
                    receivedApiError = true;
                    continue;
            }

            if (string.IsNullOrWhiteSpace(profileResult.AccountUuid)
                || string.IsNullOrWhiteSpace(profileResult.OrganizationUuid)) {
                receivedApiError = true;
                continue;
            }

            if (!string.Equals(
                readResult.LastKnownAccountUuid,
                profileResult.AccountUuid,
                StringComparison.OrdinalIgnoreCase
            )) {
                receivedIdentityMismatch = true;
                continue;
            }

            string identityKey = string.Concat(profileResult.AccountUuid, "|", profileResult.OrganizationUuid);
            if (!verifiedCandidates.ContainsKey(identityKey)) {
                ClaudeAccountProfile? matchedProfile = await MatchProfileAsync(
                    installation,
                    profiles,
                    profileResult.AccountUuid,
                    profileResult.OrganizationUuid,
                    cancellationToken
                ).ConfigureAwait(false);
                verifiedCandidates.Add(identityKey, new(
                    profileResult.AccountUuid,
                    profileResult.OrganizationUuid,
                    identityFingerprinter.MaskEmail(profileResult.Email),
                    tokenCandidate,
                    matchedProfile
                ));
            }
        }

        if (verifiedCandidates.Count == 1 && !receivedUnauthorized && !receivedForbidden && !receivedApiError) {
            VerifiedCandidate verified = verifiedCandidates.Values.First();
            return CreateResult(
                ClaudeAccountService.IdentityStatus.Verified,
                verified.AccountUuid,
                verified.OrganizationUuid,
                verified.MaskedEmail,
                null,
                verified.TokenCandidate,
                verified.MatchedProfile
            );
        }

        bool identityIsAmbiguous = verifiedCandidates.Count > 1
            || (verifiedCandidates.Count > 0 && (receivedUnauthorized || receivedForbidden || receivedApiError));
        if (identityIsAmbiguous) {
            return CreateResult(
                ClaudeAccountService.IdentityStatus.Unknown,
                null,
                null,
                null,
                "無法確認目前的 Claude 帳號，請在 Desktop 完成登入後重新讀取身分。"
            );
        }

        ClaudeAccountService.IdentityStatus status = receivedUnauthorized || receivedForbidden
            ? ClaudeAccountService.IdentityStatus.ReauthenticationRequired
            : receivedIdentityMismatch && !receivedApiError
                ? ClaudeAccountService.IdentityStatus.Mismatch
                : ClaudeAccountService.IdentityStatus.Unknown;
        string message = status switch {
            ClaudeAccountService.IdentityStatus.ReauthenticationRequired
                => "登入已失效，請在 Claude Desktop 重新登入。",
            ClaudeAccountService.IdentityStatus.Mismatch
                => "本機帳號與線上帳號資料不一致。",
            _ => "目前無法確認 Claude Desktop 帳號，請稍後再試。"
        };

        return CreateResult(status, null, null, null, message);
    }

    internal static bool HasMatchingBinding(
        ClaudeAccountProfile.DesktopSnapshot first,
        ClaudeAccountProfile.DesktopSnapshot second
    ) {
        return SnapshotHasTrustedProvenance(first)
            && SnapshotHasTrustedProvenance(second)
            && string.Equals(first.WindowsUserSid, second.WindowsUserSid, StringComparison.Ordinal)
            && string.Equals(first.InstallationId, second.InstallationId, StringComparison.Ordinal)
            && string.Equals(first.DataRootFingerprint, second.DataRootFingerprint, StringComparison.Ordinal)
            && string.Equals(
                first.LocalStateEncryptedKeySha256,
                second.LocalStateEncryptedKeySha256,
                StringComparison.Ordinal
            )
            && first.RootBinding is not null
            && second.RootBinding is not null
            && first.RootBinding.Source.BindingId == second.RootBinding.Source.BindingId
            && HasSameSource(first.RootBinding.Source, second.RootBinding.Source);
    }

    private async Task<ClaudeAccountProfile?> MatchProfileAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        IReadOnlyList<ClaudeAccountProfile> profiles,
        string accountUuid,
        string organizationUuid,
        CancellationToken cancellationToken
    ) {
        ClaudeDesktopRootBinding.Observation? source = installation.Binding?.Source ?? installation.Observation;
        if (source is null || source.SchemaVersion != 1 || source.BindingId == Guid.Empty) {
            return null;
        }

        ClaudeAccountProfile? match = null;
        foreach (ClaudeAccountProfile profile in profiles.Where(profile =>
            profile.ProvenanceSchemaVersion == 1
            && profile.BindingId is not null
            && string.Equals(profile.AccountUuid, accountUuid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.OrganizationUuid, organizationUuid, StringComparison.OrdinalIgnoreCase)
        )) {
            ClaudeAccountProfile.DesktopSnapshot? snapshot = null;
            try {
                snapshot = await accountRepository.LoadDesktopSnapshotAsync(profile.ProfileId, cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot is null || !SnapshotHasTrustedProvenance(snapshot)
                    || snapshot.RootBinding is not ClaudeDesktopRootBinding binding
                    || profile.BindingId != binding.Source.BindingId
                    || !HasSameSource(source, binding.Source)
                    || !string.Equals(profile.InstallationId, snapshot.InstallationId, StringComparison.Ordinal)
                    || !string.Equals(profile.WindowsUserSid, snapshot.WindowsUserSid, StringComparison.Ordinal)
                    || !string.Equals(profile.DataRootFingerprint, installation.DataRootFingerprint, StringComparison.Ordinal)
                    || !string.Equals(profile.DataRootFingerprint, snapshot.DataRootFingerprint, StringComparison.Ordinal)
                    || !string.Equals(profile.LocalStateEncryptedKeySha256, snapshot.LocalStateEncryptedKeySha256, StringComparison.Ordinal)
                    || (installation.Binding is ClaudeDesktopRootBinding resolvedBinding
                        && (resolvedBinding.Source.BindingId != binding.Source.BindingId
                            || !string.Equals(resolvedBinding.LocalStateEncryptedKeySha256,
                                binding.LocalStateEncryptedKeySha256, StringComparison.Ordinal)))) {
                    continue;
                }

                if (match is not null) {
                    return null;
                }

                match = profile;
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception) {
                return null;
            } finally {
                if (snapshot is not null) {
                    CryptographicOperations.ZeroMemory(snapshot.Cookies);
                    if (snapshot.CookieJournal is not null) {
                        CryptographicOperations.ZeroMemory(snapshot.CookieJournal);
                    }

                    if (snapshot.CookieWriteAheadLog is not null) {
                        CryptographicOperations.ZeroMemory(snapshot.CookieWriteAheadLog);
                    }

                    if (snapshot.CookieSharedMemory is not null) {
                        CryptographicOperations.ZeroMemory(snapshot.CookieSharedMemory);
                    }
                }
            }
        }

        return match;
    }

    private static bool SnapshotHasTrustedProvenance(ClaudeAccountProfile.DesktopSnapshot snapshot) {
        return snapshot.ProvenanceSchemaVersion == 1
            && snapshot.CookieSidecarSnapshotVersion == 1
            && snapshot.RootBinding is ClaudeDesktopRootBinding binding
            && binding.Source.SchemaVersion == 1
            && binding.Source.BindingId != Guid.Empty
            && string.Equals(snapshot.WindowsUserSid, binding.Source.WindowsUserSid, StringComparison.Ordinal)
            && string.Equals(snapshot.InstallationId, binding.Source.InstallationId, StringComparison.Ordinal)
            && string.Equals(snapshot.LocalStateEncryptedKeySha256, binding.LocalStateEncryptedKeySha256, StringComparison.Ordinal)
            && IsSha256(snapshot.LocalStateEncryptedKeySha256);
    }

    internal static bool HasSameSource(
        ClaudeDesktopRootBinding.Observation first,
        ClaudeDesktopRootBinding.Observation second
    ) {
        return first.SchemaVersion == 1 && second.SchemaVersion == 1
            && first.Kind == second.Kind
            && string.Equals(first.WindowsUserSid, second.WindowsUserSid, StringComparison.Ordinal)
            && string.Equals(first.InstallationId, second.InstallationId, StringComparison.Ordinal)
            && string.Equals(first.AppUserModelId, second.AppUserModelId, StringComparison.Ordinal)
            && string.Equals(first.PackageFamilyName, second.PackageFamilyName, StringComparison.OrdinalIgnoreCase)
            && (first.Kind == IClaudeDesktopInstallationLocator.InstallKind.Msix
                || string.Equals(first.PackageFullName, second.PackageFullName, StringComparison.OrdinalIgnoreCase))
            && string.Equals(first.DirectRegistrationSource, second.DirectRegistrationSource, StringComparison.Ordinal)
            && string.Equals(first.ExecutablePath, second.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.ExecutableSha256, second.ExecutableSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.DataRoot, second.DataRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.CookieRelativePath.Replace('/', '\\'), second.CookieRelativePath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)
            && first.RootVolumeSerialNumber == second.RootVolumeSerialNumber
            && string.Equals(first.RootFileId, second.RootFileId, StringComparison.Ordinal)
            && first.LayoutVersion == second.LayoutVersion;
    }

    internal static bool HasSameTokenCandidates(
        IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> first,
        IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> second
    ) {
        if (first.Count != second.Count) {
            return false;
        }

        IClaudeDesktopProfileStore.TokenCandidate[] orderedFirst = first
            .OrderBy(candidate => candidate.SourceField, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.AccessToken, StringComparer.Ordinal)
            .ToArray();
        IClaudeDesktopProfileStore.TokenCandidate[] orderedSecond = second
            .OrderBy(candidate => candidate.SourceField, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.AccessToken, StringComparer.Ordinal)
            .ToArray();

        return orderedFirst.Zip(orderedSecond).All(pair =>
            string.Equals(pair.First.SourceField, pair.Second.SourceField, StringComparison.Ordinal)
            && string.Equals(pair.First.AccessToken, pair.Second.AccessToken, StringComparison.Ordinal)
        );
    }

    private static bool IsSha256(string? value) {
        if (value is null || value.Length != 64) {
            return false;
        }

        foreach (char character in value) {
            if (!char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)) {
                return false;
            }
        }

        return true;
    }

    internal sealed record Result(
        ClaudeAccountService.IdentityStatus Status,
        string? AccountUuid,
        string? OrganizationUuid,
        string? MaskedEmail,
        string? Message,
        string? LastKnownAccountUuid,
        IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> TokenCandidates,
        IClaudeDesktopProfileStore.TokenCandidate? VerifiedToken = null,
        ClaudeAccountProfile? MatchedProfile = null
    );

    private sealed record VerifiedCandidate(
        string AccountUuid,
        string OrganizationUuid,
        string? MaskedEmail,
        IClaudeDesktopProfileStore.TokenCandidate TokenCandidate,
        ClaudeAccountProfile? MatchedProfile
    );
}
