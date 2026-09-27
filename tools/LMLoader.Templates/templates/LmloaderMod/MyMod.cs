using LMLoader.Api;

namespace MyMod;

/// <summary>模组入口:特性 uid 必须与 mod.json mods[].uid 一致(运行期按此绑定)。</summary>
[LmMod("COM_AUTHOR_MYMOD")]
public class MyModModule : LmMod
{
	public override void OnPreLoad()
	{
		// 声明配置项(只能在 OnPreLoad;详见 docs/mod-config.md):
		// Config.Bind("General", "Enabled", true, "启用示例功能");
	}

	public override void OnLoad()
	{
		Logger.Info($"{Uid} loaded");
		// 主入口:读配置、发布跨模组服务、patch(HarmonyX 由模组自带):
		// var patcher = new Harmony(Uid);
		// PublishService<IMyService>(new MyService());
	}

	public override void OnPostLoad()
	{
		// 全部模组 Load 完成后:消费其他模组发布的服务(TryGetService<T>(out var s))
	}
}
