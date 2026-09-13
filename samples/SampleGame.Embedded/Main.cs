// SPDX-License-Identifier: LGPL-3.0-or-later

using Godot;
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
				GD.PrintErr("LMLOADER-SMOKE-FAIL");
				GD.PrintErr("静态方法 patch 未生效");
				GetTree().Quit(1);
				return;
			}

			// 引擎 native→managed 路径:_Process 由引擎每帧调用,postfix 计数应持续增长
			GetTree().CreateTimer(0.5).Timeout += () =>
			{
				if (ProcessPatchHits == 0)
				{
					GD.PrintErr("LMLOADER-SMOKE-FAIL");
					GD.PrintErr("_Process postfix 未命中(native→managed)");
					GetTree().Quit(1);
					return;
				}

				GD.Print($"LMLOADER-SMOKE-PASS (patch hits: {ProcessPatchHits})");
				GD.Print(result.SummaryText);
				GetTree().Quit(0);
			};
		};
	}
}
