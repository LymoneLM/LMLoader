// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Core.Manifest;

namespace LMLoader.Distribution.SteamWorkshop;

/// <summary>
/// Workshop 订阅模组扫描:候选内容目录 → 存在 mod.json 者为发现根;
/// ACF(<c>appworkshop_&lt;appid&gt;.acf</c>)做机会性孤儿过滤——解析成功时剔除未安装条目,
/// 失败/缺失一律放行(逆向格式不受保护,永不依赖)。
/// </summary>
public static class WorkshopModsScanner
{
	/// <summary>取可被 loader 发现的 Workshop 模组目录(含 *mod.json 的条目根)。</summary>
	public static IReadOnlyList<string> GetScanRoots(IReadOnlyList<string> libraryRoots, string gameAppId, Action<string>? warn = null)
	{
		var candidates = SteamLibraryLocator.EnumerateWorkshopItemRoots(libraryRoots, gameAppId);
		if (candidates.Count == 0)
		{
			return Array.Empty<string>();
		}

		var installed = TryReadInstalledItemIds(libraryRoots, gameAppId, warn);
		if (installed is not null)
		{
			candidates = candidates
				.Where(dir => installed.Contains(Path.GetFileName(dir)))
				.ToList();
		}

		return candidates
			.Where(dir => Directory.EnumerateFiles(dir, "*mod.json", SearchOption.TopDirectoryOnly).Any())
			.ToList();
	}

	/// <summary>读 ACF 已安装条目 id 集;acf 缺失/解析失败返回 null(不过滤)。</summary>
	private static HashSet<string>? TryReadInstalledItemIds(IReadOnlyList<string> libraryRoots, string gameAppId, Action<string>? warn)
	{
		foreach (var library in libraryRoots)
		{
			var acfPath = Path.Combine(library, "steamapps", "workshop", $"appworkshop_{gameAppId}.acf");
			if (!File.Exists(acfPath))
			{
				continue;
			}

			try
			{
				var ids = AcfParser.ReadWorkshopItemsInstalled(File.ReadAllText(acfPath));
				if (ids is not null)
				{
					return ids;
				}

				warn?.Invoke($"ACF 缺少 WorkshopItemsInstalled 节,跳过孤儿过滤: {acfPath}");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				warn?.Invoke($"ACF 读取失败,跳过孤儿过滤: {ex.Message}");
			}
		}

		return null;
	}
}

/// <summary>极简 KeyValues(ACF)读取:仅提取顶层 <c>WorkshopItemsInstalled</c> 的直接子键(pfid)。</summary>
public static class AcfParser
{
	public static HashSet<string>? ReadWorkshopItemsInstalled(string acfText)
	{
		const string sectionName = "\"WorkshopItemsInstalled\"";
		var sectionStart = acfText.IndexOf(sectionName, StringComparison.Ordinal);
		if (sectionStart < 0)
		{
			return null;
		}

		var braceStart = acfText.IndexOf('{', sectionStart + sectionName.Length);
		if (braceStart < 0)
		{
			return null;
		}

		// 找到匹配的闭括号,收集块内"深度 1 的键"(引号段后直接跟 '{')
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var depth = 0;
		var i = braceStart;
		var lastKey = default(string);

		for (; i < acfText.Length; i++)
		{
			var c = acfText[i];
			if (c == '{')
			{
				depth++;
				if (depth == 2 && lastKey is not null)
				{
					ids.Add(lastKey);
					lastKey = null;
				}
			}
			else if (c == '}')
			{
				depth--;
				if (depth == 0)
				{
					return ids;
				}
			}
			else if (c == '"' && depth >= 1)
			{
				var close = acfText.IndexOf('"', i + 1);
				if (close < 0)
				{
					return null; // 引号不配对:格式损坏
				}

				var token = acfText[(i + 1)..close];
				i = close;
				if (depth == 1)
				{
					lastKey = token; // 深度 1 的引号段 = 子条目键(pfid)或子节名
				}
			}
		}

		return null; // 未闭合:格式损坏
	}
}
