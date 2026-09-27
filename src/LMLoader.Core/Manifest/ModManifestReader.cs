// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Text.Json;
using LMLoader.Core.Versioning;

namespace LMLoader.Core.Manifest;

/// <summary>
/// mod.json 读取器(schema v1,D18:文件级声明 + 并列模组列表)。
/// 容错语义:未知字段忽略+警告;schemaVersion 主版本不识别拒载;错误累积返回(不抛出)。
/// 继承在此展开:每个 <see cref="ModEntry"/> 携带解析后的最终值
/// (模组级声明覆盖文件级,整项替换;version 两级皆缺为错误)。
/// </summary>
public static class ModManifestReader
{
	/// <summary>当前加载器支持的清单主版本。</summary>
	public const int SupportedSchemaVersion = 1;

	private static readonly JsonDocumentOptions DocumentOptions = new()
	{
		AllowTrailingCommas = true,
		CommentHandling = JsonCommentHandling.Skip,
	};

	private const string UidPattern = @"^[a-zA-Z][a-zA-Z0-9_-]*(\.[a-zA-Z][a-zA-Z0-9_-]*)+$";

	/// <summary>从文件读取;文件不可读视为致命错误。</summary>
	public static ManifestReadResult ReadFile(string manifestPath)
	{
		string json;
		try
		{
			json = File.ReadAllText(manifestPath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return ManifestReadResult.Fail([$"{manifestPath}: 无法读取清单文件({ex.Message})"], Array.Empty<string>());
		}

		return ReadJson(json, manifestPath);
	}

	/// <summary>从 JSON 文本读取。<paramref name="sourcePath"/> 仅用于诊断信息。</summary>
	public static ManifestReadResult ReadJson(string json, string sourcePath)
	{
		var errors = new List<string>();
		var warnings = new List<string>();

		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(json, DocumentOptions);
		}
		catch (JsonException ex)
		{
			return ManifestReadResult.Fail([$"{sourcePath}: JSON 解析失败({ex.Message})"], warnings);
		}

		using (document)
		{
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				errors.Add($"{sourcePath}: 清单根节点必须是 JSON 对象");
				return ManifestReadResult.Fail(errors, warnings);
			}

			var file = CollectUniqueProperties(root, "", warnings);

			// schemaVersion:主版本不识别 → 拒载
			if (!TryGetRequiredInt(file, "schemaVersion", errors, out var schemaVersion))
			{
				return ManifestReadResult.Fail(errors, warnings);
			}

			if (schemaVersion != SupportedSchemaVersion)
			{
				errors.Add(
					$"schemaVersion: 主版本 {schemaVersion} 不受支持(当前支持 {SupportedSchemaVersion}),已拒载;" +
					"请升级加载器或调整清单");
				return ManifestReadResult.Fail(errors, warnings);
			}

			// ---- 文件级声明块(全部可选,作为模组继承默认值) ----
			var fileGameId = GetOptionalString(file, "gameId");
			VersionRange? loaderVersion = null;
			if (file.TryGetValue("loaderVersion", out var loaderElement))
			{
				if (loaderElement.ValueKind == JsonValueKind.String &&
					VersionRange.TryParse(loaderElement.GetString(), out var parsedLoader))
				{
					loaderVersion = parsedLoader;
				}
				else
				{
					errors.Add(FormatVersionError("loaderVersion", loaderElement.ValueKind == JsonValueKind.String
						? loaderElement.GetString() ?? ""
						: "(非字符串)"));
				}
			}

			var fileVersion = ReadOptionalSemVer(file, "version", errors);
			var fileName = GetOptionalString(file, "name");
			var fileAuthors = ReadStringArray(file, "authors", warnings);
			var fileDescription = GetOptionalString(file, "description");
			var fileIcon = GetOptionalString(file, "icon");
			var fileWebsite = GetOptionalString(file, "website");
			var fileTags = ReadStringArray(file, "tags", warnings); // 展示字段
			var distribution = ReadDistribution(file, errors, warnings);

			// ---- 模组列表(唯一必填集合) ----
			var mods = ReadMods(file, fileName, fileVersion, fileAuthors, fileDescription,
				fileIcon, fileWebsite, fileTags, errors, warnings);

			if (errors.Count > 0 || mods.Length == 0)
			{
				if (errors.Count == 0)
				{
					errors.Add("mods: 至少需要一个模组");
				}

				return ManifestReadResult.Fail(errors, warnings);
			}

			var manifest = new ModManifest
			{
				SchemaVersion = schemaVersion,
				GameId = fileGameId,
				LoaderVersion = loaderVersion,
				Authors = fileAuthors,
				Description = fileDescription,
				Icon = fileIcon,
				Website = fileWebsite,
				Tags = fileTags,
				Distribution = distribution,
				Mods = mods,
				SourcePath = sourcePath,
				Directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? "",
			};

			return ManifestReadResult.Ok(manifest, warnings);
		}
	}

	private static ModEntry[] ReadMods(
		Dictionary<string, JsonElement> file,
		string? fileName,
		SemVer? fileVersion,
		string[] fileAuthors,
		string? fileDescription,
		string? fileIcon,
		string? fileWebsite,
		string[] fileTags,
		List<string> errors,
		List<string> warnings)
	{
		if (!file.TryGetValue("mods", out var modsElement))
		{
			errors.Add("mods: 缺少必填字段");
			return Array.Empty<ModEntry>();
		}

		if (modsElement.ValueKind != JsonValueKind.Array)
		{
			errors.Add("mods: 必须是数组");
			return Array.Empty<ModEntry>();
		}

		var result = new List<ModEntry>();
		var seenUids = new HashSet<string>(StringComparer.Ordinal);

		var index = 0;
		foreach (var modElement in modsElement.EnumerateArray())
		{
			var path = $"mods[{index}]";
			index++;

			if (modElement.ValueKind != JsonValueKind.Object)
			{
				errors.Add($"{path}: 必须是对象");
				continue;
			}

			var mod = CollectUniqueProperties(modElement, path, warnings);

			if (!TryGetRequiredString(mod, "uid", errors, out var uid))
			{
				continue;
			}

			if (!IsValidUid(uid))
			{
				errors.Add($"{path}.uid: \"{uid}\" 不是合法的反向域名结构");
			}

			if (!seenUids.Add(uid))
			{
				errors.Add($"{path}.uid: 模组 uid \"{uid}\" 在清单内重复定义(双方均放弃加载)");
			}

			// ---- 继承展开:模组级声明覆盖文件级(整项替换) ----
			var version = ReadOptionalSemVer(mod, "version", errors, path) ?? fileVersion;
			if (version is null)
			{
				errors.Add($"{path}.version: 缺失(文件级与模组级均未声明,依赖区间判定需要版本锚)");
			}

			var name = GetOptionalString(mod, "name") ?? fileName ?? DefaultName(uid);
			// 继承覆盖:模组级声明合法才覆盖;类型错误(非数组)警告后回退文件级,避免"错误声明以空值覆盖继承"
			var authors = ReadOverrideArray(mod, "authors", path, warnings) ?? fileAuthors;
			var description = GetOptionalString(mod, "description") ?? fileDescription;
			var icon = GetOptionalString(mod, "icon") ?? fileIcon;
			var website = GetOptionalString(mod, "website") ?? fileWebsite;
			var tags = ReadOverrideArray(mod, "tags", path, warnings) ?? fileTags;

			// ---- assemblies(可选:缺省 = 纯资源/依赖锚模组) ----
			var assemblies = Array.Empty<string>();
			if (mod.TryGetValue("assemblies", out var assembliesElement))
			{
				if (assembliesElement.ValueKind == JsonValueKind.Array)
				{
					var list = new List<string>();
					var assemblyIndex = 0;
					foreach (var item in assembliesElement.EnumerateArray())
					{
						var itemPath = $"{path}.assemblies[{assemblyIndex}]";
						assemblyIndex++;
						if (item.ValueKind != JsonValueKind.String || !IsValidAssemblyName(item.GetString() ?? ""))
						{
							errors.Add(
								$"{itemPath}: 必须是相对模组目录的 .dll 文件名,不得包含路径分隔符");
							continue;
						}

						list.Add(item.GetString()!);
					}

					assemblies = list.ToArray();
				}
				else
				{
					errors.Add($"{path}.assemblies: 必须是数组");
				}
			}

			var depends = ReadDependencies(mod, path, errors, warnings);
			var pck = ReadPck(mod, path, warnings);

			result.Add(new ModEntry
			{
				Uid = uid,
				Version = version ?? default, // 缺失时已记错误;errors 非空则清单不会返回
				Name = name,
				Authors = authors,
				Description = description,
				Icon = icon,
				Website = website,
				Tags = tags,
				Assemblies = assemblies,
				PckResources = pck,
				Depends = depends,
			});
		}

		return result.ToArray();
	}

	private static ModDependency[] ReadDependencies(
		Dictionary<string, JsonElement> modProperties,
		string modPath,
		List<string> errors,
		List<string> warnings)
	{
		if (!modProperties.TryGetValue("depends", out var dependsElement))
		{
			return Array.Empty<ModDependency>();
		}

		if (dependsElement.ValueKind != JsonValueKind.Array)
		{
			errors.Add($"{modPath}.depends: 必须是数组");
			return Array.Empty<ModDependency>();
		}

		var result = new List<ModDependency>();
		var index = 0;
		foreach (var dependencyElement in dependsElement.EnumerateArray())
		{
			var path = $"{modPath}.depends[{index}]";
			index++;

			if (dependencyElement.ValueKind != JsonValueKind.Object)
			{
				errors.Add($"{path}: 必须是对象");
				continue;
			}

			var properties = CollectUniqueProperties(dependencyElement, path, warnings);

			if (!TryGetRequiredString(properties, "uid", errors, out var uid))
			{
				continue;
			}

			if (!IsValidUid(uid))
			{
				errors.Add($"{path}.uid: \"{uid}\" 不是合法的反向域名结构");
			}

			SemVer? version = null;
			VersionRange? range = null;
			if (properties.TryGetValue("version", out var versionElement))
			{
				if (versionElement.ValueKind == JsonValueKind.String)
				{
					var versionText = versionElement.GetString() ?? "";
					// 优先按区间解析(^ ~ 比较符);裸精确版本两者皆可,区间保留原语法
					if (VersionRange.TryParse(versionText, out var parsedRange))
					{
						range = parsedRange;
						if (SemVer.TryParse(versionText, out var parsedExact))
						{
							version = parsedExact; // 精确语义回退(SemVer.TryParse 对裸精确成功)
						}
					}
					else
					{
						errors.Add(FormatVersionError($"{path}.version", versionText));
					}
				}
				else
				{
					errors.Add($"{path}.version: 必须是字符串");
				}
			}

			var soft = false;
			if (properties.TryGetValue("soft", out var softElement))
			{
				if (softElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
				{
					soft = softElement.GetBoolean();
				}
				else
				{
					errors.Add($"{path}.soft: 必须是布尔值");
				}
			}

			result.Add(new ModDependency(uid, version, soft) { Range = range });
		}

		return result.ToArray();
	}

	private static string[] ReadPck(Dictionary<string, JsonElement> modProperties, string modPath, List<string> warnings)
	{
		if (!modProperties.TryGetValue("pck", out var pckElement))
		{
			return Array.Empty<string>();
		}

		if (pckElement.ValueKind != JsonValueKind.Array)
		{
			warnings.Add($"{modPath}.pck: 应为数组,已忽略");
			return Array.Empty<string>();
		}

		var result = new List<string>();
		foreach (var pathElement in pckElement.EnumerateArray())
		{
			if (pathElement.ValueKind != JsonValueKind.String)
			{
				warnings.Add($"{modPath}.pck: 含非字符串项,已忽略该项");
				continue;
			}

			var pckPath = pathElement.GetString() ?? "";
			if (!IsValidRelativePath(pckPath))
			{
				warnings.Add($"{modPath}.pck: \"{pckPath}\" 不是合法的相对路径(禁止绝对路径与 .. 上跳),已忽略该项");
				continue;
			}

			result.Add(pckPath);
		}

		return result.ToArray();
	}

	/// <summary>枚举对象属性;重复键警告(取最后出现的值);未知键警告(宽容读入)。</summary>
	private static Dictionary<string, JsonElement> CollectUniqueProperties(
		JsonElement element,
		string path,
		List<string> warnings)
	{
		var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		var known = path.Length == 0 ? KnownTopLevelFields : KnownNestedFields(path);

		foreach (var property in element.EnumerateObject())
		{
			if (result.TryGetValue(property.Name, out _))
			{
				warnings.Add($"{PathLabel(path, property.Name)}: 字段重复定义,采用最后一个值");
			}

			result[property.Name] = property.Value;

			if (!known.Contains(property.Name))
			{
				warnings.Add($"{PathLabel(path, property.Name)}: 未知字段,已忽略(前向兼容)");
			}
		}

		return result;
	}

	private static string PathLabel(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

	private static readonly string[] KnownTopLevelFields =
	[
		"schemaVersion", "mods", "gameId", "loaderVersion", "version", "name", "authors",
		"description", "icon", "website", "tags", "distribution",
	];

	private static string[] KnownNestedFields(string path) =>
		path.Contains(".depends[", StringComparison.Ordinal)
			? ["uid", "version", "soft"]
			: path.StartsWith("mods[", StringComparison.Ordinal)
				? ["uid", "assemblies", "pck", "depends", "version", "name", "authors",
					"description", "icon", "website", "tags"]
				: path switch
				{
					"distribution" => ["thunderstore"],
					_ => Array.Empty<string>(),
				};

	/// <summary>读入 distribution(分发别名);结构不对仅警告并忽略(loader 不消费此字段)。</summary>
	private static ModDistribution? ReadDistribution(
		Dictionary<string, JsonElement> properties, List<string> errors, List<string> warnings)
	{
		if (!properties.TryGetValue("distribution", out var distributionElement))
		{
			return null;
		}

		if (distributionElement.ValueKind != JsonValueKind.Object)
		{
			errors.Add("distribution: 必须是对象");
			return null;
		}

		if (!distributionElement.TryGetProperty("thunderstore", out var ts) || ts.ValueKind != JsonValueKind.Object)
		{
			warnings.Add("distribution: 无 thunderstore 节,忽略");
			return null;
		}

		string? team = ts.TryGetProperty("team", out var teamElement) && teamElement.ValueKind == JsonValueKind.String
			? teamElement.GetString()
			: null;
		string? name = ts.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
			? nameElement.GetString()
			: null;
		if (team is null && name is null)
		{
			warnings.Add("distribution.thunderstore: team/name 均缺省,忽略");
			return null;
		}

		return new ModDistribution(team, name);
	}

	private static string[] ReadStringArray(
		Dictionary<string, JsonElement> properties,
		string field,
		List<string> warnings,
		string? path = null)
	{
		if (!properties.TryGetValue(field, out var element))
		{
			return Array.Empty<string>();
		}

		if (element.ValueKind != JsonValueKind.Array)
		{
			warnings.Add($"{PathLabel(path ?? "", field)}: 应为数组,已忽略");
			return Array.Empty<string>();
		}

		var result = new List<string>();
		foreach (var item in element.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
			{
				result.Add(value);
			}
			else
			{
				warnings.Add($"{PathLabel(path ?? "", field)}: 含空或非字符串项,已忽略该项");
			}
		}

		return result.ToArray();
	}

	/// <summary>读模组级继承覆盖字段:未声明或类型错误(警告)返回 null → 调用方回退文件级继承。</summary>
	private static string[]? ReadOverrideArray(
		Dictionary<string, JsonElement> properties,
		string field,
		string? path,
		List<string> warnings)
	{
		if (!properties.TryGetValue(field, out var element))
		{
			return null;
		}

		if (element.ValueKind != JsonValueKind.Array)
		{
			warnings.Add($"{PathLabel(path ?? "", field)}: 应为数组,已忽略(回退文件级继承)");
			return null;
		}

		return ReadStringArray(properties, field, warnings, path);
	}

	private static SemVer? ReadOptionalSemVer(
		Dictionary<string, JsonElement> properties, string field, List<string> errors, string? path = null)
	{
		if (!properties.TryGetValue(field, out var element))
		{
			return null;
		}

		if (element.ValueKind == JsonValueKind.String &&
			SemVer.TryParse(element.GetString(), out var parsed))
		{
			return parsed;
		}

		errors.Add(FormatVersionError(PathLabel(path ?? "", field),
			element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "(非字符串)"));
		return null;
	}

	private static string? GetOptionalString(Dictionary<string, JsonElement> properties, string field)
	{
		if (!properties.TryGetValue(field, out var element) || element.ValueKind != JsonValueKind.String)
		{
			return null;
		}

		var value = element.GetString();
		return string.IsNullOrEmpty(value) ? null : value;
	}

	private static bool TryGetRequiredString(
		Dictionary<string, JsonElement> properties,
		string field,
		List<string> errors,
		out string value)
	{
		value = "";

		if (!properties.TryGetValue(field, out var element))
		{
			errors.Add($"{field}: 缺少必填字段");
			return false;
		}

		if (element.ValueKind != JsonValueKind.String)
		{
			errors.Add($"{field}: 必须是字符串");
			return false;
		}

		value = element.GetString() ?? "";
		if (value.Length == 0)
		{
			errors.Add($"{field}: 不能为空字符串");
			return false;
		}

		return true;
	}

	private static bool TryGetRequiredInt(
		Dictionary<string, JsonElement> properties,
		string field,
		List<string> errors,
		out int value)
	{
		value = 0;

		if (!properties.TryGetValue(field, out var element))
		{
			errors.Add($"{field}: 缺少必填字段");
			return false;
		}

		if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
		{
			errors.Add($"{field}: 必须是整数");
			return false;
		}

		return true;
	}

	private static string DefaultName(string uid) => uid.Split('.')[^1];

	private static string FormatVersionError(string field, string text)
	{
		var hint = text.IndexOfAny(['<', '>', '^', '~', '*', ',', ' ']) >= 0
			? ";支持 ^ ~ >= < 等区间语法与精确版本"
			: "";
		return $"{field}: \"{text}\" 不是合法的 SemVer 版本串{hint}";
	}

	private static bool IsValidUid(string uid) =>
		System.Text.RegularExpressions.Regex.IsMatch(uid, UidPattern);

	private static bool IsValidAssemblyName(string? name) =>
		name is not null &&
		name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
		name.IndexOfAny(['/', '\\']) < 0 &&
		IsValidRelativePath(name);

	private static bool IsValidRelativePath(string path)
	{
		if (path.Length == 0 ||
			path.StartsWith("res:", StringComparison.OrdinalIgnoreCase) ||
			path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
			path.StartsWith('~'))
		{
			return false;
		}

		// Windows 盘符路径(C:\、C:/)在任何平台都算绝对:清单应跨平台可移植
		// (Path.IsPathRooted("C:/x") 在 Linux 返回 false,盘符路径必须显式拒绝)
		if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
		{
			return false;
		}

		if (Path.IsPathRooted(path))
		{
			return false;
		}

		var invalidChars = Path.GetInvalidPathChars();

		// 相对子目录路径允许(pcks/x.pck);空段、点段与含非法字符段不允许
		return path.Split('/', '\\')
			.All(segment =>
				segment.Length > 0 &&
				segment != "." &&
				segment != ".." &&
				!segment.Any(invalidChars.Contains));
	}
}
