// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Core.Manifest;
using LMLoader.Core.Versioning;

namespace LMLoader.Distribution.Thunderstore;

/// <summary>
/// mod.json ↔ Thunderstore manifest 转换(D18/D16 修订):
/// - 身份:包名 = distribution.thunderstore.name,缺省 = 首个模组 uid 尾段;team 必须显式声明,缺失即抛错;
/// - 版本:首个模组的版本 → version_number;含 prerelease/build 时报错(平台不支持);
/// - 依赖:遍历全部模组的 depends,按依赖模组 uid 精确匹配(依赖即模组 uid,无前缀推导),
///   经依赖自身别名生成 <c>Team-Name-精确版本</c>;版本取声明区间下界(闭端),
///   开下界(<c>&gt;v</c>)或无界且声明精确版本缺失时报错;依赖无清单/无别名 → 警告并跳过该条;
/// - 逆向:平台 manifest 不回写 mod.json——zip 内含 mod.json 的包直接以模组 uid 为身份。
/// </summary>
public static class ThunderstoreAdapter
{
	/// <summary>
	/// 生成本清单的 Thunderstore manifest。<paramref name="dependencyManifests"/> 提供全部依赖模组的
	/// 清单(键 = 模组 uid);缺条目时该依赖以警告跳过。
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

		var primary = manifest.Mods[0];
		if (!primary.Version.IsPrerelease && primary.Version.Build.Count == 0)
		{
			// 正常路径:直接采用核心版本号
		}
		else
		{
			throw new NotSupportedException(
				$"模组 \"{primary.Uid}\" 版本 {primary.Version} 含 prerelease/build 元数据,Thunderstore 仅支持 M.m.p");
		}

		var tsManifest = new ThunderstoreManifest
		{
			Name = ThunderstoreRules.ResolvePackageName(manifest),
			VersionNumber = $"{primary.Version.Major}.{primary.Version.Minor}.{primary.Version.Patch}",
			WebsiteUrl = manifest.Website ?? "",
			Description = manifest.Description ?? "",
		};

		// 依赖映射:模组级依赖(依赖 uid 即模组 uid)→ 目标清单别名(Team-Name-精确版本)
		var seenTargets = new HashSet<string>(StringComparer.Ordinal);
		foreach (var mod in manifest.Mods)
		{
			foreach (var dependency in mod.Depends)
			{
				var depUid = dependency.Uid;
				if (!dependencyManifests.TryGetValue(depUid, out var target))
				{
					warn?.Invoke($"依赖模组 \"{depUid}\" 无对应清单,Thunderstore 依赖条目省略");
					continue;
				}

				if (!seenTargets.Add(depUid))
				{
					continue; // 同目标多模组依赖只生成一条
				}

				var tsTeam = target.Distribution?.ThunderstoreTeam;
				if (string.IsNullOrEmpty(tsTeam))
				{
					warn?.Invoke($"依赖模组 \"{depUid}\" 未声明 distribution.thunderstore.team,依赖条目省略");
					continue;
				}

				var version = ResolveExactVersion(target, dependency, depUid);
				tsManifest.Dependencies.Add($"{tsTeam}-{ThunderstoreRules.ResolvePackageName(target)}-{version}");
			}
		}

		return tsManifest;
	}

	/// <summary>版本解析:区间下界(闭端)/精确版本;开下界、无界或 prerelease 报错。</summary>
	private static string ResolveExactVersion(ModManifest target, ModDependency dependency, string depUid)
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

		// 未声明版本:取目标模组自身首个模组的版本(生态惯例:发布时依赖随当前版本)
		var fallback = target.Mods[0].Version;
		RequireCore(fallback, depUid, "未声明版本,回退依赖模组当前版本");
		return $"{fallback.Major}.{fallback.Minor}.{fallback.Patch}";
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
