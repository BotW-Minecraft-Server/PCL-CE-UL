using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PCL.Core.App.Localization;
using PCL.Core.Link.UnionLink;
using PCL.Core.Link.UnionLink.Models;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL;

public partial class PageDownloadUnionPack
{
    private readonly PageDownloadUnionPackViewModel _vm = new();

    private readonly Loading.LoaderTask<bool, List<UnionLinkMemberServer>> _loader;

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
    }

    private void LoaderInit()
    {
        PageLoaderInit(Load, PanLoad, PanBack, null, _loader, _ => Load_OnFinish());
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
            var servers = _loader.output ?? [];
            _vm.SetServers(servers.Select(server => new UnionServerItemViewModel(server)));
            HintEmpty.Visibility = servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
}
