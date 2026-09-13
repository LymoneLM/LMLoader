// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Reflection;
using HarmonyLib;
using LMLoader.Api;
using LMLoader.Api.Config;

namespace Sample;

/// <summary>
/// 样例模块:演示 OnPreLoad 声明配置(D11)、OnLoad 发布跨模组服务(D4)、日志、
/// OnPostLoad 消费时机,以及配置值驱动的 patch(热重载改值即改行为)。
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

	private static ConfigEntry<int>? _multiplier;

	public override void OnPreLoad()
	{
		// 任务 4.2:D11 配置声明——默认值/范围/描述;缺失键将补默认写回 user://configs
		_multiplier = Config.Bind("Patch", "Multiplier", 100, "Add 前缀改写值(热重载演示)",
			acceptableValues: new AcceptableValueRange<int>(0, 1000));
		_multiplier.SettingChanged += e =>
			Logger.Info($"样例模组配置热重载:Multiplier = {e.Value}");
		Logger.Info($"样例模组 OnPreLoad:绑定配置 Multiplier(声明默认 100)");
	}

	public override void OnLoad()
	{
		Logger.Info("样例模组 OnLoad:发布 IClockService");
		PublishService<IClockService>(new ClockService());

		// 任务 3.4:patch 游戏静态方法与引擎逐帧调用的 _Process(native→managed)
		// HarmonyX 由模组自带(D14):per-mod 实例约定 id = 模组 uid
		var patcher = new Harmony(Uid);
		patcher.Patch(
			typeof(SampleGame.Main).GetMethod(nameof(SampleGame.Main.Add))!,
			prefix: new HarmonyMethod(typeof(SampleModule).GetMethod(nameof(AddPrefix), BindingFlags.NonPublic | BindingFlags.Static)!));
		patcher.Patch(
			typeof(SampleGame.Main).GetMethod(nameof(SampleGame.Main._Process), BindingFlags.Instance | BindingFlags.Public)!,
			postfix: new HarmonyMethod(typeof(SampleModule).GetMethod(nameof(ProcessPostfix), BindingFlags.NonPublic | BindingFlags.Static)!));
		Logger.Info("样例模组 OnLoad:已 patch Main.Add 与 Main._Process");
	}

	private static bool AddPrefix(ref int __result)
	{
		__result = _multiplier?.Value ?? 100;
		return false; // 跳过原方法
	}

	private static void ProcessPostfix() => SampleGame.Main.ProcessPatchHits++;

	public override void OnPostLoad() => Logger.Info("样例模组 OnPostLoad");
}
