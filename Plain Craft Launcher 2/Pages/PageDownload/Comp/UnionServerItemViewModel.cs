using System.Linq;
using PCL.Core.App.Localization;
using PCL.Core.Link.UnionLink.Models;

namespace PCL;

/// <summary>
///     Union 服务器列表中的一项，展示服务器及其当前发布的整合包信息。
/// </summary>
public sealed class UnionServerItemViewModel(UnionLinkMemberServer server)
{
    public UnionLinkMemberServer Server { get; } = server;

    public string DisplayName => Server.DisplayName;

    public string Description => Server.Description;

    public string PackLine
    {
        get
        {
            var release = Server.CurrentRelease;
            var pack = release is null
                ? string.Empty
                : string.Join(" · ", new[] { release.Name, release.VersionLabel }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            return Lang.Text("Download.Union.Pack", pack);
        }
    }

    public string OrganizationLine =>
        Lang.Text("Download.Union.Organization", Server.Organization?.DisplayName ?? string.Empty);

    public string AddressLine =>
        Lang.Text("Download.Union.Address", Server.GameAddress ?? string.Empty);

    public bool HasAddress => !string.IsNullOrWhiteSpace(Server.GameAddress);
}
