// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Api.Config;

/// <summary>文件值写入结果:Applied = 已生效;TypeMismatch = 类型/值域不符,由调用方回退默认并警告(D11)。</summary>
internal enum ConfigSetOutcome
{
	Applied,

	TypeMismatch,
}

/// <summary>
/// 配置项元数据基类:节、键、描述、是否需重启、约束与默认值(为 GUI 配置面板预留,任务 4.2)。
/// 类型化读写见 <see cref="ConfigEntry{T}"/>,实例由 <see cref="ModConfig.Bind{T}"/> 创建,不开放外部构造。
/// </summary>
public abstract class ConfigEntryBase
{
	private protected ConfigEntryBase(
		string section,
		string key,
		string? description,
		bool requiresRestart,
		AcceptableValueBase? acceptableValues)
	{
		Section = section;
		Key = key;
		Description = description ?? "";
		RequiresRestart = requiresRestart;
		AcceptableValues = acceptableValues;
	}

	/// <summary>TOML 节名(空字符串 = 顶层裸键)。</summary>
	public string Section { get; }

	/// <summary>TOML 键名(节内唯一)。</summary>
	public string Key { get; }

	/// <summary>人可读描述,写回文件时作为注释/文档来源(当前重建全文不含注释,预留)。</summary>
	public string Description { get; }

	/// <summary>true = 改动需重启才可靠生效(v1 无 Unload,与范围决策一致);热重载仍会更新值并回调。</summary>
	public bool RequiresRestart { get; }

	/// <summary>取值约束;未声明为 null。</summary>
	public AcceptableValueBase? AcceptableValues { get; }

	/// <summary>绑定值类型。</summary>
	public abstract Type SettingType { get; }

	/// <summary>声明时的默认值(装箱)。</summary>
	public abstract object? BoxedDefaultValue { get; }

	/// <summary>当前值(装箱);供热重载/GUI 读取。</summary>
	public abstract object? BoxedValue { get; }

	/// <summary>文件值写入路径(首次合并与热重载共用);由 Core 调用。</summary>
	internal abstract ConfigSetOutcome TrySetFromRaw(object? raw);

	/// <summary>回退默认值(类型不符/列表外值时由 Core 调用)。</summary>
	internal abstract void ResetToDefault();
}
