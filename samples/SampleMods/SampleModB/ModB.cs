// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Api;
using LMLoader.Api.Config;

namespace SampleB;

/// <summary>
/// 样例模块 B:硬依赖 com.lmloader.sample.main(D8 验证依赖序),
/// OnPostLoad 经强类型直引通道消费 base 的 IClockService(D4),
/// 独立配置文件 com.lmloader.sample.extra.toml(D11 per-mod 配置)。
/// 消费结果记录到静态属性供冒烟断言。
/// </summary>
public class SampleModuleB : LmModule
{
	/// <summary>冒烟断言用:OnPostLoad 消费服务的结果记录。</summary>
	public static System.Collections.Generic.List<string> Records { get; } = new();

	private ConfigEntry<string>? _template;

	public override void OnPreLoad()
	{
		_template = Config.Bind("Greet", "Template", "clock:{ticks}", "消费 IClockService 的输出模板");
	}

	public override void OnPostLoad()
	{
		// base 已在 OnLoad 发布 IClockService;PostLoad 阶段全部模块注册完成
		if (TryGetService<Sample.SampleModule.IClockService>(out var clock))
		{
			var template = _template?.Value ?? "clock:{ticks}";
			Records.Add(template.Replace("{ticks}", clock.NowUnixMilliseconds().ToString()));
			Logger.Info("样例模组 B OnPostLoad:消费 IClockService 成功");
		}
		else
		{
			Logger.Warn("样例模组 B OnPostLoad:未找到 IClockService(依赖序或发布时机异常)");
		}
	}
}
