// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Api;

/// <summary>
/// 模组身份特性(D18):标在 <see cref="LmMod"/> 子类上,特性 uid 必须与 mod.json
/// <c>mods[].uid</c> 一致。加载器扫描模组声明的程序集,按特性 uid 与清单绑定;
/// uid 同时存在于清单(manifest-first 工具链:lint/pack-ts/Workshop 扫描/跨文件规划
/// 不加载 dll 即知 uid 集合)与代码(绑定锚点),双写由 lint 交叉校验。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class LmModAttribute : Attribute
{
	/// <summary>创建模组身份特性。</summary>
	/// <param name="uid">模组唯一标识,反向域名结构;与清单 <c>mods[].uid</c> 一致。</param>
	public LmModAttribute(string uid)
	{
		Uid = uid;
	}

	/// <summary>模组唯一标识(反向域名结构)。</summary>
	public string Uid { get; }
}
