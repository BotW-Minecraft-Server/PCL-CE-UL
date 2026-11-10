using PCL.Core.App.Localization;
using PCL.Core.Link.UnionLink.Models;

namespace PCL;

/// <summary>
///     Union 服务器列表中的一项，展示服务器及其当前发布的整合包信息。
/// </summary>
public sealed class UnionServerItemViewModel(
    UnionLinkMemberServer server,
    MuaPackInstalledState? installed = null)
{
    public UnionLinkMemberServer Server { get; } = server;

    public MuaPackInstalledState? Installed { get; } = installed;

    /// <summary>服务器名称 + 整合包名称。</summary>
    public string TitleLine
    {
        get
        {
            var packName = server.CurrentRelease?.Name;
            return string.IsNullOrWhiteSpace(packName)
                ? server.DisplayName
                : string.Join(" · ", server.DisplayName, packName);
        }
    }

    /// <summary>整合包版本号，界面以灰色展示。</summary>
    public string VersionLine => server.CurrentRelease?.VersionLabel ?? string.Empty;

    public string Description => server.Description;

    public bool IsInstalled => Installed is not null;

    public bool HasUpdate => Installed is not null
                             && server.CurrentRelease is { Id: var releaseId }
                             && releaseId != Guid.Empty
                             && !string.Equals(Installed.ReleaseId, releaseId.ToString(),
                                 StringComparison.OrdinalIgnoreCase);

    public string ButtonText => !IsInstalled
        ? Lang.Text("Download.Union.Download")
        : HasUpdate
            ? Lang.Text("Download.Union.Update")
            : Lang.Text("Download.Union.Reinstall");
}
