// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Text.RegularExpressions;
using LMLoader.Core.Manifest;

namespace LMLoader.Distribution.Thunderstore;

/// <summary>
/// Thunderstore 平台约束(依据平台源码查证):
/// 包名 <c>^[a-zA-Z0-9_]+$</c> ≤128;version_number 仅 <c>M.m.p</c>(无 prerelease/build,禁前导零);
/// 依赖引用 <c>Team-Name-1.2.3</c>(name 段不含 '-',team 可含);description ≤256。
/// </summary>
public static partial class ThunderstoreRules
{
	[GeneratedRegex("^[a-zA-Z0-9_]+$")]
	public static partial Regex PackageName();

	[GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$")]
	public static partial Regex VersionNumber();

	[GeneratedRegex("^(?<team>[a-zA-Z0-9_.-]+)-(?<name>[a-zA-Z0-9_]+)-(?<version>[0-9]+\\.[0-9]+\\.[0-9]+)$")]
	public static partial Regex DependencyReference();

	public const int MaxPackageNameLength = 128;

	public const int MaxDescriptionLength = 256;

	/// <summary>UID 尾段 → Thunderstore 包名缺省值('.' → '_';尾段本身即不含点)。</summary>
	public static string DefaultPackageName(string uid) =>
		uid.Split('.')[^1].Replace('-', '_');

	/// <summary>从本格式清单取别名包名;未声明时回退缺省规则。</summary>
	public static string ResolvePackageName(ModManifest manifest)
	{
		var declared = manifest.Distribution?.ThunderstoreName;
		return string.IsNullOrEmpty(declared) ? DefaultPackageName(manifest.Uid) : declared!;
	}
}
