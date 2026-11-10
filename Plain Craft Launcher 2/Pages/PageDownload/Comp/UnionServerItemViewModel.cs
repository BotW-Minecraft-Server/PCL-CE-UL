using PCL.Core.Link.UnionLink.Models;

namespace PCL;

/// <summary>
///     Union 服务器列表中的一项，展示服务器及其当前发布的整合包信息。
/// </summary>
public sealed class UnionServerItemViewModel(UnionLinkMemberServer server)
{
    public UnionLinkMemberServer Server { get; } = server;

    /// <summary>服务器名称 + 整合包名称。</summary>
    public string TitleLine
    {
        get
        {
            var packName = server.CurrentRelease?.Name;
            return string.IsNullOrWhiteSpace(packName)
                ? server.DisplayName
                : string.Join(" · ", server.DisplayName, "整合包");
        }
    }

    /// <summary>整合包版本号，界面以灰色展示。</summary>
    public string VersionLine => server.CurrentRelease?.VersionLabel ?? string.Empty;

    public string Description => server.Description;
}
