// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using LMLoader.Core.Manifest;

namespace LMLoader.Distribution.Thunderstore;

/// <summary>
/// Thunderstore manifest.json 模型(平台 Schema v1)。
/// 与本格式 <see cref="ModManifest"/> 的转换规则见 <see cref="ThunderstoreAdapter"/>(D16)。
/// </summary>
public sealed class ThunderstoreManifest
{
	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("version_number")]
	public string VersionNumber { get; set; } = "";

	[JsonPropertyName("website_url")]
	public string WebsiteUrl { get; set; } = "";

	/// <summary>平台字段 namespace(团队);上传时由平台/CLI 填,转换时取 team。</summary>
	[JsonPropertyName("description")]
	public string Description { get; set; } = "";

	/// <summary>依赖引用 <c>Team-Name-1.2.3</c>(精确版本;平台禁止区间)。</summary>
	[JsonPropertyName("dependencies")]
	public List<string> Dependencies { get; set; } = [];

	/// <summary>反序列化 + 结构校验;失败抛 <see cref="FormatException"/>(含全部问题行)。</summary>
	public static ThunderstoreManifest Parse(string json)
	{
		var manifest = JsonSerializer.Deserialize<ThunderstoreManifest>(json, JsonOptions)
			?? throw new FormatException("Thunderstore manifest 反序列化为空");
		var errors = Validate(manifest);
		if (errors.Count > 0)
		{
			throw new FormatException("Thunderstore manifest 无效:\n" + string.Join("\n", errors));
		}

		return manifest;
	}

	public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

	/// <summary>平台规则校验(命名/版本/描述/依赖引用);返回问题行列表,空 = 合法。</summary>
	public static List<string> Validate(ThunderstoreManifest manifest)
	{
		var errors = new List<string>();
		if (string.IsNullOrEmpty(manifest.Name) ||
			manifest.Name.Length > ThunderstoreRules.MaxPackageNameLength ||
			!ThunderstoreRules.PackageName().IsMatch(manifest.Name))
		{
			errors.Add($"name: \"{manifest.Name}\" 不合法(仅 a-zA-Z0-9_,≤{ThunderstoreRules.MaxPackageNameLength})");
		}

		if (!ThunderstoreRules.VersionNumber().IsMatch(manifest.VersionNumber))
		{
			errors.Add($"version_number: \"{manifest.VersionNumber}\" 不是规范 M.m.p(禁前导零、禁 prerelease/build)");
		}

		if (manifest.Description.Length > ThunderstoreRules.MaxDescriptionLength)
		{
			errors.Add($"description: 长度 {manifest.Description.Length} 超过 {ThunderstoreRules.MaxDescriptionLength}");
		}

		foreach (var dependency in manifest.Dependencies)
		{
			if (!ThunderstoreRules.DependencyReference().IsMatch(dependency))
			{
				errors.Add($"dependencies: \"{dependency}\" 不是 Team-Name-M.m.p 引用(精确版本)");
			}
		}

		return errors;
	}

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
}
