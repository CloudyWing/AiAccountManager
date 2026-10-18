using System.Security.Cryptography;
using System.Text;

namespace CloudyWing.AiAccountManager.Windows.Security;

internal sealed class DpapiSecretProtector {
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AiAccountManager/identity-key/v1");

    public byte[] Protect(byte[] secret) {
        return ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedSecret) {
        return ProtectedData.Unprotect(protectedSecret, Entropy, DataProtectionScope.CurrentUser);
    }
}
