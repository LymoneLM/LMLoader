// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Core.Manifest;
using LMLoader.Core.Versioning;

namespace LMLoader.Distribution.Thunderstore;

/// <summary>
/// mod.json ↔ Thunderstore manifest 转换(D16):
/// - 身份:包名 = distribution.thunderstore.name,缺省 UID 尾段;team 必须显式声明,缺失即抛错(显式优于猜测);
/// - 版本:version → version_number;含 prerelease/build 时报错(平台不支持);
/// - 依赖:遍历依赖清单,经依赖各自 mod.json 的别名生成 <c>Team-Name-精确版本</c>;版本取声明区间下界(闭端),
///   开下界(<c>&gt;v</c>)或无界且声明精确版本缺失时报错;依赖无别名 → 警告并跳过该条;
/// - 逆向:平台 manifest 不回写 mod.json——zip 内含 mod.json 的包直接以 UID 为身份(D16 逆向规则)。
/// </summary>
public static class ThunderstoreAdapter
{
	/// <summary>
	/// 生成本模组的 Thunderstore manifest。<paramref name="dependencyManifests"/> 提供全部依赖模组的
	/// 本格式清单(按依赖模块 uid → 所属模组清单);缺条目时该依赖以警告跳过。
	/// </summary>
	public static ThunderstoreManifest ToThunderstore(
		ModManifest manifest,
		string team,
		IReadOnlyDictionary<string, ModManifest> dependencyManifests,
		Action<string>? warn = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(team);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(dependencyManifests);

		if (!manifest.Version.IsPrerelease && manifest.Version.Build.Count == 0)
		{
			// 正常路径:直接采用核心版本号
		}
		else
		{
			throw new NotSupportedException(
				$"模组 \"{manifest.Uid}\" 版本 {manifest.Version} 含 prerelease/build 元数据,Thunderstore 仅支持 M.m.p");
		}

		var tsManifest = new ThunderstoreManifest
		{
			Name = ThunderstoreRules.ResolvePackageName(manifest),
			VersionNumber = $"{manifest.Version.Major}.{manifest.Version.Minor}.{manifest.Version.Patch}",
			WebsiteUrl = manifest.Website ?? "",
			Description = manifest.Description ?? "",
		};

		// 依赖映射:模块级依赖 → 所属模组别名(Team-Name-精确版本)
		var seenMods = new HashSet<string>(StringComparer.Ordinal);
		foreach (var module in manifest.Modules)
		{
			foreach (var dependency in module.Depends)
			{
				var depUid = dependency.Uid;
				// 依赖 uid 是模块 uid;所属模组 = 清单内声明的模块集合(此处按"模组 uid = 模块 uid 去掉 .main 尾段"不可靠,
				// 直接以 dependencyManifests 的 key(模组 uid)与"模块 uid 前缀"双查)
				var mod = ResolveDependencyMod(depUid, dependencyManifests);
				if (mod is null)
				{
					warn?.Invoke($"依赖模块 \"{depUid}\" 无对应清单,Thunderstore 依赖条目省略");
					continue;
				}

				if (!seenMods.Add(mod.Uid))
				{
					continue; // 同模组多模块依赖只生成一条
				}

				var tsTeam = mod.Distribution?.ThunderstoreTeam;
				if (string.IsNullOrEmpty(tsTeam))
				{
					warn?.Invoke($"依赖模组 \"{mod.Uid}\" 未声明 distribution.thunderstore.team,依赖条目省略");
					continue;
				}

				var version = ResolveExactVersion(mod, dependency, depUid);
				tsManifest.Dependencies.Add($"{tsTeam}-{ThunderstoreRules.ResolvePackageName(mod)}-{version}");
			}
		}

		return tsManifest;
	}

	/// <summary>依赖模组解析:优先按模组 uid 精确匹配,其次按"模块 uid 去掉最后一段"前缀匹配。</summary>
	private static ModManifest? ResolveDependencyMod(
		string moduleUid, IReadOnlyDictionary<string, ModManifest> dependencyManifests)
	{
		if (dependencyManifests.TryGetValue(moduleUid, out var exact))
		{
			return exact;
		}

		var lastDot = moduleUid.LastIndexOf('.');
		if (lastDot > 0 && dependencyManifests.TryGetValue(moduleUid[..lastDot], out var parent))
		{
			return parent;
		}

		return null;
	}

	/// <summary>版本解析:区间下界(闭端)/精确版本;开下界、无界或 prerelease 报错(D16)。</summary>
	private static string ResolveExactVersion(ModManifest mod, ModuleDependency dependency, string depUid)
	{
		if (dependency.Range is { } range)
		{
			var lower = range.TryGetLowerBound(out var bound, out var inclusive);
			if (!lower || !inclusive)
			{
				throw new NotSupportedException(
					$"依赖 \"{depUid}\" 的区间 \"{range.OriginalText}\" 无闭下界,无法映射为 Thunderstore 精确版本");
			}

			RequireCore(bound, depUid, range.OriginalText);
			return $"{bound.Major}.{bound.Minor}.{bound.Patch}";
		}

		if (dependency.Version is { } exact)
		{
			RequireCore(exact, depUid, exact.ToString());
			return $"{exact.Major}.{exact.Minor}.{exact.Patch}";
		}

		// 未声明版本:按依赖模组自身版本(生态惯例:发布时依赖随当前版本)
		RequireCore(mod.Version, depUid, "未声明版本,回退依赖模组当前版本");
		return $"{mod.Version.Major}.{mod.Version.Minor}.{mod.Version.Patch}";
	}

	private static void RequireCore(SemVer version, string depUid, string source)
	{
		if (version.IsPrerelease || version.Build.Count > 0)
		{
			throw new NotSupportedException(
				$"依赖 \"{depUid}\" 版本 {version}(来源:{source})含 prerelease/build,Thunderstore 不支持");
		}
	}
}
