// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Core.Versioning;

namespace LMLoader.Core.Manifest;

/// <summary>
/// mod.json 清单模型(schema v1,D18 重定义):一份清单 = 文件级声明块 + 并列模组列表。
/// 文件级字段是可继承默认值;<see cref="ModManifestReader"/> 构造时已展开继承
/// (每个 <see cref="ModEntry"/> 携带解析后的最终值);字段语义见 docs/mod-json-schema.md。
/// </summary>
public sealed class ModManifest
{
	public required int SchemaVersion { get; init; }

	/// <summary>目标游戏标识;null = 通用模组(装载于任何宿主;宿主设置 GameId 时仅显式声明者比对)。</summary>
	public string? GameId { get; init; }

	/// <summary>
	/// 依赖的 LMLoader API 版本区间;null = 缺省(当前 loader 主版本兼容区,由宿主侧按 API 版本取值)。
	/// </summary>
	public VersionRange? LoaderVersion { get; init; }

	public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();

	public string? Description { get; init; }

	public string? Icon { get; init; }

	public string? Website { get; init; }

	/// <summary>展示用标签;loader 本体不消费,供管理器/分发平台过滤。</summary>
	public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

	/// <summary>分发平台别名;loader 本体不消费,供打包器生成平台 manifest。缺省 null。</summary>
	public ModDistribution? Distribution { get; init; }

	/// <summary>本清单声明的模组(≥1)。</summary>
	public required IReadOnlyList<ModEntry> Mods { get; init; }

	/// <summary>mod.json 物理路径(诊断用)。</summary>
	public required string SourcePath { get; init; }

	/// <summary>模组目录(= SourcePath 所在目录)。</summary>
	public string Directory { get; init; } = "";
}

/// <summary>模组定义:加载/依赖/排序/失败传播的最小单位。</summary>
public sealed class ModEntry
{
	/// <summary>模组 UID,全局唯一;与代码侧 <c>[LmMod(uid)]</c> 特性一致。</summary>
	public required string Uid { get; init; }

	/// <summary>模组版本(继承解析后;依赖区间判定锚)。</summary>
	public required SemVer Version { get; init; }

	/// <summary>显示名(继承解析后;缺省 = uid 尾段)。</summary>
	public required string Name { get; init; }

	/// <summary>作者列表(继承解析后)。</summary>
	public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();

	/// <summary>简介(继承解析后)。</summary>
	public string? Description { get; init; }

	/// <summary>图标路径(继承解析后)。</summary>
	public string? Icon { get; init; }

	/// <summary>主页/仓库链接(继承解析后)。</summary>
	public string? Website { get; init; }

	/// <summary>展示标签(继承解析后)。</summary>
	public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

	/// <summary>
	/// 该模组要加载的程序集(相对模组目录);空 = 纯资源/依赖锚模组(无代码、无实例、无钩子)。
	/// 仅当模组被加载时才加载;同一路径跨模组重复声明只加载一次。
	/// </summary>
	public IReadOnlyList<string> Assemblies { get; init; } = Array.Empty<string>();

	/// <summary>pck 资源路径(相对模组目录;早于一切代码加载)。</summary>
	public IReadOnlyList<string> PckResources { get; init; } = Array.Empty<string>();

	/// <summary>依赖声明(唯一来源)。</summary>
	public IReadOnlyList<ModDependency> Depends { get; init; } = Array.Empty<ModDependency>();
}

/// <summary>模组依赖声明。<c>Version</c>/<c>Range</c> 二选一:区间为 null 时保留精确语义(裸精确声明兼容)。</summary>
public sealed record ModDependency(string Uid, SemVer? Version, bool Soft)
{
	/// <summary>版本区间声明;解析失败或未声明为 null(此时回退 <see cref="Version"/>).</summary>
	public VersionRange? Range { get; init; }
}

/// <summary>
/// 分发平台别名声明。uid 为运行时唯一身份;此处仅为打包器向分发平台映射身份提供显式来源。
/// Thunderstore:name 缺省 = 首个模组 uid 尾段(`.`→`_`);team 无缺省(打包时缺失报错)。
/// </summary>
public sealed record ModDistribution(string? ThunderstoreTeam, string? ThunderstoreName = null);
