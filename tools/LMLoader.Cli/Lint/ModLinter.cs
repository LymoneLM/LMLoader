// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using LMLoader.Core.Manifest;
using LMLoader.Distribution.Thunderstore;

namespace LMLoader.Cli.Lint;

/// <summary>lint 问题;error 使 lint 失败,warning 提示但不失败。</summary>
public sealed record LintIssue(bool IsError, string Message);

/// <summary>lint 结果;<see cref="Success"/> = 无 error。</summary>
public sealed record LintResult(bool Success, IReadOnlyList<LintIssue> Issues);

/// <summary>
/// mod.json 作者侧严格校验(D18)。与运行时 reader 的宽容语义互补:
/// 运行时"忽略并继续"的行为在此一律升级为 error(作者必须在发布前修正),
/// 另加磁盘一致性检查(assemblies/pck/icon 文件存在)、清单 uid ↔ 代码 [LmMod] 特性
/// 交叉校验(元数据读取,不加载程序集)与 Thunderstore 别名预校验。
/// </summary>
public static partial class ModLinter
{
	[GeneratedRegex("^[a-zA-Z0-9_.-]+$")]
	private static partial Regex TeamName();

	/// <summary>严格字段白名单(= 运行时已知字段;额外含 distribution 树)。</summary>
	private static readonly Dictionary<string, string[]> StrictFields = new()
	{
		[""] = ["schemaVersion", "mods", "gameId", "loaderVersion", "version", "name", "authors",
			"description", "icon", "website", "tags", "distribution"],
		["mods[]"] = ["uid", "assemblies", "pck", "depends", "version", "name", "authors",
			"description", "icon", "website", "tags"],
		["mods[].depends[]"] = ["uid", "version", "soft"],
		["distribution"] = ["thunderstore"],
		["distribution.thunderstore"] = ["team", "name"],
	};

	public static LintResult Lint(string modDirectory)
	{
		var issues = new List<LintIssue>();
		var dir = Path.GetFullPath(modDirectory);
		if (!Directory.Exists(dir))
		{
			return Fail($"目录不存在: {dir}");
		}

		// ---- 1. 清单文件唯一性 ----
		var manifestFiles = Directory.EnumerateFiles(dir, "*mod.json", SearchOption.TopDirectoryOnly)
			.OrderBy(p => p, StringComparer.Ordinal)
			.ToArray();
		switch (manifestFiles.Length)
		{
			case 0:
				return Fail($"目录中无 *mod.json 清单: {dir}");
			case > 1:
				issues.Add(new LintIssue(true,
					$"目录存在 {manifestFiles.Length} 份清单(仅允许一份): {string.Join(", ", manifestFiles.Select(Path.GetFileName))}"));
				break;
		}

		var manifestPath = manifestFiles[0];

		// ---- 2. 运行时解析(其错误与"宽容忽略"类警告一律升级为 error) ----
		var read = ModManifestReader.ReadFile(manifestPath);
		issues.AddRange(read.Errors.Select(e => new LintIssue(true, e)));
		issues.AddRange(read.Warnings.Select(w => new LintIssue(true, $"运行时会忽略以下内容,请修正: {w}")));
		if (!read.Success)
		{
			return new LintResult(false, issues);
		}

		var manifest = read.Manifest!;

		// ---- 3. 未知字段(严格;运行时只警告) ----
		var json = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
		CheckUnknownFields(json, "", issues);

		// ---- 4. gameId 缺省提示(合法,但须是作者的显式选择,D18) ----
		if (string.IsNullOrEmpty(manifest.GameId))
		{
			issues.Add(new LintIssue(false,
				"未声明 gameId:该清单将装载于任何设置了本 loader 的宿主游戏(通用模组);如仅面向特定游戏请显式声明"));
		}

		// ---- 5. 磁盘一致性(per-mod assemblies/pck + 各级 icon) ----
		foreach (var mod in manifest.Mods)
		{
			foreach (var assembly in mod.Assemblies)
			{
				if (!File.Exists(Path.Combine(dir, assembly)))
				{
					issues.Add(new LintIssue(true,
						$"模组 \"{mod.Uid}\" 的 assemblies 文件不存在: {assembly}(先构建模组工程)"));
				}
			}

			foreach (var pck in mod.PckResources)
			{
				if (!File.Exists(Path.Combine(dir, pck)))
				{
					issues.Add(new LintIssue(true,
						$"模组 \"{mod.Uid}\" 的 pck 文件不存在: {pck}(用 lmcli pack-pck / godot --export-pack 导出后随包分发)"));
				}
				else if (!pck.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
				{
					issues.Add(new LintIssue(false, $"模组 \"{mod.Uid}\" 的 pck: \"{pck}\" 扩展名非 .pck"));
				}
			}
		}

		foreach (var icon in new[] { manifest.Icon }.Concat(manifest.Mods.Select(m => m.Icon))
			.Where(i => i is not null).Select(i => i!))
		{
			if (!File.Exists(Path.Combine(dir, icon)))
			{
				issues.Add(new LintIssue(true, $"icon 文件不存在: {icon}"));
			}
			else if (!icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
			{
				issues.Add(new LintIssue(false, $"icon: \"{icon}\" 非 png(Thunderstore 平台要求根目录 256x256 icon.png)"));
			}
		}

		// ---- 6. 清单 uid ↔ 代码 [LmMod] 特性交叉校验(元数据读取,不加载程序集,D18) ----
		CheckAttributeBinding(dir, manifest, issues);

		// ---- 7. Thunderstore 别名预校验(打包前早失败) ----
		if (manifest.Distribution is { } distribution)
		{
			if (distribution.ThunderstoreTeam is { } team && !TeamName().IsMatch(team))
			{
				issues.Add(new LintIssue(true, $"distribution.thunderstore.team: \"{team}\" 含非法字符(仅 a-zA-Z0-9_.-)"));
			}

			if (distribution.ThunderstoreName is { } name && !ThunderstoreRules.PackageName().IsMatch(name))
			{
				issues.Add(new LintIssue(true,
					$"distribution.thunderstore.name: \"{name}\" 不合法(仅 a-zA-Z0-9_,≤{ThunderstoreRules.MaxPackageNameLength})"));
			}
		}

		// ---- 8. 发布质量提示(不失败) ----
		if (string.IsNullOrEmpty(manifest.Description))
		{
			issues.Add(new LintIssue(false, "缺少 description(分发平台展示与 README 生成来源)"));
		}

		if (manifest.Tags.Count == 0)
		{
			issues.Add(new LintIssue(false, "缺少 tags(管理器/平台过滤用)"));
		}

		return new LintResult(issues.All(i => !i.IsError), issues);

		LintResult Fail(string message) => new(false, [new LintIssue(true, message)]);
	}

	/// <summary>
	/// 扫描各模组声明的 assemblies,将代码侧 [LmMod(uid)] 与清单 mods[].uid 比对:
	/// 声明有代码却无对应类 → error;特性 uid 未声明 → warning;同 uid 多类型 → error(绑定歧义)。
	/// </summary>
	private static void CheckAttributeBinding(string dir, ModManifest manifest, List<LintIssue> issues)
	{
		var declaredUids = manifest.Mods.Select(m => m.Uid).ToHashSet(StringComparer.Ordinal);
		var uidSources = new Dictionary<string, List<string>>(StringComparer.Ordinal);

		foreach (var mod in manifest.Mods.Where(m => m.Assemblies.Count > 0))
		{
			foreach (var assembly in mod.Assemblies)
			{
				var path = Path.Combine(dir, assembly);
				if (!File.Exists(path))
				{
					continue; // 缺文件已在上一步报错,此处不重复
				}

				IReadOnlyList<string> uids;
				try
				{
					uids = LmModAttributeScanner.ScanUids(path);
				}
				catch (Exception ex) when (ex is BadImageFormatException or IOException)
				{
					issues.Add(new LintIssue(true, $"无法读取程序集元数据: {assembly}({ex.Message})"));
					continue;
				}

				foreach (var uid in uids)
				{
					if (!uidSources.TryGetValue(uid, out var sources))
					{
						uidSources[uid] = sources = new List<string>();
					}

					sources.Add($"{assembly}: {uid}");
				}
			}
		}

		foreach (var mod in manifest.Mods.Where(m => m.Assemblies.Count > 0))
		{
			if (!uidSources.ContainsKey(mod.Uid))
			{
				issues.Add(new LintIssue(true,
					$"模组 \"{mod.Uid}\" 声明了 assemblies 但其程序集中没有标注 [LmMod(\"{mod.Uid}\")] 的类型" +
					"(确认类已标注 LMLoader.Api.LmModAttribute 且为 public 非 abstract)"));
			}
		}

		foreach (var (uid, sources) in uidSources)
		{
			if (!declaredUids.Contains(uid))
			{
				issues.Add(new LintIssue(false,
					$"程序集中存在未声明的 [LmMod(\"{uid}\")]({string.Join("; ", sources.Distinct())})——" +
					$"运行时将告警并忽略;请在 mods[] 中声明该 uid 或移除特性"));
			}
			else if (sources.Count > 1)
			{
				issues.Add(new LintIssue(true,
					$"uid \"{uid}\" 被多个 [LmMod] 特性声明,运行时绑定歧义: {string.Join("; ", sources.Distinct())}"));
			}
		}
	}

	private static void CheckUnknownFields(JsonElement element, string path, List<LintIssue> issues)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		var known = StrictFields.GetValueOrDefault(path,
			// 依赖数组嵌在 mods[] 内:先判 depends[] 再判 mods[](路径同时包含两者)
			path.Contains(".depends[", StringComparison.Ordinal)
				? StrictFields["mods[].depends[]"]
				: path.Contains(".mods[", StringComparison.Ordinal)
					? StrictFields["mods[]"]
					: Array.Empty<string>());

		foreach (var property in element.EnumerateObject())
		{
			var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
			if (!known.Contains(property.Name, StringComparer.Ordinal))
			{
				issues.Add(new LintIssue(true, $"未知字段: {childPath}(当前 schema 不含该字段)"));
				continue;
			}

			// 数组元素路径替换为 [i] 语义上等价的集合名,逐元素下钻
			if (property.Name is "mods" or "depends" && property.Value.ValueKind == JsonValueKind.Array)
			{
				var prefix = path.Length == 0 ? "" : path + ".";
				var itemPath = $"{prefix}{property.Name}[]";
				foreach (var item in property.Value.EnumerateArray())
				{
					CheckUnknownFields(item, itemPath, issues);
				}
			}
			else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
			{
				CheckUnknownFields(property.Value, childPath, issues);
			}
		}
	}
}
