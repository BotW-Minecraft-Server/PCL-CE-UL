using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Link.UnionLink;
using PCL.Core.Link.UnionLink.Models;
using PCL.Core.Link.UnionLink.MuaPack;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;
using static PCL.Loading;

namespace PCL;

/// <summary>
/// 已安装的 Union 整合包状态，来自实例目录下的 .mua/state.json。
/// </summary>
public sealed record MuaPackInstalledState(
    string ServerId,
    string InstanceName,
    string ReleaseId,
    string Version,
    string TargetManifestSha256,
    bool NeedsFullInstall = false);

/// <summary>
/// 启动前发现的 Union 整合包更新。
/// </summary>
public sealed record MuaPackUpdateNotice(
    string ServerId,
    string ServerName,
    string InstanceName,
    string InstalledVersion,
    string LatestVersion,
    bool NeedsFullInstall = false);

/// <summary>
/// muapack v2 的最小安装/更新实现：校验签名与摘要，取出 MUA 元数据，
/// 再复用现有 Modrinth 安装器安装内容。
/// </summary>
public static class MuaPackInstallService
{
    /// <summary>安装或更新完成后触发，参数为 serverId。</summary>
    public static event Action<string>? InstallCompleted;

    /// <summary>安装或更新失败、被中断后触发，参数为 serverId。此时实例需要重新下载完整包。</summary>
    public static event Action<string>? InstallFailed;

    private sealed record MuaPackInfo(
        string InstanceName,
        string ServerId,
        string ReleaseId,
        string Version,
        string TargetManifestSha256,
        string PackageType,
        string? BaseReleaseId,
        string? BaseManifestSha256,
        bool RuntimeChanged,
        string RemovalPolicy,
        int RemovedCount,
        IReadOnlyList<string> RemovedPaths,
        byte[] MuaPackBytes,
        byte[] TargetManifestBytes,
        IReadOnlyDictionary<string, byte[]> MetadataEntries,
        IReadOnlyList<string> OptionalLocalPaths);

    /// <summary>
    /// 下载 Union 整合包，完成后自动安装或更新。
    /// </summary>
    public static void DownloadAndInstall(string url, long size, string sha256, string version, string serverId,
        string? instanceName = null, MuaPackInstalledState? currentState = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (string.IsNullOrWhiteSpace(sha256))
            throw new ArgumentException("Union 整合包缺少 SHA-256。", nameof(sha256));

        var folder = Path.Combine(Paths.Temp, "Union", serverId);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"{_SafeFileName(version, "pack")}-{serverId}.muapack");

        var download = new LoaderDownload(
            Lang.Text("Download.Union.DownloadTask", serverId),
            [
                new DownloadFile([url], target,
                    new FileChecker(actualSize: size, hash: sha256), true)
            ]);
        var loader = new LoaderCombo<int>(Lang.Text("Download.Union.DownloadTask", serverId), [download]);
        loader.OnStateChangedUi += (_, state, _) =>
        {
            if (state != LoadState.Finished) return;
            LauncherCommon.RunInNewThread(() =>
            {
                try
                {
                    Install(target, serverId, instanceName, currentState);
                }
                catch (Exception ex)
                {
                    LauncherCommon.Log(ex, "[MuaPack] 安装整合包失败",
                        LauncherLogLevel.Feedback, userSummary: Lang.Text("Download.Union.InstallFailed"));
                    LauncherUi.Interaction?.ShowHint(
                        Lang.Text("Download.Union.InstallFailed"), NotificationType.Error);
                }
            }, "MuaPack install");
        };
        loader.Start(1);
        LoaderTaskbarAdd(loader);
    }

    /// <summary>
    /// 安装或更新 muapack。必须在工作线程执行。
    /// </summary>
    public static LoaderCombo<string> Install(string muapackPath, string serverId,
        string? instanceName = null, MuaPackInstalledState? currentState = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(muapackPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var info = ReadInfo(muapackPath, serverId, instanceName);
        var targetInstance = info.InstanceName;

        if (string.Equals(info.PackageType, "update", StringComparison.OrdinalIgnoreCase))
        {
            if (currentState is null)
                throw new Exception("未找到已安装的版本，请重新安装完整包。");
            if (currentState.NeedsFullInstall)
                throw new Exception("上次安装未完成，请重新下载完整包。");
            if (!string.Equals(info.BaseReleaseId, currentState.ReleaseId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(info.BaseManifestSha256, currentState.TargetManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("更新基准与本地安装不一致，请重新安装完整包。");
            }

            if (info.RuntimeChanged)
                throw new Exception("运行环境已变化，请重新安装完整包。");

            targetInstance = currentState.InstanceName;
        }

        var previousManifest = ReadInstalledTargetManifest(targetInstance);
        var loader = Modpack.ModpackInstallWithoutRestriction(
            muapackPath, targetInstance, excludeOptional: true, preferSha512: true,
            allowExistingInstance: true);
        loader.OnStateChangedUi += (_, state, _) =>
        {
            // 失败或被中断时，实例可能只被改写了一部分：
            // 记录“需要完整安装”，下次请求不再以旧版本为基准索取增量包。
            if (state is LoadState.Failed or LoadState.Aborted)
            {
                SaveBrokenState(targetInstance, info, currentState);
                LauncherCommon.RunInNewThread(() =>
                {
                    InstallFailed?.Invoke(serverId);
                    LauncherUi.Interaction?.ShowHint(
                        Lang.Text("Download.Union.InstallFailed"), NotificationType.Error);
                }, "MuaPack state");
                return;
            }

            if (state != LoadState.Finished) return;
            LauncherCommon.RunInNewThread(() =>
            {
                SaveInstallResult(info, targetInstance, previousManifest);
                InstallCompleted?.Invoke(serverId);
            }, "MuaPack state");
        };
        return loader;
    }

    /// <summary>
    /// 扫描当前 Minecraft 文件夹下所有实例的 .mua/state.json。
    /// </summary>
    public static IReadOnlyDictionary<string, MuaPackInstalledState> GetInstalledStates()
    {
        var result = new Dictionary<string, MuaPackInstalledState>(StringComparer.Ordinal);
        var selected = GameFolders.mcFolderSelected;
        if (string.IsNullOrWhiteSpace(selected)) return result;

        var versions = Path.Combine(selected, "versions");
        if (!Directory.Exists(versions)) return result;

        foreach (var instanceDir in Directory.EnumerateDirectories(versions))
        {
            var statePath = Path.Combine(instanceDir, ".mua", "state.json");
            if (!File.Exists(statePath)) continue;
            try
            {
                if (JsonCompat.ParseNode(File.ReadAllText(statePath)) is not JsonObject state) continue;
                var serverId = state["serverId"]?.ToString();
                if (string.IsNullOrWhiteSpace(serverId)) continue;
                result[serverId] = new MuaPackInstalledState(
                    serverId,
                    Path.GetFileName(instanceDir),
                    state["releaseId"]?.ToString() ?? string.Empty,
                    state["version"]?.ToString() ?? string.Empty,
                    state["targetManifestSha256"]?.ToString() ?? string.Empty,
                    state["needsFullInstall"]?.GetValue<bool>() == true);
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(ex, $"[MuaPack] 读取安装状态失败：{statePath}");
            }
        }

        return result;
    }

    /// <summary>
    /// 读取指定实例的 Union 整合包安装状态；未安装时返回 null。
    /// </summary>
    public static MuaPackInstalledState? GetInstalledState(string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return null;
        var selected = GameFolders.mcFolderSelected;
        if (string.IsNullOrWhiteSpace(selected)) return null;

        var statePath = Path.Combine(selected, "versions", instanceName, ".mua", "state.json");
        if (!File.Exists(statePath)) return null;
        try
        {
            if (JsonCompat.ParseNode(File.ReadAllText(statePath)) is not JsonObject state) return null;
            var serverId = state["serverId"]?.ToString();
            if (string.IsNullOrWhiteSpace(serverId)) return null;
            return new MuaPackInstalledState(
                serverId,
                instanceName,
                state["releaseId"]?.ToString() ?? string.Empty,
                state["version"]?.ToString() ?? string.Empty,
                state["targetManifestSha256"]?.ToString() ?? string.Empty,
                state["needsFullInstall"]?.GetValue<bool>() == true);
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, $"[MuaPack] 读取安装状态失败：{statePath}");
            return null;
        }
    }

    /// <summary>
    /// 检查指定实例是否有新的 Union 整合包版本。
    /// 网络或会话不可用时返回 null，调用方应当继续正常启动。
    /// </summary>
    public static MuaPackUpdateNotice? CheckUpdate(string instanceName)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return CheckUpdateAsync(instanceName, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[MuaPack] 启动前检查整合包更新失败");
            return null;
        }
    }

    /// <summary>
    /// 检查指定实例是否有新的 Union 整合包版本。
    /// </summary>
    public static async Task<MuaPackUpdateNotice?> CheckUpdateAsync(string instanceName,
        CancellationToken token = default)
    {
        var state = GetInstalledState(instanceName);
        if (state is null) return null;

        var profile = ProfileService.Current;
        if (profile is null) return null;
        if (!await UnionLinkAuthService.Shared.IsProfileEligibleAsync(profile, token).ConfigureAwait(false))
            return null;

        var servers = await UnionLinkAuthService.Shared.GetServersAsync(profile, token).ConfigureAwait(false);
        if (servers.IsFailure) return null;

        var server = servers.Value.FirstOrDefault(item =>
            string.Equals(item.ServerId, state.ServerId, StringComparison.Ordinal));
        if (server?.CurrentRelease is not { } release || release.Id == Guid.Empty) return null;

        var needsFullInstall = state.NeedsFullInstall || string.IsNullOrWhiteSpace(state.ReleaseId);
        if (!needsFullInstall
            && string.Equals(state.ReleaseId, release.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            return null;

        return new MuaPackUpdateNotice(
            server.ServerId,
            server.DisplayName,
            state.InstanceName,
            state.Version,
            string.IsNullOrWhiteSpace(release.VersionLabel) ? release.Name : release.VersionLabel,
            needsFullInstall);
    }

    /// <summary>
    /// 在后台获取最新包描述并开始下载安装（用于启动前的更新确认）。
    /// </summary>
    public static void StartUpdate(string serverId, string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        LauncherCommon.RunInNewThread(() =>
        {
            try
            {
                var profile = ProfileService.Current;
                if (profile is null) return;

                var state = GetInstalledState(instanceName);
                Guid? baseReleaseId = null;
                string? baseManifestSha256 = null;
                if (state is { NeedsFullInstall: false }
                    && Guid.TryParse(state.ReleaseId, out var parsed)
                    && !string.IsNullOrWhiteSpace(state.TargetManifestSha256))
                {
                    baseReleaseId = parsed;
                    baseManifestSha256 = state.TargetManifestSha256;
                }

                var result = UnionLinkAuthService.Shared
                    .GetServerPackagesAsync(profile, serverId, baseReleaseId: baseReleaseId,
                        baseManifestSha256: baseManifestSha256)
                    .GetAwaiter().GetResult();
                if (result.IsFailure)
                {
                    LauncherUi.Interaction?.ShowHint(result.Error.Message, NotificationType.Error);
                    return;
                }

                var descriptor = result.Value;
                var (url, size, sha256) = ResolveDownload(descriptor);
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(sha256))
                {
                    LauncherUi.Interaction?.ShowHint(
                        Lang.Text("Download.Union.PackageMissing"), NotificationType.Error);
                    return;
                }

                LauncherCommon.RunInUi(() => DownloadAndInstall(url, size, sha256,
                    descriptor.Version, serverId, state?.InstanceName, state));
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(ex, "[MuaPack] 启动前更新整合包失败", LauncherLogLevel.Feedback);
                LauncherUi.Interaction?.ShowHint(
                    Lang.Text("Download.Union.PackageFetchFailed"), NotificationType.Error);
            }
        }, "MuaPack update");
    }

    /// <summary>
    /// 从包描述中取出实际需要下载的文件；增量包会自动回退到它附带的完整包。
    /// </summary>
    public static (string Url, long Size, string Sha256) ResolveDownload(
        UnionLinkPackageDownload descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
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

        return (url, size, sha256);
    }

    private static MuaPackInfo ReadInfo(string muapackPath, string serverId, string? instanceName)
    {
        using var stream = File.OpenRead(muapackPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var muaBytes = _ReadEntry(archive, "mua-pack.json")
                       ?? throw new Exception("muapack 缺少 mua-pack.json。");
        var targetBytes = _ReadEntry(archive, "metadata/target-manifest.json")
                          ?? throw new Exception("muapack 缺少 metadata/target-manifest.json。");
        var indexBytes = _ReadEntry(archive, "modrinth.index.json")
                         ?? throw new Exception("muapack 缺少 modrinth.index.json。");
        var signatureBytes = _ReadEntry(archive, "metadata/signature.json");

        var mua = JsonCompat.ParseNode(Encoding.UTF8.GetString(muaBytes)) as JsonObject
                  ?? throw new Exception("mua-pack.json 格式无效。");
        var targetManifest = JsonCompat.ParseNode(Encoding.UTF8.GetString(targetBytes)) as JsonObject
                             ?? throw new Exception("target-manifest.json 格式无效。");

        if (mua["formatVersion"]?.GetValue<int>() != 2 || mua["protocolRevision"]?.GetValue<int>() != 2)
            throw new Exception("仅支持 muapack v2。");

        // 签名校验（未配置公钥时会跳过并记录警告）
        if (signatureBytes is null)
            throw new Exception("muapack 缺少 metadata/signature.json。");
        MuaPackSignature.Verify(muaBytes, signatureBytes);

        var targetRef = mua["target"] as JsonObject
                        ?? throw new Exception("mua-pack.json 缺少 target。");
        var release = targetManifest["release"] as JsonObject
                      ?? throw new Exception("target-manifest.json 缺少 release。");

        var targetHash = _Sha256Hex(targetBytes);
        if (!string.Equals(targetRef["manifestSha256"]?.ToString(), targetHash,
                StringComparison.OrdinalIgnoreCase))
            throw new Exception("target-manifest.json 摘要与 mua-pack.json 不一致。");

        var indexHash = _Sha256Hex(indexBytes);
        if (!string.Equals(mua["indexSha256"]?.ToString(), indexHash, StringComparison.OrdinalIgnoreCase))
            throw new Exception("modrinth.index.json 摘要与 mua-pack.json 不一致。");

        var releaseName = release["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(instanceName) || !LauncherCommon.IsValidInstanceName(instanceName))
        {
            instanceName = LauncherCommon.IsValidInstanceName(releaseName ?? "")
                ? releaseName!
                : serverId;
        }

        var optionalLocalPaths = new List<string>();
        if (targetManifest["files"] is JsonArray files)
        {
            foreach (var file in files.OfType<JsonObject>())
            {
                if (file["optional"]?.GetValue<bool>() != true) continue;
                if (!string.Equals(file["source"]?["type"]?.ToString(), "local", StringComparison.Ordinal))
                    continue;
                var path = file["path"]?.ToString();
                if (!string.IsNullOrWhiteSpace(path)) optionalLocalPaths.Add(path);
            }
        }

        var metadataEntries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("metadata/", StringComparison.Ordinal) || entry.Length <= 0)
                continue;
            if (_ReadEntry(entry) is { } bytes) metadataEntries[entry.FullName] = bytes;
        }

        var baseRef = mua["base"] as JsonObject;
        var changes = mua["changes"] as JsonObject;
        var removed = changes?["removed"] as JsonArray;
        var removedPaths = new List<string>();
        if (removed is not null)
        {
            foreach (var item in removed.OfType<JsonObject>())
            {
                if (item["path"]?.ToString() is { Length: > 0 } path) removedPaths.Add(path);
            }
        }

        return new MuaPackInfo(
            instanceName!,
            serverId,
            targetRef["releaseId"]?.ToString() ?? release["releaseId"]?.ToString() ?? string.Empty,
            release["version"]?.ToString() ?? string.Empty,
            targetRef["manifestSha256"]?.ToString() ?? targetHash,
            mua["packageType"]?.ToString() ?? "full",
            baseRef?["releaseId"]?.ToString(),
            baseRef?["manifestSha256"]?.ToString(),
            changes?["runtimeChanged"] is not null,
            mua["removalPolicy"]?.ToString() ?? "retain",
            removed?.Count ?? 0,
            removedPaths,
            muaBytes,
            targetBytes,
            metadataEntries,
            optionalLocalPaths);
    }

    /// <summary>
    /// 安装失败或被中断后记录状态：保留实例名与服务器绑定，
    /// 但标记为需要完整安装，避免继续以旧版本为基准请求增量包。
    /// </summary>
    private static void SaveBrokenState(string instanceName, MuaPackInfo info,
        MuaPackInstalledState? previous)
    {
        try
        {
            var selected = GameFolders.mcFolderSelected;
            if (string.IsNullOrWhiteSpace(selected)) return;

            var instanceDir = Path.Combine(selected, "versions", instanceName);
            var stateDir = Path.Combine(instanceDir, ".mua");
            Directory.CreateDirectory(stateDir);

            var releaseId = string.IsNullOrWhiteSpace(previous?.ReleaseId) ? string.Empty : previous.ReleaseId;
            var version = string.IsNullOrWhiteSpace(previous?.Version) ? info.Version : previous.Version;
            var targetManifestSha256 = previous?.TargetManifestSha256 ?? string.Empty;

            var state = new JsonObject
            {
                ["format"] = "pcl-mua-state",
                ["formatVersion"] = 1,
                ["serverId"] = info.ServerId,
                ["releaseId"] = releaseId,
                ["version"] = version,
                ["targetManifestSha256"] = targetManifestSha256,
                ["packageFormatVersion"] = 2,
                ["needsFullInstall"] = true,
                ["failedAt"] = DateTimeOffset.UtcNow.ToString("O")
            };
            File.WriteAllText(Path.Combine(stateDir, "state.json"),
                state.ToJsonString(JsonCompat.SerializerOptions), new UTF8Encoding(false));

            LauncherCommon.Log($"[MuaPack] 安装未完成，已标记需要完整安装：{stateDir}");
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[MuaPack] 记录安装失败状态失败");
        }
    }

    private static void SaveInstallResult(MuaPackInfo info, string instanceName, JsonObject? previousManifest)
    {
        try
        {
            var selected = GameFolders.mcFolderSelected;
            if (string.IsNullOrWhiteSpace(selected)) return;

            var instanceDir = Path.Combine(selected, "versions", instanceName);
            var stateDir = Path.Combine(instanceDir, ".mua");
            Directory.CreateDirectory(stateDir);

            // 旧版本托管但新目标不再包含的文件需要清理（备份策略会先复制到 .mua/backup）
            CleanupRemovedFiles(instanceDir, previousManifest, info);

            // 保存 muapack 自身的内容，供后续更新/修复时对照
            File.WriteAllBytes(Path.Combine(stateDir, "mua-pack.json"), info.MuaPackBytes);
            foreach (var (entry, bytes) in info.MetadataEntries)
            {
                var relative = entry["metadata/".Length..];
                var path = Path.Combine(stateDir, "metadata",
                    relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }

            // Modrinth 安装器会解压全部 overrides，这里按 target-manifest 删除 optional 本地文件
            foreach (var relative in info.OptionalLocalPaths)
            {
                var path = Path.Combine(instanceDir, relative.Replace('/', Path.DirectorySeparatorChar));
                LauncherCommon.TryDeleteFile(path);
            }

            var state = new JsonObject
            {
                ["format"] = "pcl-mua-state",
                ["formatVersion"] = 1,
                ["serverId"] = info.ServerId,
                ["releaseId"] = info.ReleaseId,
                ["version"] = info.Version,
                ["targetManifestSha256"] = info.TargetManifestSha256,
                ["packageFormatVersion"] = 2,
                ["installedAt"] = DateTimeOffset.UtcNow.ToString("O")
            };
            File.WriteAllText(Path.Combine(stateDir, "state.json"),
                state.ToJsonString(JsonCompat.SerializerOptions), new UTF8Encoding(false));

            LauncherCommon.Log($"[MuaPack] 已保存安装状态：{stateDir}");
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[MuaPack] 保存安装状态失败");
        }
    }

    private static void CleanupRemovedFiles(string instanceDir, JsonObject? previousManifest, MuaPackInfo info)
    {
        try
        {
            if (JsonCompat.ParseNode(Encoding.UTF8.GetString(info.TargetManifestBytes)) is not JsonObject targetManifest)
                return;

            var newPaths = new HashSet<string>(StringComparer.Ordinal);
            if (targetManifest["files"] is JsonArray newFiles)
            {
                foreach (var file in newFiles.OfType<JsonObject>())
                {
                    if (file["path"]?.ToString() is { Length: > 0 } path) newPaths.Add(path);
                }
            }

            var candidates = new HashSet<string>(StringComparer.Ordinal);
            var oldPolicies = new Dictionary<string, string>(StringComparer.Ordinal);
            if (previousManifest?["files"] is JsonArray oldFiles)
            {
                foreach (var file in oldFiles.OfType<JsonObject>())
                {
                    var path = file["path"]?.ToString();
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var policy = file["policy"]?.ToString() ?? string.Empty;
                    oldPolicies[path] = policy;
                    if (newPaths.Contains(path)) continue;
                    // 只清理旧版本明确托管的文件，preserve 文件与玩家自建文件不动
                    if (!string.Equals(policy, "managed", StringComparison.OrdinalIgnoreCase)) continue;
                    candidates.Add(path);
                }
            }

            // 更新包自带的 removed 列表：旧 manifest 缺失时也能清理
            foreach (var path in info.RemovedPaths)
            {
                if (oldPolicies.TryGetValue(path, out var policy)
                    && !string.Equals(policy, "managed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.Add(path);
            }

            if (candidates.Count == 0) return;

            var backupManaged = string.Equals(info.RemovalPolicy, "backup-managed",
                StringComparison.OrdinalIgnoreCase);
            var oldRelease = previousManifest?["release"]?["releaseId"]?.ToString() ?? "previous";
            var instanceRoot = Path.GetFullPath(instanceDir) + Path.DirectorySeparatorChar;
            var backupRoot = Path.Combine(instanceDir, ".mua", "backup", oldRelease);
            var removed = 0;

            foreach (var relative in candidates)
            {
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) continue;
                var target = Path.GetFullPath(Path.Combine(instanceDir,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(instanceRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(target)) continue;

                if (backupManaged)
                {
                    var backup = Path.Combine(backupRoot,
                        relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    LauncherCommon.CopyFile(target, backup);
                }

                if (!LauncherCommon.TryDeleteFile(target)) continue;
                removed++;
            }

            if (removed > 0)
            {
                LauncherCommon.Log(
                    $"[MuaPack] 已清理 {removed} 个旧版本托管文件（removalPolicy={info.RemovalPolicy}）");
            }
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[MuaPack] 清理旧版本文件失败");
        }
    }

    private static JsonObject? ReadInstalledTargetManifest(string instanceName)
    {
        try
        {
            var selected = GameFolders.mcFolderSelected;
            if (string.IsNullOrWhiteSpace(selected)) return null;
            var path = Path.Combine(selected, "versions", instanceName,
                ".mua", "metadata", "target-manifest.json");
            if (!File.Exists(path)) return null;
            return JsonCompat.ParseNode(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[MuaPack] 读取旧版本目标清单失败");
            return null;
        }
    }

    private static string _Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static byte[]? _ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        return entry is null ? null : _ReadEntry(entry);
    }

    private static byte[]? _ReadEntry(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string _SafeFileName(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }
}
