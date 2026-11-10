using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;
using static PCL.Loading;

namespace PCL;

/// <summary>
/// muapack v2 的最小安装实现：先取出包内的 MUA 元数据，再复用现有 Modrinth 安装器安装内容。
/// 目前只支持完整包；更新与验签留到后续实现。
/// </summary>
public static class MuaPackInstallService
{
    private sealed record MuaPackInfo(
        string InstanceName,
        string ServerId,
        string ReleaseId,
        string Version,
        string TargetManifestSha256,
        byte[] MuaPackBytes,
        IReadOnlyDictionary<string, byte[]> MetadataEntries,
        IReadOnlyList<string> OptionalLocalPaths);

    /// <summary>
    /// 下载 Union 整合包并在完成后自动安装。
    /// </summary>
    public static void DownloadAndInstall(string url, long size, string sha256, string version, string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (string.IsNullOrWhiteSpace(sha256))
            throw new ArgumentException("Union 整合包缺少 SHA-256。", nameof(sha256));

        var folder = Path.Combine(Paths.Temp, "Union", serverId);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"{_SafeFileName(version, "pack")}.muapack");

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
            LauncherCommon.RunInNewThread(() => Install(target, serverId), "MuaPack install");
        };
        loader.Start(1);
        LoaderTaskbarAdd(loader);
    }

    /// <summary>
    /// 安装完整 muapack。必须在工作线程执行。
    /// </summary>
    public static LoaderCombo<string> Install(string muapackPath, string serverId, string? instanceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(muapackPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var info = ReadInfo(muapackPath, serverId, instanceName);
        var loader = Modpack.ModpackInstallWithoutRestriction(muapackPath, info.InstanceName, excludeOptional: true);
        loader.OnStateChangedUi += (_, state, _) =>
        {
            if (state != LoadState.Finished) return;
            LauncherCommon.RunInNewThread(() => SaveInstallResult(info), "MuaPack state");
        };
        return loader;
    }

    private static MuaPackInfo ReadInfo(string muapackPath, string serverId, string? instanceName)
    {
        using var stream = File.OpenRead(muapackPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var muaBytes = _ReadEntry(archive, "mua-pack.json")
                       ?? throw new Exception("muapack 缺少 mua-pack.json。");
        var targetBytes = _ReadEntry(archive, "metadata/target-manifest.json")
                          ?? throw new Exception("muapack 缺少 metadata/target-manifest.json。");

        var mua = JsonCompat.ParseNode(Encoding.UTF8.GetString(muaBytes)) as JsonObject
                  ?? throw new Exception("mua-pack.json 格式无效。");
        var targetManifest = JsonCompat.ParseNode(Encoding.UTF8.GetString(targetBytes)) as JsonObject
                             ?? throw new Exception("target-manifest.json 格式无效。");

        if (mua["formatVersion"]?.GetValue<int>() != 2 || mua["protocolRevision"]?.GetValue<int>() != 2)
            throw new Exception("仅支持 muapack v2。");
        if (!string.Equals(mua["packageType"]?.ToString(), "full", StringComparison.OrdinalIgnoreCase))
            throw new Exception("当前仅支持完整包安装，请先重新安装完整包。");

        var targetRef = mua["target"] as JsonObject
                        ?? throw new Exception("mua-pack.json 缺少 target。");
        var release = targetManifest["release"] as JsonObject
                      ?? throw new Exception("target-manifest.json 缺少 release。");
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

        return new MuaPackInfo(
            instanceName!,
            serverId,
            targetRef["releaseId"]?.ToString() ?? release["releaseId"]?.ToString() ?? string.Empty,
            release["version"]?.ToString() ?? string.Empty,
            targetRef["manifestSha256"]?.ToString() ?? string.Empty,
            muaBytes,
            metadataEntries,
            optionalLocalPaths);
    }

    private static void SaveInstallResult(MuaPackInfo info)
    {
        try
        {
            var selected = GameFolders.mcFolderSelected;
            if (string.IsNullOrWhiteSpace(selected)) return;

            var instanceDir = Path.Combine(selected, "versions", info.InstanceName);
            var stateDir = Path.Combine(instanceDir, ".mua");
            Directory.CreateDirectory(stateDir);

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
                if (File.Exists(path)) File.Delete(path);
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
                state.ToJsonString(JsonCompat.SerializerOptions), Encoding.UTF8);

            LauncherCommon.Log($"[MuaPack] 已保存安装状态：{stateDir}");
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[MuaPack] 保存安装状态失败");
        }
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
