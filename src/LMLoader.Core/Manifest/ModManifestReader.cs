// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Text.Json;
using LMLoader.Core.Versioning;

namespace LMLoader.Core.Manifest;

/// <summary>
/// mod.json 读取器(schema v1.0)。
/// 容错语义:未知字段忽略+警告;schemaVersion 主版本不识别拒载;错误累积返回(不抛出)。
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

			var properties = CollectUniqueProperties(root, "", warnings);

			// schemaVersion:主版本不识别 → 拒载
			if (!TryGetRequiredInt(properties, "schemaVersion", errors, out var schemaVersion))
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

			// 必填标量字段(错误累积,不短路)
			var okUid = TryGetRequiredString(properties, "uid", errors, out var uid);
			var okName = TryGetRequiredString(properties, "name", errors, out var name);
			var okVersion = TryGetRequiredString(properties, "version", errors, out var versionText);
			var okGameId = TryGetRequiredString(properties, "gameId", errors, out var gameId);
			var okLoaderVersion = TryGetRequiredString(properties, "loaderVersion", errors, out var loaderVersionText);

			// 可选标量字段
			var authors = ReadStringArray(properties, "authors", warnings);
			var description = GetOptionalString(properties, "description");
			var icon = GetOptionalString(properties, "icon");
			var website = GetOptionalString(properties, "website");
			var tags = ReadStringArray(properties, "tags", warnings); // 展示字段
			var distribution = ReadDistribution(properties, errors, warnings);

			if (okUid && !IsValidUid(uid))
			{
				errors.Add($"uid: \"{uid}\" 不是合法的反向域名结构(如 com.author.modname)");
			}

			SemVer version = default;
			if (okVersion && !SemVer.TryParse(versionText, out version))
			{
				errors.Add(FormatVersionError("version", versionText));
				okVersion = false;
			}

			VersionRange loaderVersion = default!;
			if (okLoaderVersion && !VersionRange.TryParse(loaderVersionText, out loaderVersion!))
			{
				errors.Add(FormatVersionError("loaderVersion", loaderVersionText));
				okLoaderVersion = false;
			}

			if (!(okUid && okName && okVersion && okGameId && okLoaderVersion))
			{
				return ManifestReadResult.Fail(errors, warnings);
			}

			// entry
			ModuleEntry[] modules = Array.Empty<ModuleEntry>();
			string entryAssembly = "";
			if (properties.TryGetValue("entry", out var entryElement))
			{
				if (entryElement.ValueKind != JsonValueKind.Object)
				{
					errors.Add("entry: 必须是对象");
				}
				else
				{
					var entry = CollectUniqueProperties(entryElement, "entry", warnings);
					if (!TryGetRequiredString(entry, "assembly", errors, out entryAssembly))
					{
						entryAssembly = "";
					}
					else if (!IsValidAssemblyName(entryAssembly))
					{
						errors.Add($"entry.assembly: \"{entryAssembly}\" 必须是相对模组目录的 .dll 文件名,不得包含路径分隔符");
						entryAssembly = "";
					}

					if (entry.TryGetValue("modules", out var modulesElement))
					{
						modules = ReadModules(modulesElement, errors, warnings);
					}
					else
					{
						errors.Add("entry.modules: 缺少必填字段");
					}
				}
			}
			else
			{
				errors.Add("entry: 缺少必填字段");
			}

			// resources(可选)
			var pckResources = ReadResources(properties, warnings);

			if (errors.Count > 0 || modules.Length == 0)
			{
				if (errors.Count == 0)
				{
					errors.Add("entry.modules: 至少需要一个模块");
				}

				return ManifestReadResult.Fail(errors, warnings);
			}

			var manifest = new ModManifest
			{
				SchemaVersion = schemaVersion,
				Uid = uid,
				Name = name,
				Version = version,
				Authors = authors,
				Description = description,
				Icon = icon,
				Website = website,
				Tags = tags,
				Distribution = distribution,
				GameId = gameId,
				LoaderVersion = loaderVersion,
				EntryAssembly = entryAssembly,
				Modules = modules,
				PckResources = pckResources,
				SourcePath = sourcePath,
				Directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? "",
			};

			return ManifestReadResult.Ok(manifest, warnings);
		}
	}

	private static ModuleEntry[] ReadModules(JsonElement modulesElement, List<string> errors, List<string> warnings)
	{
		var result = new List<ModuleEntry>();
		var seenUids = new HashSet<string>(StringComparer.Ordinal);

		if (modulesElement.ValueKind != JsonValueKind.Array)
		{
			errors.Add("entry.modules: 必须是数组");
			return Array.Empty<ModuleEntry>();
		}

		var index = 0;
		foreach (var moduleElement in modulesElement.EnumerateArray())
		{
			var path = $"entry.modules[{index}]";
			index++;

			if (moduleElement.ValueKind != JsonValueKind.Object)
			{
				errors.Add($"{path}: 必须是对象");
				continue;
			}

			var properties = CollectUniqueProperties(moduleElement, path, warnings);

			if (!TryGetRequiredString(properties, "uid", errors, out var uid) ||
				!TryGetRequiredString(properties, "type", errors, out var type))
			{
				continue;
			}

			if (!IsValidUid(uid))
			{
				errors.Add($"{path}.uid: \"{uid}\" 不是合法的反向域名结构");
			}

			if (!type.Contains('.'))
			{
				// 按字符串查类型须用完整命名空间全名,无命名空间类会被漏掉
				errors.Add($"{path}.type: \"{type}\" 必须为完整命名空间全名(无命名空间的类无法被按名查找)");
			}

			if (!seenUids.Add(uid))
			{
				errors.Add($"{path}.uid: 模块 uid \"{uid}\" 在清单内重复定义");
			}

			var depends = ReadDependencies(properties, path, errors, warnings);

			result.Add(new ModuleEntry { Uid = uid, Type = type, Depends = depends });
		}

		return result.ToArray();
	}

	private static ModuleDependency[] ReadDependencies(
		Dictionary<string, JsonElement> moduleProperties,
		string modulePath,
		List<string> errors,
		List<string> warnings)
	{
		if (!moduleProperties.TryGetValue("depends", out var dependsElement))
		{
			return Array.Empty<ModuleDependency>();
		}

		if (dependsElement.ValueKind != JsonValueKind.Array)
		{
			errors.Add($"{modulePath}.depends: 必须是数组");
			return Array.Empty<ModuleDependency>();
		}

		var result = new List<ModuleDependency>();
		var index = 0;
		foreach (var dependencyElement in dependsElement.EnumerateArray())
		{
			var path = $"{modulePath}.depends[{index}]";
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

			result.Add(new ModuleDependency(uid, version, soft) { Range = range });
		}

		return result.ToArray();
	}

	private static string[] ReadResources(Dictionary<string, JsonElement> properties, List<string> warnings)
	{
		if (!properties.TryGetValue("resources", out var resourcesElement))
		{
			return Array.Empty<string>();
		}

		if (resourcesElement.ValueKind != JsonValueKind.Object)
		{
			warnings.Add("resources: 应为对象,已忽略");
			return Array.Empty<string>();
		}

		var collected = CollectUniqueProperties(resourcesElement, "resources", warnings);

		if (!collected.TryGetValue("pck", out var pckElement))
		{
			return Array.Empty<string>();
		}

		if (pckElement.ValueKind != JsonValueKind.Array)
		{
			warnings.Add("resources.pck: 应为数组,已忽略");
			return Array.Empty<string>();
		}

		var result = new List<string>();
		foreach (var pathElement in pckElement.EnumerateArray())
		{
			if (pathElement.ValueKind != JsonValueKind.String)
			{
				warnings.Add("resources.pck: 含非字符串项,已忽略该项");
				continue;
			}

			var pckPath = pathElement.GetString() ?? "";
			if (!IsValidRelativePath(pckPath))
			{
				warnings.Add($"resources.pck: \"{pckPath}\" 不是合法的相对路径(禁止绝对路径与 .. 上跳),已忽略该项");
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
		"schemaVersion", "uid", "name", "version", "authors", "description",
		"icon", "website", "tags", "gameId", "loaderVersion", "entry", "resources", "distribution",
	];

	private static string[] KnownNestedFields(string path) =>
		path.Contains(".depends[", StringComparison.Ordinal)
			? ["uid", "version", "soft"]
			: path.StartsWith("entry.modules[", StringComparison.Ordinal)
				? ["uid", "type", "depends"]
				: path switch
				{
					"entry" => ["assembly", "modules"],
					"resources" => ["pck"],
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
		List<string> warnings)
	{
		if (!properties.TryGetValue(field, out var element))
		{
			return Array.Empty<string>();
		}

		if (element.ValueKind != JsonValueKind.Array)
		{
			warnings.Add($"{field}: 应为数组,已忽略");
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
				warnings.Add($"{field}: 含空或非字符串项,已忽略该项");
			}
		}

		return result.ToArray();
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

	private static string FormatVersionError(string field, string text)
	{
		var hint = text.IndexOfAny(['<', '>', '^', '~', '*', ',', ' ']) >= 0
			? ";支持 ^ ~ >= < 等区间语法与精确版本"
			: "";
		return $"{field}: \"{text}\" 不是合法的 SemVer 版本串{hint}";
	}

	private static bool IsValidUid(string uid) =>
		System.Text.RegularExpressions.Regex.IsMatch(uid, UidPattern);

	private static bool IsValidAssemblyName(string name) =>
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
