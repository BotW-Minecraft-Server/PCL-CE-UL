using System;
using System.Text.Json.Serialization;

namespace PCL.Core.Link.UnionLink.Models;

/// <summary>
/// Union Link 服务器目录中的成员服务器。
/// </summary>
public sealed class UnionLinkMemberServer
{
    [JsonPropertyName("currentRelease")]
    public UnionLinkCurrentRelease? CurrentRelease { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("displayEndsAt")]
    public DateTimeOffset? DisplayEndsAt { get; set; }

    [JsonPropertyName("displayMode")]
    public UnionLinkDisplayMode DisplayMode { get; set; }

    [JsonPropertyName("displayName"), JsonRequired]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("displayStartsAt")]
    public DateTimeOffset? DisplayStartsAt { get; set; }

    [JsonPropertyName("gameAddress")]
    public string? GameAddress { get; set; }

    [JsonPropertyName("organization")]
    public UnionLinkOrganization? Organization { get; set; }

    [JsonPropertyName("publicationRevision")]
    public long PublicationRevision { get; set; }

    [JsonPropertyName("serverId"), JsonRequired]
    public string ServerId { get; set; } = string.Empty;

    [JsonPropertyName("sortOrder")]
    public long SortOrder { get; set; }

    [JsonPropertyName("themeColor")]
    public string? ThemeColor { get; set; }
}

/// <summary>
/// 服务器当前发布的客户端整合包。
/// </summary>
public sealed class UnionLinkCurrentRelease
{
    [JsonPropertyName("clientId")]
    public Guid ClientId { get; set; }

    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("manifestSha256")]
    public string ManifestSha256 { get; set; } = string.Empty;

    [JsonPropertyName("manifestUrl")]
    public string ManifestUrl { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("packageFormatVersion")]
    public double PackageFormatVersion { get; set; }

    [JsonPropertyName("packagesUrl")]
    public string PackagesUrl { get; set; } = string.Empty;

    [JsonPropertyName("versionLabel")]
    public string VersionLabel { get; set; } = string.Empty;
}

public sealed class UnionLinkOrganization
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public Guid Id { get; set; }
}

public enum UnionLinkDisplayMode
{
    [JsonStringEnumMemberName("PERMANENT")]
    Permanent,

    [JsonStringEnumMemberName("RANGE")]
    Range
}
