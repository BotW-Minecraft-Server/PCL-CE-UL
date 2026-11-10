using System;
using System.Linq;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace PCL.Core.Utils.Signature;

/// <summary>
/// 基于 BouncyCastle 的 Ed25519 签名验证实现。
/// </summary>
public sealed class Ed25519Provider : ISignatureProvider
{
    public static Ed25519Provider Instance { get; } = new();

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
    {
        var key = PublicKeyFactory.CreateKey(publicKey.ToArray()) as Ed25519PublicKeyParameters
                  ?? throw new ArgumentException("不是有效的 Ed25519 公钥。", nameof(publicKey));
        var verifier = new Ed25519Signer();
        verifier.Init(false, key);
        var buffer = data.ToArray();
        verifier.BlockUpdate(buffer, 0, buffer.Length);
        return verifier.VerifySignature(signature.ToArray());
    }

    public string ComputeKeyId(ReadOnlySpan<byte> publicKey)
    {
        return Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant()[..24];
    }

    public byte[] DecodePublicKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var text = value
            .Replace("-----BEGIN PUBLIC KEY-----", string.Empty, StringComparison.Ordinal)
            .Replace("-----END PUBLIC KEY-----", string.Empty, StringComparison.Ordinal);
        text = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        return Convert.FromBase64String(text);
    }
}
