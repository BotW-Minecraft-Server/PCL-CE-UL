using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.Link.UnionLink;
using PCL.Core.Link.UnionLink.Models;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.UI.Hints;

namespace PCL;

public partial class PageDownloadUnionPack
{
    private readonly PageDownloadUnionPackViewModel _vm = new();

    private readonly Loading.LoaderTask<bool, List<UnionLinkMemberServer>> _loader;

    private List<UnionLinkMemberServer> _servers = [];

    private IReadOnlyDictionary<string, MuaPackInstalledState> _installedStates =
        new Dictionary<string, MuaPackInstalledState>(StringComparer.Ordinal);

    private bool _isPopulatingOrganizations;

    public PageDownloadUnionPack()
    {
        _loader = new Loading.LoaderTask<bool, List<UnionLinkMemberServer>>(
            Lang.Text("Download.Union.Loading"), LoadServers)
        {
            reloadTimeout = 60 * 1000
        };
        Initialized += (_, _) => LoaderInit();
        Loaded += (_, _) => PanBack.ScrollToHome();
        InitializeComponent();
        DataContext = _vm;

        // 在 InitializeComponent 之后再挂接，避免 XAML 解析时默认选项的 SelectionChanged 早于控件字段赋值
        ComboSearchOrg.SelectionChanged += FilterChanged;
        ComboSearchSort.SelectionChanged += FilterChanged;
        MuaPackInstallService.InstallCompleted += OnInstallCompleted;
    }

    private void OnInstallCompleted(string serverId)
    {
        Common.RunInUi(() =>
        {
            _installedStates = MuaPackInstallService.GetInstalledStates();
            ApplyFilters();
        });
    }

    private void LoaderInit()
    {
        PageLoaderInit(Load, PanLoad, PanContent, PanAlways, _loader, _ => Load_OnFinish());
    }

    private static void LoadServers(Loading.LoaderTask<bool, List<UnionLinkMemberServer>> task)
    {
        var profile = ProfileService.Current;
        if (profile is null || profile.ProfileType != ProfileType.Authlib)
        {
            throw new Exception(Lang.Text("Download.Union.ProfileRequired"));
        }

        task.Progress = 0.2d;
        var result = UnionLinkAuthService.Shared
            .GetServersAsync(profile, task.AbortedToken)
            .GetAwaiter()
            .GetResult();
        task.Progress = 0.9d;
        if (result.IsFailure)
        {
            throw new Exception(result.Error.Message);
        }

        // 当前启动器只接受格式版本 2 的整合包
        task.output = result.Value
            .Where(server => server.CurrentRelease?.PackageFormatVersion == 2d)
            .OrderBy(server => server.SortOrder)
            .ThenBy(server => server.ServerId, StringComparer.Ordinal)
            .ToList();
    }

    private void Load_OnFinish()
    {
        try
        {
            _servers = _loader.output ?? [];
            _installedStates = MuaPackInstallService.GetInstalledStates();
            PopulateOrganizations();
            ApplyFilters();
        }
        catch (Exception ex)
        {
            Common.Log(
                ex,
                "可视化 Union 服务器列表出错",
                Common.LogLevel.Feedback,
                userSummary: Lang.Text("Download.Error.OperationFailed"));
        }
    }

    /// <summary>用当前服务器列表重建组织筛选器，并尽量保留原有选项。</summary>
    private void PopulateOrganizations()
    {
        if (ComboSearchOrg is null) return;

        var previousId = (ComboSearchOrg.SelectedItem as MyComboBoxItem)?.Tag?.ToString() ?? string.Empty;

        _isPopulatingOrganizations = true;
        try
        {
            ComboSearchOrg.Items.Clear();
            var allItem = new MyComboBoxItem
            {
                Content = Lang.Text("Common.Option.All"),
                Tag = string.Empty
            };
            ComboSearchOrg.Items.Add(allItem);

            MyComboBoxItem? restore = null;
            var organizations = _servers
                .Select(server => server.Organization)
                .Where(organization => organization is not null)
                .GroupBy(organization => organization!.Id)
                .Select(group => group.First()!)
                .OrderBy(organization => organization.DisplayName, StringComparer.CurrentCulture);
            foreach (var organization in organizations)
            {
                var item = new MyComboBoxItem
                {
                    Content = organization.DisplayName,
                    Tag = organization.Id.ToString()
                };
                ComboSearchOrg.Items.Add(item);
                if (!string.IsNullOrEmpty(previousId) && item.Tag?.ToString() == previousId)
                {
                    restore = item;
                }
            }

            (restore ?? allItem).IsSelected = true;
        }
        finally
        {
            _isPopulatingOrganizations = false;
        }
    }

    private void ApplyFilters()
    {
        // 极端情况下（XAML 尚未完成控件连接）不执行过滤，等 Load_OnFinish 再刷新
        if (ComboSearchSort is null || ComboSearchOrg is null || PanSearchBox is null || HintEmpty is null) return;

        var search = PanSearchBox.Text.Trim();
        var organizationId = (ComboSearchOrg.SelectedItem as FrameworkElement)?.Tag?.ToString() ?? string.Empty;
        var sort = (ComboSearchSort.SelectedItem as FrameworkElement)?.Tag?.ToString() ?? "0";

        IEnumerable<UnionLinkMemberServer> query = _servers;
        if (!string.IsNullOrEmpty(organizationId))
        {
            query = query.Where(server => server.Organization?.Id.ToString() == organizationId);
        }

        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(server =>
                server.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || server.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || (server.Organization?.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                    ?? false)
                || (server.CurrentRelease?.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                    ?? false)
                || (server.CurrentRelease?.VersionLabel.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                    ?? false));
        }

        query = sort switch
        {
            "1" => query.OrderBy(server => server.DisplayName, StringComparer.CurrentCulture),
            "2" => query.OrderByDescending(server => server.CurrentRelease?.VersionLabel ?? string.Empty,
                StringComparer.OrdinalIgnoreCase),
            _ => query.OrderBy(server => server.SortOrder)
                .ThenBy(server => server.ServerId, StringComparer.Ordinal)
        };

        var items = query
            .Select(server => new UnionServerItemViewModel(server,
                _installedStates.GetValueOrDefault(server.ServerId)))
            .ToList();
        _vm.SetServers(items);
        HintEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PanSearchBox_Search(object sender, EventArgs e)
    {
        ApplyFilters();
    }

    private void DownloadButton_Click(object sender, Common.RouteEventArgs routeEventArgs)
    {
        _ = DownloadAndInstallAsync(sender);
    }

    private async Task DownloadAndInstallAsync(object sender)
    {
        if (sender is not FrameworkElement { DataContext: UnionServerItemViewModel item }) return;

        var profile = ProfileService.Current;
        if (profile is null || profile.ProfileType != ProfileType.Authlib)
        {
            HintService.Show(Hints.Error(Lang.Text("Download.Union.ProfileRequired")));
            return;
        }

        HintService.Show(Hints.Info(Lang.Text("Download.Union.PackageFetching")));
        try
        {
            var installed = item.Installed;
            Guid? baseReleaseId = null;
            string? baseManifestSha256 = null;
            if (installed is not null
                && Guid.TryParse(installed.ReleaseId, out var parsed)
                && !string.IsNullOrWhiteSpace(installed.TargetManifestSha256))
            {
                baseReleaseId = parsed;
                baseManifestSha256 = installed.TargetManifestSha256;
            }

            var result = await UnionLinkAuthService.Shared
                .GetServerPackagesAsync(profile, item.Server.ServerId,
                    baseReleaseId: baseReleaseId,
                    baseManifestSha256: baseManifestSha256)
                .ConfigureAwait(true);
            if (result.IsFailure)
            {
                HintService.Show(Hints.Error(result.Error.Message));
                return;
            }

            var descriptor = result.Value;
            var url = descriptor.Url;
            var size = descriptor.Size;
            var sha256 = descriptor.Sha256;
            if (!string.Equals(descriptor.PackageType, "full", StringComparison.OrdinalIgnoreCase)
                && descriptor.Full is { } full)
            {
                url = full.Url;
                size = full.Size;
                sha256 = full.Sha256;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                HintService.Show(Hints.Error(Lang.Text("Download.Union.PackageMissing")));
                return;
            }

            MuaPackInstallService.DownloadAndInstall(url, size, sha256, descriptor.Version,
                item.Server.ServerId, installed?.InstanceName, installed);
            HintService.Show(Hints.Info(Lang.Text("Download.Union.PackageStarted")));
        }
        catch (Exception ex)
        {
            Common.Log(ex, "获取 Union 整合包失败", Common.LogLevel.Feedback,
                userSummary: Lang.Text("Download.Union.PackageFetchFailed"));
            HintService.Show(Hints.Error(Lang.Text("Download.Union.PackageFetchFailed")));
        }
    }

    private void PanSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyFilters();
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingOrganizations) return;
        ApplyFilters();
    }

    private void BtnSearchReset_Click(object sender, EventArgs e)
    {
        _isPopulatingOrganizations = true;
        try
        {
            PanSearchBox.Text = string.Empty;
            ComboSearchOrg.SelectedIndex = 0;
            ComboSearchSort.SelectedIndex = 0;
        }
        finally
        {
            _isPopulatingOrganizations = false;
        }

        ApplyFilters();
    }
}
