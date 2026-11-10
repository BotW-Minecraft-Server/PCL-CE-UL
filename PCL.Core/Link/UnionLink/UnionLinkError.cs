namespace PCL.Core.Link.UnionLink;

/// <summary>
/// Union Link 请求的失败类别。界面可据此决定是否引导用户重新登录。
/// </summary>
public enum UnionLinkErrorKind
{
    /// <summary>网络不可用或响应无法读取。</summary>
    Network,

    /// <summary>响应格式或协议字段不符合预期。</summary>
    Protocol,

    /// <summary>凭据缺失、无效或已过期，需要重新登录。</summary>
    AuthenticationRequired,

    /// <summary>服务端拒绝访问。</summary>
    Forbidden,

    /// <summary>请求过于频繁。</summary>
    RateLimited,

    /// <summary>无法打开浏览器。</summary>
    BrowserUnavailable,

    /// <summary>当前档案或认证来源不支持该操作。</summary>
    Unsupported,

    /// <summary>浏览器登录或换票请求已过期。</summary>
    Expired
}

/// <summary>
/// Union Link 请求的失败信息，<see cref="Message"/> 已经过本地化，可直接展示。
/// </summary>
public sealed record UnionLinkError(UnionLinkErrorKind Kind, string Message);
