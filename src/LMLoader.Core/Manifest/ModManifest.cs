// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Core.Versioning;

namespace LMLoader.Core.Manifest;

/// <summary>
/// mod.json 清单模型(schema v0.1,决策 D9)。
/// 由 <see cref="ModManifestReader"/> 构造;字段语义见 docs/mod-json-schema.md。
/// </summary>
public sealed class ModManifest
{
	public required int SchemaVersion { get; init; }

	/// <summary>模组唯一标识,反向域名结构。</summary>
	public required string Uid { get; init; }

	public required string Name { get; init; }

	public required SemVer Version { get; init; }

	public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();

	public string? Description { get; init; }

	public string? Icon { get; init; }

	public string? Website { get; init; }

	/// <summary>展示用标签(v1.0 新增,D9 迭代);loader 本体不消费,供管理器/分发平台过滤。</summary>
	public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

	/// <summary>目标游戏标识,与宿主游戏约定比对,不匹配拒载(D9)。</summary>
	public required string GameId { get; init; }

	/// <summary>依赖的 LMLoader API 版本区间(阶段 5 起支持 ^ ~ 比较符等;裸精确版本天然兼容)。</summary>
	public required VersionRange LoaderVersion { get; init; }

	/// <summary>模组 DLL 文件名(相对模组目录)。</summary>
	public required string EntryAssembly { get; init; }

	/// <summary>模块列表(加载最小单位)。</summary>
	public required IReadOnlyList<ModuleEntry> Modules { get; init; }

	/// <summary>pck 资源路径(相对模组目录;早于逻辑加载,D10)。</summary>
	public IReadOnlyList<string> PckResources { get; init; } = Array.Empty<string>();

	/// <summary>mod.json 物理路径(诊断用)。</summary>
	public required string SourcePath { get; init; }

	/// <summary>模组目录(= SourcePath 所在目录)。</summary>
	public string Directory { get; init; } = "";
}

/// <summary>模块定义:模组的最小加载单位。</summary>
public sealed class ModuleEntry
{
	/// <summary>模块 UID,全局唯一。</summary>
	public required string Uid { get; init; }

	/// <summary>模块类完整命名空间全名(须继承 LmModule;无命名空间的类无法被按名查找,P0-1)。</summary>
	public required string Type { get; init; }

	/// <summary>依赖声明(唯一来源,决策 D2)。</summary>
	public IReadOnlyList<ModuleDependency> Depends { get; init; } = Array.Empty<ModuleDependency>();
}

/// <summary>模块依赖声明。<c>Version</c>/<c>Range</c> 二选一:区间为 null 时保留精确语义(旧清单兼容)。</summary>
public sealed record ModuleDependency(string Uid, SemVer? Version, bool Soft)
{
	/// <summary>版本区间声明(阶段 5);解析失败或未声明为 null(此时回退 <see cref="Version"/>)。</summary>
	public VersionRange? Range { get; init; }
}
