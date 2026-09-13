// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Api;

namespace Sample;

/// <summary>
/// 样例模块:演示 OnLoad 发布跨模组服务(D4)、日志、OnPostLoad 消费时机。
/// </summary>
public class SampleModule : LmModule
{
	/// <summary>样例服务接口:其他模组可经 ServiceRegistry 消费。</summary>
	public interface IClockService
	{
		long NowUnixMilliseconds();
	}

	private sealed class ClockService : IClockService
	{
		public long NowUnixMilliseconds() => DateTimeOffset.Now.ToUnixTimeMilliseconds();
	}

	public override void OnLoad()
	{
		Logger.Info("样例模组 OnLoad:发布 IClockService");
		PublishService<IClockService>(new ClockService());
	}

	public override void OnPostLoad() => Logger.Info("样例模组 OnPostLoad");
}
