using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PCL.Core.ViewModel;

namespace PCL;

/// <summary>
///     Union 整合包下载页的视图模型：只承载服务器与其当前整合包的信息。
///     实际下载将在后续版本接入。
/// </summary>
public partial class PageDownloadUnionPackViewModel : ViewModelBase
{
    [ObservableProperty] private ObservableCollection<UnionServerItemViewModel> _servers = [];

    public void SetServers(IEnumerable<UnionServerItemViewModel> servers)
    {
        Servers = new ObservableCollection<UnionServerItemViewModel>(servers);
    }
}
