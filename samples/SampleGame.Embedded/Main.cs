// SPDX-License-Identifier: LGPL-3.0-or-later

using Godot;
using LMLoader.Api;
using LMLoader.Embedded;

namespace SampleGame;

/// <summary>
/// 样例游戏主场景:内嵌版一行接入的活文档。
/// 引导节点延迟入树,加载为异步完成:订阅 BootCompleted 后断言结果。
/// headless 冒烟验证:成功输出 LMLOADER-SMOKE-PASS 并以 0 退出;失败输出 LMLOADER-SMOKE-FAIL 以 1 退出。
/// </summary>
public partial class Main : Node
{
	/// <summary>样例目标:被模组 patch 的静态方法(原语义 1+2=3;模组 prefix 改写为 100)</summary>
	public static int Add(int a, int b) => a + b;

	/// <summary>样例目标:被模组 postfix 的 _Process 命中计数(引擎 native→managed 路径,P0-1 核心风险点)</summary>
	public static int ProcessPatchHits;

	public override void _Process(double delta)
	{
	}

	public override void _Ready()
	{
		// 一行接入(D5/D7 能力经 loader 节点访问)
		var loader = LMLoaderEmbedded.Initialize(GetTree(), l =>
		{
			l.GameId = "com.lmloader.samplegame";
			l.ApiVersion = new Version(1, 0, 0); // 样例自定基线;缺省取 LMLoader.Api 程序集版本
			// P0-1:游戏程序集在宿主自身 ALC,须按名显式供给模组(嵌入模式由入口程序集自供)
			l.GameAssemblyResolver = n => n.Name == "SampleGame.Embedded" ? typeof(Main).Assembly : null;
		});

		loader.BootCompleted += () =>
		{
			var result = loader.LastLoadResult;
			if (result is null || result.Lifecycle is null || result.Lifecycle.SucceededCount != 1)
			{
				GD.PrintErr("LMLOADER-SMOKE-FAIL");
				GD.PrintErr(result?.SummaryText ?? "(无加载结果)");
				GetTree().Quit(1);
				return;
			}

			if (loader.GetModMountPoint("com.lmloader.sample") is null)
			{
				GD.PrintErr("LMLOADER-SMOKE-FAIL");
				GD.PrintErr("模组挂载点缺失(D5)");
				GetTree().Quit(1);
				return;
			}

			// 任务 2.4:模组 pck 挂载验证(D10)——pck 早于逻辑挂载,资源在此应可访问;
			// mod_icon.svg 经 Godot 导入(.import 重映射),验证导入产物在宿主挂载后的行为
			var rawText = Godot.FileAccess.Open("res://mods/com.lmloader.sample/hello.txt", Godot.FileAccess.ModeFlags.Read);
			if (rawText is null || rawText.GetAsText().Trim() != "hello-from-pck")
			{
				GD.PrintErr("LMLOADER-SMOKE-FAIL");
				GD.PrintErr("pck 内原始文件不可访问");
				GetTree().Quit(1);
				return;
			}

			if (!ResourceLoader.Exists("res://mods/com.lmloader.sample/mod_icon.svg"))
			{
				GD.PrintErr("LMLOADER-SMOKE-FAIL");
				GD.PrintErr("pck 内导入资源(.import 重映射)不可访问");
				GetTree().Quit(1);
				return;
			}

			var texture = ResourceLoader.Load<Texture2D>("res://mods/com.lmloader.sample/mod_icon.svg");
			if (texture is null)
			{
				GD.PrintErr("LMLOADER-SMOKE-FAIL");
				GD.PrintErr("导入资源加载失败");
				GetTree().Quit(1);
				return;
			}

			// 任务 3.4:模组 patch 验证——静态方法 prefix 改写(原语义应为 3)
			if (Add(1, 2) != 100)
			{
				SmokeFail("静态方法 patch 未生效");
				return;
			}

			// ---- M4:配置系统(D11) ----
			if (loader.Configs is null)
			{
				SmokeFail("配置系统未启用(user://configs)");
				return;
			}

			if (loader.LogBuffer is null || loader.LogBuffer.Snapshot().Length == 0)
			{
				SmokeFail("内存环形日志缓冲为空(D6)");
				return;
			}

			// 首次运行:缺失键应已按默认值写回 user://configs/com.lmloader.sample.toml
			if (!Godot.FileAccess.FileExists("user://configs/com.lmloader.sample.toml"))
			{
				SmokeFail("配置默认值未写回(D11)");
				return;
			}

			// 任务 4.6:日志窗口——渲染环形缓冲 + 分级过滤(不入树,直接断言)
			var window = new LMLoader.UI.LmLogWindow(loader.LogBuffer);
			window.Refresh();
			if (!window.LogText.Contains("[LMLoader]"))
			{
				SmokeFail("日志窗口未渲染出 loader 日志");
				return;
			}

			window.SetMinimumLevel(LmLogLevel.Error);
			window.Refresh();
			if (window.LogText.Contains("[INFO]"))
			{
				SmokeFail("日志窗口分级过滤未生效");
				return;
			}
			window.QueueFree();

			// 任务 4.4:热重载引擎链验证——先把配置归一到默认(幂等,消除上次运行遗留),
			// 再改值 250,防抖后 Add 应跟踪变化
			if (!WriteSampleConfig(100))
			{
				SmokeFail("配置文件写入失败");
				return;
			}

			GetTree().CreateTimer(1.5).Timeout += () =>
			{
				if (Add(1, 2) != 100)
				{
					SmokeFail($"配置热重载(归一到默认)未生效:Add={Add(1, 2)}");
					return;
				}

				if (!WriteSampleConfig(250))
				{
					SmokeFail("配置文件写入失败");
					return;
				}

				GetTree().CreateTimer(1.5).Timeout += () =>
				{
					if (Add(1, 2) != 250)
					{
						SmokeFail($"配置热重载未生效:Add={Add(1, 2)}(期望 250)");
						return;
					}

					// 引擎 native→managed 路径:_Process 由引擎每帧调用,postfix 计数应持续增长
					GetTree().CreateTimer(0.5).Timeout += () =>
					{
						if (ProcessPatchHits == 0)
						{
							SmokeFail("_Process postfix 未命中(native→managed)");
							return;
						}

						GD.Print($"LMLOADER-SMOKE-PASS (patch hits: {ProcessPatchHits})");
						GD.Print(result.SummaryText);
						GetTree().Quit(0);
					};
				};
			};
		};
	}

	private void SmokeFail(string reason)
	{
		GD.PrintErr("LMLOADER-SMOKE-FAIL");
		GD.PrintErr(reason);
		GetTree().Quit(1);
	}

	private static bool WriteSampleConfig(int multiplier)
	{
		using var file = Godot.FileAccess.Open(
			"user://configs/com.lmloader.sample.toml", Godot.FileAccess.ModeFlags.Write);
		if (file is null)
		{
			return false;
		}

		file.StoreString($"[Patch]\nMultiplier = {multiplier}\n");
		return true;
	}
}
