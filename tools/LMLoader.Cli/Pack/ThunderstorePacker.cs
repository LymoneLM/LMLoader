// SPDX-License-Identifier: LGPL-3.0-or-later

using System.IO.Compression;
using LMLoader.Core.Manifest;
using LMLoader.Distribution.Thunderstore;

namespace LMLoader.Cli.Pack;

/// <summary>Thunderstore 打包结果。</summary>
public sealed record ThunderstorePackResult(string ZipPath, string ManifestName, string VersionNumber);

/// <summary>
/// Thunderstore 包打包:模组目录全量进 zip(含 mod.json——loader 在
/// Workshop/解压目录按 mod.json 发现,Thunderstore 包解压后即原生格式)+ 根目录生成
/// manifest.json(经 <see cref="ThunderstoreAdapter"/>)。
/// 平台硬约束:根目录必须含 icon.png(缺失/非 png → 打包失败)。
/// </summary>
public static class ThunderstorePacker
{
	public static ThunderstorePackResult Pack(
		string modDirectory,
		string team,
		string zipPath,
		IReadOnlyDictionary<string, ModManifest>? dependencyManifests = null,
		Action<string>? warn = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(team);
		var dir = Path.GetFullPath(modDirectory);
		if (!Directory.Exists(dir))
		{
			throw new DirectoryNotFoundException($"模组目录不存在: {dir}");
		}

		var read = ModManifestReader.ReadFile(
			Directory.EnumerateFiles(dir, "*mod.json", SearchOption.TopDirectoryOnly).First());
		if (!read.Success)
		{
			throw new FormatException("mod.json 无效,先运行 lint:\n" + string.Join("\n", read.Errors));
		}

		var manifest = read.Manifest!;
		var tsManifest = ThunderstoreAdapter.ToThunderstore(
			manifest, team, dependencyManifests ?? new Dictionary<string, ModManifest>(), warn);

		// icon.png:平台必须。目录根已有 icon.png 直接用;否则取显式 icon(须 png)在 zip 内改名——
		// 不修改作者源目录(打包不应有副作用)
		string? iconSource = null;
		if (File.Exists(Path.Combine(dir, "icon.png")))
		{
			iconSource = null; // 随目录全量枚举自然进入
		}
		else if (manifest.Icon is { } icon && icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
			&& File.Exists(Path.Combine(dir, icon)))
		{
			iconSource = Path.Combine(dir, icon);
			warn?.Invoke($"zip 内将 {icon} 改名为 icon.png(平台要求根目录 icon.png)");
		}
		else
		{
			throw new FileNotFoundException("根目录缺 icon.png(Thunderstore 上传必需;icon 字段须指向 png)");
		}

		var fullZip = Path.GetFullPath(zipPath);
		var zipDir = Path.GetDirectoryName(fullZip);
		if (!string.IsNullOrEmpty(zipDir))
		{
			Directory.CreateDirectory(zipDir);
		}

		using var stream = new FileStream(fullZip, FileMode.Create);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
		var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// 1. 生成的 manifest.json 在根
		AddEntry(archive, "manifest.json", tsManifest.Serialize());
		added.Add("manifest.json");

		if (iconSource is not null)
		{
			archive.CreateEntryFromFile(iconSource, "icon.png", CompressionLevel.Optimal);
			added.Add("icon.png");
		}

		// 2. 模组目录全量(相对路径进 zip 根;排除目录内既有的 manifest.json 与输出 zip 自身防冲突/自吞)
		foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
		{
			if (string.Equals(Path.GetFullPath(file), fullZip, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
			if (!added.Add(relative))
			{
				continue;
			}

			archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
		}

		// 3. 无 README.md 时用 description 生成(平台展示页)
		if (!added.Contains("README.md") && !string.IsNullOrEmpty(manifest.Description))
		{
			AddEntry(archive, "README.md", $"# {manifest.Name}\n\n{manifest.Description}\n");
		}

		return new ThunderstorePackResult(fullZip, tsManifest.Name, tsManifest.VersionNumber);
	}

	private static void AddEntry(ZipArchive archive, string entryName, string content)
	{
		var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
		using var writer = new StreamWriter(entry.Open());
		writer.Write(content);
	}
}
