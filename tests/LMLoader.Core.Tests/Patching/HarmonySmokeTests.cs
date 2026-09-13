using HarmonyLib;

namespace LMLoader.Core.Tests.Patching;

/// <summary>
/// HarmonyX 运行时冒烟(D12:锁定 2.16.x/25.x):在 net8 单测环境对自身程序集静态方法
/// 打 prefix 并还原,验证包引用与 detour 基础可用(引擎内 native→managed 路径由阶段 3.4 样例覆盖)。
/// </summary>
public class HarmonySmokeTests
{
	public static int Add(int a, int b) => a + b;

	public static bool PrefixSkip(int a, int b, ref int __result)
	{
		__result = 100;
		return false; // 跳过原方法
	}

	[Fact]
	public void Patch_静态方法生效_Unpatch后还原()
	{
		const string id = "test.harmony.smoke";

		Assert.Equal(3, Add(1, 2));

		var harmony = new Harmony(id);
		var original = typeof(HarmonySmokeTests).GetMethod(nameof(Add))!;
		harmony.Patch(original, prefix: new HarmonyMethod(typeof(HarmonySmokeTests), nameof(PrefixSkip)));

		Assert.Equal(100, Add(1, 2));

		Harmony.UnpatchID(id);
		Assert.Equal(3, Add(1, 2));
	}
}
