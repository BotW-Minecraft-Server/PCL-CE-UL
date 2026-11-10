using System;

namespace PCL.Core.Utils.Signature;

/// <summary>
/// Ed25519 签名验证提供方。
/// </summary>
public interface ISignatureProvider
{
    /// <summary>验证签名。</summary>
    bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey);

    /// <summary>按文档计算公钥 ID：SHA-256(SPKI DER) 前 24 位小写十六进制。</summary>
    string ComputeKeyId(ReadOnlySpan<byte> publicKey);

    /// <summary>解析 Base64 / PEM 形式的 SPKI DER 公钥。</summary>
    byte[] DecodePublicKey(string value);
}
