// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Core.Lifecycle;

/// <summary>加载执行阶段。Unload 不实装(范围决策):运行期开关以重启生效为唯一可靠路径。</summary>
public enum LifecycleStage
{
	/// <summary>模组声明的程序集加载进共享上下文(含特性扫描)。</summary>
	AssemblyLoad,

	/// <summary>按特性 uid 绑定并反射实例化模组类型,注入上下文。</summary>
	Instantiation,

	/// <summary>模组钩子 OnPreLoad。</summary>
	PreLoad,

	/// <summary>模组钩子 OnLoad。</summary>
	Load,

	/// <summary>模组钩子 OnPostLoad。</summary>
	PostLoad,
}

/// <summary>单个模组的运行结果(单点失败不放大,一切可诊断)。</summary>
public sealed class ModRunResult
{
	/// <summary>模组 uid(全局唯一)。</summary>
	public required string Uid { get; init; }

	/// <summary>显示名(清单继承解析后)。</summary>
	public required string Name { get; init; }

	/// <summary>所属清单文件路径(诊断用)。</summary>
	public required string SourcePath { get; init; }

	/// <summary>PreLoad 与 Load 均成功,或为纯资源模组(无论 PostLoad 结果)。</summary>
	public bool Succeeded { get; internal set; }

	/// <summary>因前序失败被级联跳过(未实例化/未执行对应阶段)。</summary>
	public bool Skipped { get; internal set; }

	/// <summary>纯资源/依赖锚模组(无 assemblies,无实例、无钩子,直接视为成功)。</summary>
	public bool ResourceOnly { get; internal set; }

	/// <summary>PostLoad 是否已执行(仅对 PreLoad/Load 成功且非纯资源的模组执行)。</summary>
	public bool PostLoadExecuted { get; internal set; }

	/// <summary>失败阶段(非 null = 失败)。</summary>
	public LifecycleStage? FailedStage { get; internal set; }

	/// <summary>首个异常(hook 边界捕获,不外抛)。</summary>
	public Exception? Exception { get; internal set; }

	/// <summary>补充说明(级联原因、strict 中止标记等)。</summary>
	public string? Note { get; internal set; }

	/// <summary>模组实例(PreLoad/Load 成功后非 null;供服务注册等后续使用)。</summary>
	public Api.LmMod? Instance { get; internal set; }
}

/// <summary>一轮生命周期执行的汇总。</summary>
public sealed class LifecycleReport
{
	/// <summary>按执行顺序排列的结果(级联跳过的模组同样在列)。</summary>
	public required IReadOnlyList<ModRunResult> Results { get; init; }

	/// <summary>strict 模式因失败中止。</summary>
	public bool StrictAborted { get; init; }

	/// <summary>总耗时。</summary>
	public TimeSpan Duration { get; init; }

	/// <summary>
	/// 特性扫描警告(D18):继承 LmMod 未标注特性、标注未继承/不可实例化、uid 未被清单声明(孤立类)等。
	/// 绑定失败(error 级)不走这里——它表现为对应模组的失败结果。
	/// </summary>
	public IReadOnlyList<string> BindingWarnings { get; init; } = Array.Empty<string>();

	public int SucceededCount => Results.Count(r => r.Succeeded);

	public int FailedCount => Results.Count(r => r.FailedStage is not null);

	public int SkippedCount => Results.Count(r => r.Skipped);
}
