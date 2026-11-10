using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Utils.Exts;

namespace PCL.Core.Utils.Hash;

/// <summary>
/// PKCE S256 哈希实现。<br/>
/// S256 的 code_challenge 定义为 <c>BASE64URL(SHA256(ASCII(code_verifier)))</c>。<br/>
/// 为保持与其它 <see cref="IHashProvider"/> 实现一致的契约，
/// <see cref="ComputeHash(ReadOnlySpan{byte})"/> 返回原始 SHA-256 摘要；
/// 需要直接得到 Base64URL 编码的挑战串时请使用 <see cref="ComputeCodeChallenge(ReadOnlySpan{byte})"/>。
/// </summary>
public class S256Provider : IHashProvider
{
    public static S256Provider Instance { get; } = new();

    /// <summary>
    /// SHA-256 摘要的十六进制表示长度。
    /// </summary>
    public int Length => 64;

    public byte[] ComputeHash(ReadOnlySpan<byte> input)
    {
        return SHA256.HashData(input);
    }

    public byte[] ComputeHash(string input, Encoding? en = null)
    {
        en ??= Encoding.UTF8;
        return ComputeHash(en.GetBytes(input));
    }

    public byte[] ComputeHash(Stream input)
    {
        return SHA256.HashData(input);
    }

    public async ValueTask<byte[]> ComputeHashAsync(Stream input, CancellationToken cancellationToken = default)
    {
        return await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 生成 PKCE 的 code_verifier：32 字节随机数的 BASE64URL 编码（不含填充，共 43 个字符）。
    /// </summary>
    public static string GenerateCodeVerifier()
    {
        return RandomNumberGenerator.GetBytes(32).FromBytesToB64UrlSafe().TrimEnd('=');
    }

    /// <summary>
    /// 计算 PKCE S256 的 code_challenge：<c>BASE64URL(SHA256(input))</c>（不含填充）。
    /// </summary>
    public string ComputeCodeChallenge(ReadOnlySpan<byte> input)
    {
        // PKCE 要求 base64url 不带填充（32 字节摘要为 43 字符），
        // 而 FromBytesToB64UrlSafe 仅替换字母表、不裁剪 '='，故需显式去除。
        return ComputeHash(input).FromBytesToB64UrlSafe().TrimEnd('=');
    }

    /// <summary>
    /// 计算 PKCE S256 的 code_challenge：<c>BASE64URL(SHA256(ASCII(codeVerifier)))</c>（不含填充）。
    /// </summary>
    public string ComputeCodeChallenge(string codeVerifier, Encoding? en = null)
    {
        en ??= Encoding.UTF8;
        return ComputeCodeChallenge(en.GetBytes(codeVerifier));
    }
}
