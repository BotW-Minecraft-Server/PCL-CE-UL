using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.Logging;
using PCL.Core.Utils;
using PCL.Core.Utils.Signature;

namespace PCL.Core.Link.UnionLink.MuaPack;

/// <summary>
/// muapack v2 的 Ed25519 签名校验。
/// </summary>
public static class MuaPackSignature
{
    // 由维护者填写：Ed25519 公钥 SPKI DER 的 Base64（可带 PEM 头尾）。
    // 留空时跳过验签并记录警告；填写后强制校验。
    public const string PublicKeySpkiBase64 = "MCowBQYDK2VwAyEAgjTJIIokLuWxYYVMl2QufQe5A/NzTdBH2Of2VDffB0w=";

    private const string SignedPrefix = "MUA-MUAPACK-V2";

    /// <summary>
    /// 校验 metadata/signature.json 与 mua-pack.json 的原始字节。
    /// </summary>
    public static void Verify(byte[] muaPackBytes, byte[] signatureBytes)
    {
        ArgumentNullException.ThrowIfNull(muaPackBytes);
        ArgumentNullException.ThrowIfNull(signatureBytes);

        if (string.IsNullOrWhiteSpace(PublicKeySpkiBase64))
        {
            LogWrapper.Warn("MuaPack", "未配置 muapack 公钥，已跳过签名校验");
            return;
        }

        if (JsonCompat.ParseNode(Encoding.UTF8.GetString(signatureBytes)) is not JsonObject signature)
        {
            throw new Exception("signature.json 格式无效。");
        }

        if (!string.Equals(signature["format"]?.ToString(), "mua-pack-signature", StringComparison.Ordinal)
            || signature["formatVersion"]?.GetValue<int>() != 2
            || !string.Equals(signature["algorithm"]?.ToString(), "Ed25519", StringComparison.Ordinal))
        {
            throw new Exception("muapack 签名文件格式无效。");
        }

        var publicKey = Ed25519Provider.Instance.DecodePublicKey(PublicKeySpkiBase64);
        var keyId = Ed25519Provider.Instance.ComputeKeyId(publicKey);
        if (!string.Equals(signature["keyId"]?.ToString(), keyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception("muapack 签名公钥 ID 不匹配。");
        }

        var metadataHash = Convert.ToHexString(SHA256.HashData(muaPackBytes)).ToLowerInvariant();
        if (!string.Equals(signature["metadataSha256"]?.ToString(), metadataHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception("mua-pack.json 摘要与签名不一致。");
        }

        var signed = new byte[SignedPrefix.Length + 1 + muaPackBytes.Length];
        Encoding.ASCII.GetBytes(SignedPrefix, 0, SignedPrefix.Length, signed, 0);
        signed[SignedPrefix.Length] = 0;
        Buffer.BlockCopy(muaPackBytes, 0, signed, SignedPrefix.Length + 1, muaPackBytes.Length);

        var signatureValue = Convert.FromBase64String(signature["signature"]?.ToString() ?? string.Empty);
        if (!Ed25519Provider.Instance.Verify(signed, signatureValue, publicKey))
        {
            throw new Exception("muapack 签名校验失败。");
        }

        LogWrapper.Info("MuaPack", "muapack 签名校验通过");
    }
}
