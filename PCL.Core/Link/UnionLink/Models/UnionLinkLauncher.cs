using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCL.Core.Link.UnionLink.Models;

/// <summary>
/// 服务器当前发布的下载包描述。文档中 v1 与 v2 共用该结构，v2 额外带有 runtime。
/// </summary>
public sealed class UnionLinkPackageDownload
{
    [JsonPropertyName("url"), JsonRequired]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("etag")]
    public string? Etag { get; set; }

    [JsonPropertyName("sha256"), JsonRequired]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size"), JsonRequired]
    public long Size { get; set; }

    [JsonPropertyName("refreshUrl")]
    public string RefreshUrl { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("releaseId"), JsonRequired]
    public Guid ReleaseId { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("targetManifestSha256")]
    public string TargetManifestSha256 { get; set; } = string.Empty;

    [JsonPropertyName("signingKeyId")]
    public string SigningKeyId { get; set; } = string.Empty;

    /// <summary>同目标的完整包描述；即使当前返回的是更新包也会提供。</summary>
    [JsonPropertyName("full")]
    public UnionLinkPackageFile? Full { get; set; }

    [JsonPropertyName("packageType")]
    public string PackageType { get; set; } = string.Empty;

    [JsonPropertyName("baseReleaseId")]
    public Guid? BaseReleaseId { get; set; }

    [JsonPropertyName("baseManifestSha256")]
    public string? BaseManifestSha256 { get; set; }

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("minimumLauncherProtocol")]
    public int? MinimumLauncherProtocol { get; set; }

    [JsonPropertyName("protocolRevision")]
    public int? ProtocolRevision { get; set; }

    [JsonPropertyName("runtime")]
    public UnionLinkRuntime? Runtime { get; set; }

    [JsonPropertyName("mods")]
    public List<UnionLinkPackageMod>? Mods { get; set; }
}

/// <summary>完整包下载地址与校验信息。</summary>
public sealed class UnionLinkPackageFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("etag")]
    public string? Etag { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("refreshUrl")]
    public string RefreshUrl { get; set; } = string.Empty;
}

public sealed class UnionLinkRuntime
{
    [JsonPropertyName("minecraft")]
    public string Minecraft { get; set; } = string.Empty;

    [JsonPropertyName("loader")]
    public UnionLinkRuntimeLoader? Loader { get; set; }

    [JsonPropertyName("java")]
    public UnionLinkRuntimeJava? Java { get; set; }

    [JsonPropertyName("resolverVersion")]
    public string? ResolverVersion { get; set; }
}

public sealed class UnionLinkRuntimeLoader
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
}

public sealed class UnionLinkRuntimeJava
{
    [JsonPropertyName("major")]
    public int Major { get; set; }

    [JsonPropertyName("vendor")]
    public string Vendor { get; set; } = string.Empty;

    [JsonPropertyName("imageType")]
    public string ImageType { get; set; } = string.Empty;
}

public sealed class UnionLinkPackageMod
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("optional")]
    public bool Optional { get; set; }

    /// <summary>v2 中的文件来源（local / remote）。</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("hashes")]
    public UnionLinkPackageHashes? Hashes { get; set; }
}

public sealed class UnionLinkPackageHashes
{
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("sha512")]
    public string? Sha512 { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}

/// <summary>
/// 原始发布清单响应。文档要求保留响应字节原样用于验签，不能重新序列化。
/// </summary>
public sealed record UnionLinkManifestResponse(
    byte[] Content,
    string? ContentSha256,
    string? SigningKeyId,
    string? Signature,
    string? SignatureAlgorithm);
