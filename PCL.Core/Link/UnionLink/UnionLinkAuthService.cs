using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.Http;
using PCL.Core.Link.UnionLink.Models;
using PCL.Core.Logging;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.Utils;
using PCL.Core.Utils.Hash;

namespace PCL.Core.Link.UnionLink;

/// <summary>
///     Union Link（MC高校联盟）启动器接口的底层实现。
///     <para>
///         支持的流程：浏览器登录（暂时不对接）、令牌、退出登录、获取服务器目录。
///         会话保存在内存中，成功登录后会同步写入本地配置以便重启后继续使用。
///     </para>
/// </summary>
public sealed class UnionLinkAuthService
{
    /// <summary>Union Link 服务根地址。</summary>
    public const string LinkBase = "https://link.mualliance.cn";

    private const string LauncherPath = "/api/auth/launcher";
    private const string LogModule = "UnionLink";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>进程内共享实例。</summary>
    public static UnionLinkAuthService Shared { get; } = new();

    private readonly Func<HttpClient> _getClient;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private UnionLinkSession? _session;
    private int _restored;

    internal UnionLinkAuthService(Func<HttpClient>? getClient = null, TimeProvider? clock = null)
    {
        _getClient = getClient ?? (() => NetworkService.GetClient(NetworkService.UnionLink));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    ///     当前仍然有效的会话；已过期或未登录时返回 <see langword="null" />。
    /// </summary>
    public UnionLinkSession? CurrentSession
    {
        get
        {
            var session = Volatile.Read(ref _session);
            if (session is null && Interlocked.Exchange(ref _restored, 1) == 0)
            {
                session = _RestorePersistedSession();
                if (session is not null)
                {
                    Volatile.Write(ref _session, session);
                }
            }

            return session is not null && session.ExpiresAt > _clock.GetUtcNow() ? session : null;
        }
    }

    /// <summary>
    ///     最近一次请求失败时收到的原始响应正文（没有正文时为 <see langword="null" />）。
    ///     成功请求不会清空该值，便于断点调试时通过 <c>UnionLinkAuthService.Shared.LastErrorResponseBody</c> 查看。
    /// </summary>
    public string? LastErrorResponseBody { get; private set; }

    #region 浏览器登录

    /// <summary>
    ///     发起浏览器登录：打开授权页面后轮询领取启动器令牌。
    /// </summary>
    /// <param name="openBrowser">打开浏览器的回调，返回 <see langword="false" /> 表示无法打开。</param>
    /// <param name="token">取消令牌。</param>
    public async Task<Result<UnionLinkSession, UnionLinkError>> LauncherLoginAsync(
        Func<Uri, CancellationToken, Task<bool>> openBrowser, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        await _sessionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var verifier = S256Provider.GenerateCodeVerifier();
            using var startRequest = HttpRequest.CreatePost(LinkBase + LauncherPath + "/start")
                .WithJsonContent(new { challenge = S256Provider.Instance.ComputeCodeChallenge(verifier) });
            var started = await SendAsync<UnionLinkLoginResponse>(startRequest, token).ConfigureAwait(false);
            if (started.IsFailure)
            {
                return Fail<UnionLinkSession>(started.Error);
            }

            var attempt = started.Value;

            if (attempt.Id == Guid.Empty || !Uri.TryCreate(attempt.LoginUrl, UriKind.Absolute, out var loginUrl)
                || loginUrl.UserInfo.Length != 0 || loginUrl.GetLeftPart(UriPartial.Authority) != LinkBase)
            {
                return Err<UnionLinkSession>(UnionLinkErrorKind.Protocol, Lang.Text("Link.Union.InvalidLoginUrl"));
            }

            var remaining = attempt.ExpiresAt - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return ExpiredBrowserLogin();
            }

            using var expiry = new CancellationTokenSource(
                remaining > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : remaining, _clock);
            using var polling = CancellationTokenSource.CreateLinkedTokenSource(token, expiry.Token);
            try
            {
                polling.Token.ThrowIfCancellationRequested();
                if (!await openBrowser(loginUrl, polling.Token).WaitAsync(polling.Token).ConfigureAwait(false))
                {
                    return Err<UnionLinkSession>(UnionLinkErrorKind.BrowserUnavailable,
                        Lang.Text("Link.Union.BrowserUnavailable"));
                }

                while (_clock.GetUtcNow() < attempt.ExpiresAt)
                {
                    using var claimRequest = HttpRequest.CreatePost(LinkBase + LauncherPath + "/claim")
                        .WithJsonContent(new { id = attempt.Id, verifier });
                    var claimed = await SendAsync<UnionLinkClaimResponse>(claimRequest, polling.Token)
                        .ConfigureAwait(false);
                    polling.Token.ThrowIfCancellationRequested();
                    if (_clock.GetUtcNow() >= attempt.ExpiresAt)
                    {
                        return ExpiredBrowserLogin();
                    }

                    if (claimed.IsFailure)
                    {
                        return Fail<UnionLinkSession>(claimed.Error);
                    }

                    var claim = claimed.Value;
                    if (claim.Pending is not { } pending)
                    {
                        return Err<UnionLinkSession>(UnionLinkErrorKind.Protocol,
                            Lang.Text("Link.Union.InvalidResponse"));
                    }

                    if (!pending)
                    {
                        return StoreSession(claim.Token, claim.ExpiresAt);
                    }

                    await Task.Delay(PollInterval, _clock, polling.Token).ConfigureAwait(false);
                }

                return ExpiredBrowserLogin();
            }
            catch (OperationCanceledException) when (expiry.IsCancellationRequested && !token.IsCancellationRequested)
            {
                return ExpiredBrowserLogin();
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private static Result<UnionLinkSession, UnionLinkError> ExpiredBrowserLogin()
    {
        return Err<UnionLinkSession>(UnionLinkErrorKind.Expired, Lang.Text("Link.Union.LoginExpired"));
    }

    #endregion

    #region 游戏令牌换票

    /// <summary>匿名读取 Union Link 已核准的认证来源。</summary>
    public async Task<Result<IReadOnlyList<UnionLinkProvider>, UnionLinkError>> GetProvidersAsync(
        CancellationToken token = default)
    {
        using var request = HttpRequest.Create(LinkBase + LauncherPath + "/providers");
        var result = await SendAsync<UnionLinkProvider[]>(request, token).ConfigureAwait(false);
        return result.IsSuccess
            ? Result<IReadOnlyList<UnionLinkProvider>, UnionLinkError>.Ok(result.Value)
            : Fail<IReadOnlyList<UnionLinkProvider>>(result.Error);
    }

    /// <summary>
    ///     使用已保存的外置游戏档案换取启动器令牌。认证来源根据档案的服务器地址自动匹配。
    /// </summary>
    public Task<Result<UnionLinkSession, UnionLinkError>> LauncherGameLoginAsync(McProfile profile,
        CancellationToken token = default)
    {
        return LauncherGameLoginAsync(null, profile, token);
    }

    /// <summary>
    ///     使用已保存的外置游戏档案换取启动器令牌，并限制在指定认证来源内。
    /// </summary>
    public Task<Result<UnionLinkSession, UnionLinkError>> LauncherGameLoginAsync(Guid? providerId,
        McProfile profile, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        token.ThrowIfCancellationRequested();
        if (!TryCreateCredential(profile, out var credential))
        {
            return Task.FromResult(Err<UnionLinkSession>(UnionLinkErrorKind.Unsupported,
                Lang.Text("Link.Union.ProfileUnsupported")));
        }

        return LauncherGameLoginCoreAsync(providerId == Guid.Empty ? null : providerId, credential, token);
    }

    private async Task<Result<UnionLinkSession, UnionLinkError>> LauncherGameLoginCoreAsync(
        Guid? requestedProviderId, UnionLinkGameCredential credential, CancellationToken token)
    {
        await _sessionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var providers = await GetProvidersAsync(token).ConfigureAwait(false);
            if (providers.IsFailure)
            {
                return Fail<UnionLinkSession>(providers.Error);
            }

            var matches = providers.Value
                .Where(item => item is { DirectExchange: true } && item.Id != Guid.Empty
                    && (requestedProviderId is null || item.Id == requestedProviderId)
                    && string.Equals(NormalizeAuthServer(item.AuthApiRoot), credential.AuthServer,
                        StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (matches.Length != 1)
            {
                return Err<UnionLinkSession>(UnionLinkErrorKind.Unsupported,
                    Lang.Text("Link.Union.ProviderUnsupported"));
            }

            var provider = matches[0];
            var verifier = S256Provider.GenerateCodeVerifier();
            var startedAt = _clock.GetUtcNow();
            using var startRequest = HttpRequest.CreatePost(LinkBase + LauncherPath + "/yggdrasil/start")
                .WithJsonContent(new
                {
                    providerId = provider.Id,
                    codeChallenge = S256Provider.Instance.ComputeCodeChallenge(verifier),
                    codeChallengeMethod = "S256"
                });
            var started = await SendAsync<UnionLinkGameAttempt>(startRequest, token).ConfigureAwait(false);
            if (started.IsFailure)
            {
                return Fail<UnionLinkSession>(started.Error);
            }

            var attempt = started.Value;

            if (attempt.ProviderId != provider.Id || attempt.ProviderRevision != provider.Revision
                || attempt.AttemptId == Guid.Empty || !IsBase64UrlCode(attempt.Nonce)
                || attempt.ExpiresIn is <= 0 or > 120
                || _clock.GetUtcNow() >= startedAt.AddSeconds(attempt.ExpiresIn))
            {
                return Err<UnionLinkSession>(UnionLinkErrorKind.Expired, Lang.Text("Link.Union.AttemptExpired"));
            }

            using var exchangeRequest = HttpRequest.CreatePost(LinkBase + LauncherPath + "/yggdrasil/exchange")
                .WithJsonContent(new
                {
                    attemptId = attempt.AttemptId,
                    nonce = attempt.Nonce,
                    codeVerifier = verifier,
                    gameAccessToken = credential.AccessToken,
                    gameClientToken = credential.ClientToken,
                    gameProfile = new { id = credential.ProfileId.ToString("N") }
                });
            var exchanged = await SendAsync<UnionLinkGameLoginResponse>(exchangeRequest, token).ConfigureAwait(false);
            if (exchanged.IsFailure)
            {
                return Fail<UnionLinkSession>(exchanged.Error);
            }

            var response = exchanged.Value;

            if (response.TokenType != "Bearer" || response.ProviderId != provider.Id
                || !Guid.TryParse(response.GameProfile?.Id, out var verifiedProfile)
                || verifiedProfile != credential.ProfileId || response.ExpiresIn is <= 0 or > 900)
            {
                return Err<UnionLinkSession>(UnionLinkErrorKind.Protocol, Lang.Text("Link.Union.ExchangeRejected"));
            }

            return StoreSession(response.Token, response.ExpiresAt);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private static bool TryCreateCredential(McProfile profile, out UnionLinkGameCredential credential)
    {
        credential = default!;
        if (profile.ProfileType != ProfileType.Authlib)
        {
            return false;
        }

        var server = NormalizeAuthServer(profile.Server);
        if (server is null || !Guid.TryParse(profile.Uuid, out var profileId))
        {
            return false;
        }

        var accessToken = profile.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 16384)
        {
            return false;
        }

        var clientToken = profile.ClientToken;
        credential = new UnionLinkGameCredential(server, profileId, accessToken,
            clientToken is { Length: > 0 and <= 256 } ? clientToken : null);
        return true;
    }

    private static bool IsBase64UrlCode(string? value)
    {
        return value is { Length: 43 }
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    #endregion

    #region 会话与服务器目录

    /// <summary>退出登录并清除本地保存的令牌。</summary>
    public async Task<Result<bool, UnionLinkError>> LogoutAsync(CancellationToken token = default)
    {
        await _sessionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (CurrentSession is { } session)
            {
                using var request = HttpRequest.CreatePost(LinkBase + LauncherPath + "/logout")
                    .WithBearerToken(session.Token);
                var result = await SendAsync<UnionLinkLogoutResponse>(request, token).ConfigureAwait(false);
                var credentialRejected = result.IsFailure
                    && result.Error.Kind == UnionLinkErrorKind.AuthenticationRequired;
                var confirmed = result.IsSuccess && result.Value.Ok is true;
                if (!credentialRejected && !confirmed)
                {
                    return result.IsFailure
                        ? Fail<bool>(result.Error)
                        : Err<bool>(UnionLinkErrorKind.Protocol, Lang.Text("Link.Union.LogoutFailed"));
                }
            }

            ClearSession();
            return Result<bool, UnionLinkError>.Ok(true);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>获取当前会话可以访问的分发服务器。</summary>
    public async Task<Result<IReadOnlyList<UnionLinkMemberServer>, UnionLinkError>> GetAvailableMembersAsync(
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (CurrentSession is not { } session)
        {
            return Err<IReadOnlyList<UnionLinkMemberServer>>(UnionLinkErrorKind.AuthenticationRequired,
                Lang.Text("Link.Union.LoginRequired"));
        }

        using var request = HttpRequest.Create(LinkBase + "/api/v1/servers").WithBearerToken(session.Token);
        var result = await SendAsync<UnionLinkServerDirectory>(request, token).ConfigureAwait(false);
        if (result.IsFailure)
        {
            // 仅当失败的仍是被拒绝的那一份会话时才清除，避免覆盖并发建立的新会话
            if (result.Error.Kind == UnionLinkErrorKind.AuthenticationRequired)
            {
                _ClearSessionIf(session);
            }

            return Fail<IReadOnlyList<UnionLinkMemberServer>>(result.Error);
        }

        return result.Value.Servers is { } servers
            ? Result<IReadOnlyList<UnionLinkMemberServer>, UnionLinkError>.Ok(servers)
            : Err<IReadOnlyList<UnionLinkMemberServer>>(UnionLinkErrorKind.Protocol,
                Lang.Text("Link.Union.DirectoryInvalid"));
    }

    private Result<UnionLinkSession, UnionLinkError> StoreSession(string? token, DateTimeOffset? expiresAt)
    {
        if (!IsValidToken(token) || expiresAt is not { } expiration || expiration <= _clock.GetUtcNow())
        {
            return Err<UnionLinkSession>(UnionLinkErrorKind.Protocol, Lang.Text("Link.Union.InvalidSession"));
        }

        var session = new UnionLinkSession(token!, expiration);
        Volatile.Write(ref _session, session);
        _PersistSession(session);
        return Result<UnionLinkSession, UnionLinkError>.Ok(session);
    }

    private void ClearSession()
    {
        Volatile.Write(ref _session, null);
        _PersistSession(null);
    }

    private void _ClearSessionIf(UnionLinkSession session)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session))
        {
            _PersistSession(null);
        }
    }

    private UnionLinkSession? _RestorePersistedSession()
    {
        try
        {
            var token = States.Link.UnionLinkToken;
            var expiresAt = States.Link.UnionLinkExpiresAt;
            if (!IsValidToken(token) || expiresAt <= 0)
            {
                return null;
            }

            var expiration = DateTimeOffset.FromUnixTimeMilliseconds(expiresAt);
            return expiration > _clock.GetUtcNow() ? new UnionLinkSession(token, expiration) : null;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, LogModule, "读取本地 Union Link 会话失败");
            return null;
        }
    }

    private static void _PersistSession(UnionLinkSession? session)
    {
        try
        {
            States.Link.UnionLinkToken = session?.Token ?? string.Empty;
            States.Link.UnionLinkExpiresAt = session?.ExpiresAt.ToUnixTimeMilliseconds() ?? 0L;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, LogModule, "写入本地 Union Link 会话失败");
        }
    }

    private static bool IsValidToken(string? token)
    {
        return token is { Length: 68 } && token.StartsWith("mul_", StringComparison.Ordinal)
            && token.Skip(4).All(character => character is >= '0' and <= '9'
                or >= 'a' and <= 'f');
    }

    #endregion

    #region 请求与工具

    /// <summary>
    /// Union Link定制的请求
    /// </summary>
    /// <param name="request">HTTP 请求消息</param>
    /// <param name="token">取消令牌</param>
    /// <typeparam name="T">返回数据类型</typeparam>
    /// <returns>请求结果</returns>
    private async Task<Result<T, UnionLinkError>> SendAsync<T>(HttpRequestMessage request, CancellationToken token)
        where T : class
    {
        try
        {
            using var response = await request
                .SendAsync(_getClient(), false, false, retryTimes: 0,
                    cancellationToken: token)
                .ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                var status = response.StatusCode;
                var errorBody = await _ReadErrorBodyAsync(response, token).ConfigureAwait(false);
                LastErrorResponseBody = errorBody;
                LogWrapper.Warn(LogModule, string.IsNullOrWhiteSpace(errorBody)
                    ? $"Union Link 请求返回 HTTP {(int)status}（响应体为空）"
                    : $"Union Link 请求返回 HTTP {(int)status}，响应体: {_TruncateForLog(errorBody)}");
                return status switch
                {
                    HttpStatusCode.Unauthorized => Err<T>(UnionLinkErrorKind.AuthenticationRequired,
                        Lang.Text("Link.Union.AuthenticationRequired")),
                    HttpStatusCode.Forbidden => Err<T>(UnionLinkErrorKind.Forbidden,
                        Lang.Text("Link.Union.Forbidden")),
                    HttpStatusCode.TooManyRequests => Err<T>(UnionLinkErrorKind.RateLimited,
                        Lang.Text("Link.Union.RateLimited")),
                    _ => Err<T>(UnionLinkErrorKind.Network, Lang.Text("Link.Union.HttpFailed", (int)status))
                };
            }

            var data = await response.AsJsonAsync<T>(cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return data is not null
                ? Result<T, UnionLinkError>.Ok(data)
                : Err<T>(UnionLinkErrorKind.Protocol, Lang.Text("Link.Union.InvalidResponse"));
        }
        catch (InvalidDataException)
        {
            LogWrapper.Warn(LogModule, "Union Link 返回的 JSON 无效");
            return Err<T>(UnionLinkErrorKind.Protocol, Lang.Text("Link.Union.InvalidResponse"));
        }
        catch (JsonException)
        {
            LogWrapper.Warn(LogModule, "Union Link 返回的 JSON 无法解析");
            return Err<T>(UnionLinkErrorKind.Protocol, Lang.Text("Link.Union.InvalidResponse"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException
                                   || (ex is OperationCanceledException && !token.IsCancellationRequested))
        {
            LogWrapper.Warn(ex, LogModule, "Union Link 请求失败");
            return Err<T>(UnionLinkErrorKind.Network, Lang.Text("Link.Union.NetworkFailed"));
        }
    }

    /// <summary>
    ///     通过流读取失败响应的正文，方便在断点处查看服务端返回的具体错误。
    ///     读取失败或正文为空时返回 <see langword="null" />，不影响原始的 HTTP 错误映射。
    /// </summary>
    private static async Task<string?> _ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var body = await reader.ReadToEndAsync(token).ConfigureAwait(false);
            return string.IsNullOrEmpty(body) ? null : body;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException
                                       or OperationCanceledException)
        {
            LogWrapper.Debug(ex, LogModule, "读取 Union Link 错误响应体失败");
            return null;
        }
    }

    /// <summary>日志中最多保留的错误响应正文长度。</summary>
    private const int MaxErrorBodyLogLength = 4096;

    private static string _TruncateForLog(string body)
    {
        return body.Length <= MaxErrorBodyLogLength
            ? body
            : body[..MaxErrorBodyLogLength] + "...";
    }

    /// <summary>
    ///     将Ygg服务器地址转为认证来源根地址
    ///     PCL的档案会把服务器保存为 <c>{root}/authserver</c>，UL的认证来源则是 <c>root</c>。
    /// </summary>
    /// <param name="address">服务器地址</param>
    /// <returns>归一化后的认证来源根地址，如果无效则返回 <c>null</c></returns>
    internal static string? NormalizeAuthServer(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            return null;
        }

        var value = uri.AbsoluteUri.TrimEnd('/');
        return value.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase)
            ? value[..^"/authserver".Length]
            : value;
    }

    private static Result<T, UnionLinkError> Err<T>(UnionLinkErrorKind kind, string message)
    {
        return Result<T, UnionLinkError>.Err(new UnionLinkError(kind, message));
    }

    private static Result<T, UnionLinkError> Fail<T>(UnionLinkError error)
    {
        return Result<T, UnionLinkError>.Err(error);
    }

    private sealed record UnionLinkGameCredential(
        string AuthServer,
        Guid ProfileId,
        string AccessToken,
        string? ClientToken);

    #endregion
}