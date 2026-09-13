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

			GD.Print("LMLOADER-SMOKE-PASS");
			GD.Print(result.SummaryText);
			GetTree().Quit(0);
		};
	}
}
