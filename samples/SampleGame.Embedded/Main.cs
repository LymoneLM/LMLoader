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
				SmokeFail(result?.SummaryText ?? "(无加载结果)");
				return;
			}

			if (loader.GetModMountPoint("com.lmloader.sample") is null)
			{
				SmokeFail("模组挂载点缺失(D5)");
				return;
			}

			// 任务 2.4:模组 pck 挂载验证(D10)——pck 早于逻辑挂载,资源在此应可访问;
			// mod_icon.svg 经 Godot 导入(.import 重映射),验证导入产物在宿主挂载后的行为
			var rawText = Godot.FileAccess.Open("res://mods/com.lmloader.sample/hello.txt", Godot.FileAccess.ModeFlags.Read);
			if (rawText is null || rawText.GetAsText().Trim() != "hello-from-pck")
			{
				SmokeFail("pck 内原始文件不可访问");
				return;
			}

			if (!ResourceLoader.Exists("res://mods/com.lmloader.sample/mod_icon.svg"))
			{
				SmokeFail("pck 内导入资源(.import 重映射)不可访问");
				return;
			}

			var texture = ResourceLoader.Load<Texture2D>("res://mods/com.lmloader.sample/mod_icon.svg");
			if (texture is null)
			{
				SmokeFail("导入资源加载失败");
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

			// 任务 4.4:热重载引擎链验证。设计:每轮运行只做一次外部写入(同真实用户编辑器
			// 场景),目标值在 100/250 间交替,使重复运行天然幂等;FSW 事件与 mtime 变化存在
			// OS 级延迟(实测可到秒级),故用轮询等待生效,而非固定时延。
			// 第一步:启动合并链——内存值应等于文件现值(boot 时已合并)。
			var current = ReadSampleConfigMultiplier();
			WaitUntil(() => Add(1, 2) == current, 8, ok =>
			{
				if (!ok)
				{
					SmokeFail($"启动配置/patch 链未生效:Add={Add(1, 2)}(期望 {current})");
					return;
				}

				// 第二步:改文件 → 防抖重载 → 行为跟踪
				var target = current == 250 ? 100 : 250;
				if (!WriteSampleConfig(target))
				{
					SmokeFail("配置文件写入失败");
					return;
				}

				WaitUntil(() => Add(1, 2) == target, 8, ok2 =>
				{
					if (!ok2)
					{
						SmokeFail($"配置热重载未生效:Add={Add(1, 2)}(期望 {target})");
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
				});
			});
		};
	}

	/// <summary>从配置文件提取 Multiplier 当前值;文件缺失/解析失败按声明默认 100。</summary>
	private static int ReadSampleConfigMultiplier()
	{
		try
		{
			var text = System.IO.File.ReadAllText(System.IO.Path.Combine(
				ProjectSettings.GlobalizePath("user://configs"), "com.lmloader.sample.toml"));
			foreach (var line in text.Split('\n'))
			{
				var trimmed = line.Trim();
				if (trimmed.StartsWith("Multiplier", StringComparison.Ordinal))
				{
					var value = trimmed.Split('=', 2)[1].Trim();
					return int.Parse(value);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
		{
		}

		return 100;
	}

	/// <summary>每 0.25s 轮询条件直到满足或超时(秒);结果经回调异步返回。</summary>
	private void WaitUntil(Func<bool> condition, double timeoutSeconds, Action<bool> done)
	{
		var deadline = Time.GetTicksMsec() + (ulong)(timeoutSeconds * 1000);
		void Tick()
		{
			if (condition())
			{
				done(true);
				return;
			}

			if (Time.GetTicksMsec() >= deadline)
			{
				done(false);
				return;
			}

			GetTree().CreateTimer(0.25).Timeout += Tick;
		}

		Tick();
	}

	private void SmokeFail(string reason)
	{
		GD.PrintErr("LMLOADER-SMOKE-FAIL");
		GD.PrintErr(reason);
		GetTree().Quit(1);
	}

	/// <summary>写样例配置(走 System.IO,与用户编辑器/外部工具的真实写入路径一致;
	/// 实测导出环境 Godot FileAccess 写入的 mtime/关闭事件对 .NET 侧不可见)。</summary>
	private static bool WriteSampleConfig(int multiplier)
	{
		var path = System.IO.Path.Combine(
			ProjectSettings.GlobalizePath("user://configs"), "com.lmloader.sample.toml");
		try
		{
			System.IO.File.WriteAllText(path, $"[Patch]\nMultiplier = {multiplier}\n");
			return true;
		}
		catch (Exception ex)
		{
			GD.PushWarning($"写配置失败: {ex.Message}");
			return false;
		}
	}
}
