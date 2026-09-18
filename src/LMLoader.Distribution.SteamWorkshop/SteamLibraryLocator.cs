// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Text.RegularExpressions;

namespace LMLoader.Distribution.SteamWorkshop;

/// <summary>
/// Steam 安装位置与库目录定位(D15):零 SDK 依赖。
/// Windows:注册表 <c>HKCU\Software\Valve\Steam\SteamPath</c> → 默认安装路径兜底;
/// Linux:~/.steam/steam、~/.local/share/Steam;macOS:~/Library/Application Support/Steam。
/// 多库根经 <c>steamapps/libraryfolders.vdf</c> 解析(Steam 根自身恒为第一个库)。
/// </summary>
public static partial class SteamLibraryLocator
{
	[GeneratedRegex(@"""path""\s+""(?<path>[^""]+)""")]
	private static partial Regex VdfPathEntry();

	/// <summary>按优先级排列的候选 Steam 根;存在且含 steamapps 的全部返回。</summary>
	public static IReadOnlyList<string> LocateSteamRoots()
	{
		var candidates = new List<string>();

		if (OperatingSystem.IsWindows())
		{
			try
			{
				using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
				if (key?.GetValue("SteamPath") is string registryPath)
				{
					candidates.Add(registryPath);
				}
			}
			catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
			{
				// 注册表不可读:走默认路径
			}

			var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			if (programFiles.Length > 0)
			{
				candidates.Add(Path.Combine(programFiles, "Steam"));
			}
		}

		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		candidates.Add(Path.Combine(userProfile, ".steam", "steam"));
		candidates.Add(Path.Combine(userProfile, ".local", "share", "Steam"));
		candidates.Add(Path.Combine(userProfile, "Library", "Application Support", "Steam"));

		return candidates
			.Where(p => Directory.Exists(Path.Combine(p, "steamapps")))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	/// <summary>
	/// 解析全部库根(Steam 根 + libraryfolders.vdf 中登记的各库);
	/// vdf 缺失或不可解析时仅返回 Steam 根(vdf 为逆向格式,失败不放大——D15)。
	/// </summary>
	public static IReadOnlyList<string> EnumerateLibraryRoots(IReadOnlyList<string> steamRoots)
	{
		var roots = new List<string>();
		foreach (var root in steamRoots)
		{
			if (roots.Contains(root, StringComparer.OrdinalIgnoreCase))
			{
				continue;
			}

			roots.Add(root);

			var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
			if (!File.Exists(vdf))
			{
				continue;
			}

			try
			{
				foreach (Match match in VdfPathEntry().Matches(File.ReadAllText(vdf)))
				{
					var path = match.Groups["path"].Value.Replace(@"\\", @"\");
					if (Directory.Exists(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
					{
						roots.Add(path);
					}
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// vdf 读取失败:保留已解析根
			}
		}

		return roots;
	}

	/// <summary>枚举指定游戏的 Workshop 内容目录(存在 mod.json 之外的任何内容均不管,调用方自行过滤)。</summary>
	public static IReadOnlyList<string> EnumerateWorkshopItemRoots(IReadOnlyList<string> libraryRoots, string gameAppId)
	{
		var items = new List<string>();
		foreach (var library in libraryRoots)
		{
			var contentRoot = Path.Combine(library, "steamapps", "workshop", "content", gameAppId);
			if (!Directory.Exists(contentRoot))
			{
				continue;
			}

			try
			{
				items.AddRange(Directory.EnumerateDirectories(contentRoot));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		return items;
	}
}
