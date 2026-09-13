// SPDX-License-Identifier: LGPL-3.0-or-later

using Godot;
using LMLoader.Embedded;

/// <summary>
/// 样例游戏主场景:内嵌版一行接入的活文档。
/// 引导节点延迟入树,加载为异步完成:订阅 BootCompleted 后断言结果。
/// headless 冒烟验证:成功输出 LMLOADER-SMOKE-PASS 并以 0 退出;失败输出 LMLOADER-SMOKE-FAIL 以 1 退出。
/// </summary>
public partial class Main : Node
{
	public override void _Ready()
	{
		// 一行接入(D5/D7 能力经 loader 节点访问)
		var loader = LMLoaderEmbedded.Initialize(GetTree(), l =>
		{
			l.GameId = "com.lmloader.samplegame";
			l.ApiVersion = new Version(1, 0, 0); // 样例自定基线;缺省取 LMLoader.Api 程序集版本
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

			GD.Print("LMLOADER-SMOKE-PASS");
			GD.Print(result.SummaryText);
			GetTree().Quit(0);
		};
	}
}
