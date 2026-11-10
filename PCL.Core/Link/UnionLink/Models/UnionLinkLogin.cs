using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCL.Core.Link.UnionLink.Models;

/// <summary>
/// 浏览器登录的启动结果。
/// </summary>
internal sealed record UnionLinkLoginResponse(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string LoginUrl,
    [property: JsonRequired] DateTimeOffset ExpiresAt);

/// <summary>
/// Union Link 已核准的认证来源。
/// </summary>
public sealed record UnionLinkProvider(
    [property: JsonRequired] Guid Id,
    string SiteCode,
    string DisplayName,
    string BsRoot,
    string AuthApiRoot,
    int Revision,
    bool DirectExchange,
    bool BrowserLogin);

/// <summary>
/// 启动器会话。持有期间可直接访问 Union Link 的受保护接口。
/// </summary>
public sealed class UnionLinkSession
{
    internal UnionLinkSession(string token, DateTimeOffset expiresAt)
    {
        Token = token;
        ExpiresAt = expiresAt;
    }

    public string Token { get; }
    public DateTimeOffset ExpiresAt { get; }
}

/// <summary>
/// 浏览器登录的领取结果。<see cref="Pending"/> 缺失时视为无效响应，不得据此创建会话。
/// </summary>
internal sealed record UnionLinkClaimResponse(
    bool? Pending,
    string? Token,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// 游戏令牌换票的启动结果。
/// </summary>
internal sealed record UnionLinkGameAttempt(
    [property: JsonRequired] Guid AttemptId,
    [property: JsonRequired] string Nonce,
    [property: JsonRequired] int ExpiresIn,
    [property: JsonRequired] Guid ProviderId,
    [property: JsonRequired] int ProviderRevision);

internal sealed record UnionLinkGameProfile(string? Id);

/// <summary>
/// 游戏令牌换票的兑换结果。
/// </summary>
internal sealed record UnionLinkGameLoginResponse(
    string? TokenType,
    string? Token,
    DateTimeOffset ExpiresAt,
    int ExpiresIn,
    Guid ProviderId,
    UnionLinkGameProfile? GameProfile);

internal sealed record UnionLinkLogoutResponse(bool? Ok);

internal sealed record UnionLinkServerDirectory(
    [property: JsonPropertyName("servers"), JsonRequired]
    List<UnionLinkMemberServer> Servers);
