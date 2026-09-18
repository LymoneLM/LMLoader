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
/// mod.json 作者侧严格校验(6.4)。与运行时 reader 的宽容语义互补:
/// 运行时"忽略并继续"的行为在此一律升级为 error(作者必须在发布前修正),
/// 另加磁盘一致性检查(入口程序集/图标/pck 文件存在)与 Thunderstore 别名预校验(D16)。
/// </summary>
public static partial class ModLinter
{
	[GeneratedRegex("^[a-zA-Z0-9_.-]+$")]
	private static partial Regex TeamName();

	/// <summary>严格字段白名单(= 运行时已知字段;额外含 distribution 树)。</summary>
	private static readonly Dictionary<string, string[]> StrictFields = new()
	{
		[""] = ["schemaVersion", "uid", "name", "version", "authors", "description", "icon", "website",
			"tags", "gameId", "loaderVersion", "entry", "resources", "distribution"],
		["entry"] = ["assembly", "modules"],
		["resources"] = ["pck"],
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

		// ---- 4. 磁盘一致性 ----
		var assemblyPath = Path.Combine(dir, manifest.EntryAssembly);
		if (!File.Exists(assemblyPath))
		{
			issues.Add(new LintIssue(true, $"entry.assembly 文件不存在: {manifest.EntryAssembly}(先构建模组工程)"));
		}

		foreach (var pck in manifest.PckResources)
		{
			var pckPath = Path.Combine(dir, pck);
			if (!File.Exists(pckPath))
			{
				issues.Add(new LintIssue(true, $"resources.pck 文件不存在: {pck}(用 godot --export-pack 导出后随包分发)"));
			}
			else if (!pck.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
			{
				issues.Add(new LintIssue(false, $"resources.pck: \"{pck}\" 扩展名非 .pck"));
			}
		}

		if (manifest.Icon is { } icon)
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

		// ---- 5. Thunderstore 别名预校验(D16;打包前早失败) ----
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

		// ---- 6. 发布质量提示(不失败) ----
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

	private static void CheckUnknownFields(JsonElement element, string path, List<LintIssue> issues)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		var known = StrictFields.GetValueOrDefault(path, path.Contains(".modules[", StringComparison.Ordinal)
			? ["uid", "type", "depends"]
			: path.Contains(".depends[", StringComparison.Ordinal)
				? ["uid", "version", "soft"]
				: Array.Empty<string>());

		foreach (var property in element.EnumerateObject())
		{
			var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
			if (!known.Contains(property.Name, StringComparer.Ordinal))
			{
				issues.Add(new LintIssue(true, $"未知字段: {childPath}(schema v1.0 不含该字段)"));
				continue;
			}

			// 数组元素路径替换为 [i] 语义上等价的集合名,逐元素下钻
			if (property.Name is "modules" or "depends" && property.Value.ValueKind == JsonValueKind.Array)
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
