using LMLoader.Core.Dependency;
using LMLoader.Core.Manifest;
using LMLoader.Core.Versioning;

namespace LMLoader.Core.Tests.Dependency;

public class DependencyPlannerTests
{
	private static ModDependency Hard(string uid, string? version = null) =>
		new(uid, version is null ? null : SemVer.Parse(version), Soft: false);

	private static ModDependency Soft(string uid) => new(uid, null, Soft: true);

	private static ModDependency HardRange(string uid, string range) =>
		new(uid, null, Soft: false) { Range = VersionRange.Parse(range) };

	/// <summary>模组条目:版本为依赖区间判定锚。</summary>
	private static ModEntry Entry(string uid, string version = "1.0.0", params ModDependency[] depends) =>
		new() { Uid = uid, Version = SemVer.Parse(version), Name = uid, Depends = depends };

	/// <summary>单模组清单(独立文件)。</summary>
	private static ModManifest Mod(string uid, string version = "1.0.0", params ModDependency[] depends) =>
		File($"{uid}.mod.json", Entry(uid, version, depends));

	/// <summary>多模组清单(同一文件,模组间可互依赖)。</summary>
	private static ModManifest File(string sourceName, params ModEntry[] mods) =>
		new()
		{
			SchemaVersion = 1,
			GameId = "com.game.test",
			LoaderVersion = VersionRange.Parse("1.0.0"),
			Mods = mods,
			SourcePath = sourceName,
			Directory = "/mods/x",
		};

	private static LoadPlan Plan(params ModManifest[] mods) => DependencyPlanner.Plan(mods);

	[Fact]
	public void 线性链按拓扑序输出()
	{
		var plan = Plan(
			Mod("com.t.a"),
			Mod("com.t.b", "1.0.0", Hard("com.t.a")),
			Mod("com.t.c", "1.0.0", Hard("com.t.b")));

		Assert.False(plan.BatchRejected);
		Assert.Empty(plan.Skipped);
		Assert.Equal(["com.t.a", "com.t.b", "com.t.c"], plan.Ordered.Select(m => m.Uid));
	}

	[Fact]
	public void 并列时按UID字典序tie_break()
	{
		var plan = Plan(Mod("com.t.z"), Mod("com.t.a"), Mod("com.t.m"));

		Assert.Equal(["com.t.a", "com.t.m", "com.t.z"], plan.Ordered.Select(m => m.Uid));
	}

	[Fact]
	public void 菱形依赖_共享依赖只出现一次()
	{
		var plan = Plan(
			Mod("com.t.d", "1.0.0", Hard("com.t.b"), Hard("com.t.c")),
			Mod("com.t.b", "1.0.0", Hard("com.t.a")),
			Mod("com.t.c", "1.0.0", Hard("com.t.a")),
			Mod("com.t.a"));

		var order = plan.Ordered.Select(m => m.Uid).ToList();

		Assert.Equal(4, order.Count);
		Assert.Equal("com.t.a", order[0]); // 唯一入度 0 者
		Assert.Equal("com.t.d", order[3]); // 必须最后
	}

	[Fact]
	public void 同一清单内模组互依赖_以模组为节点()
	{
		var plan = Plan(File("m.mod.json",
			Entry("com.t.a.ui", "1.0.0", Hard("com.t.a.core")),
			Entry("com.t.a.core")));

		Assert.Equal(["com.t.a.core", "com.t.a.ui"], plan.Ordered.Select(m => m.Uid));
	}

	[Fact]
	public void 循环依赖_整批拒绝并输出完整依赖链()
	{
		var plan = Plan(
			Mod("com.t.a", "1.0.0", Hard("com.t.b")),
			Mod("com.t.b", "1.0.0", Hard("com.t.a")),
			Mod("com.t.c")); // 无关模组同样被整批拒绝

		Assert.True(plan.BatchRejected);
		Assert.Empty(plan.Ordered);
		Assert.NotNull(plan.BatchRejectReason);
		Assert.Contains("com.t.a", plan.BatchRejectReason);
		Assert.Contains("com.t.b", plan.BatchRejectReason);
		Assert.Equal(3, plan.Skipped.Count);
		Assert.All(plan.Skipped, s => Assert.Contains("循环依赖", s.Reason));
	}

	[Fact]
	public void 自依赖_环链为自指()
	{
		var plan = Plan(Mod("com.t.a", "1.0.0", Hard("com.t.a")));

		Assert.True(plan.BatchRejected);
		Assert.Equal("com.t.a → com.t.a", plan.BatchRejectReason);
	}

	[Fact]
	public void 环外尾部节点不混入环链()
	{
		// tail → a → b → a
		var plan = Plan(
			Mod("com.t.t", "1.0.0", Hard("com.t.a")),
			Mod("com.t.a", "1.0.0", Hard("com.t.b")),
			Mod("com.t.b", "1.0.0", Hard("com.t.a")));

		Assert.True(plan.BatchRejected);
		Assert.Equal("com.t.a → com.t.b → com.t.a", plan.BatchRejectReason);
	}

	[Fact]
	public void 硬依赖缺失_该模组跳过并级联()
	{
		var plan = Plan(
			Mod("com.t.x", "1.0.0", Hard("com.t.ghost")),
			Mod("com.t.z", "1.0.0", Hard("com.t.x")),
			Mod("com.t.w"));

		Assert.False(plan.BatchRejected);
		Assert.Equal(["com.t.w"], plan.Ordered.Select(m => m.Uid));

		var xSkip = Assert.Single(plan.Skipped, s => s.Uid == "com.t.x");
		Assert.Contains("未提供", xSkip.Reason);
		Assert.Contains("com.t.ghost", xSkip.Reason);

		var zSkip = Assert.Single(plan.Skipped, s => s.Uid == "com.t.z");
		Assert.Contains("级联跳过", zSkip.Reason);
		Assert.Contains("com.t.x", zSkip.Reason);
	}

	[Fact]
	public void 版本不满足_依赖方跳过_被依赖方正常加载()
	{
		var plan = Plan(
			Mod("com.t.y"),
			Mod("com.t.x", "1.0.0", Hard("com.t.y", "2.0.0")));

		Assert.False(plan.BatchRejected);
		Assert.Equal(["com.t.y"], plan.Ordered.Select(m => m.Uid));
		Assert.Contains(plan.Skipped, s => s.Uid == "com.t.x" && s.Reason.Contains("版本不满足"));
	}

	[Fact]
	public void 版本精确匹配_build元数据不影响()
	{
		var plan = Plan(
			Mod("com.t.y", "1.0.0+build.7"),
			Mod("com.t.x", "1.0.0", Hard("com.t.y", "1.0.0")));

		Assert.Empty(plan.Skipped);
		Assert.Equal(2, plan.Ordered.Count);
	}

	[Fact]
	public void 软依赖存在时仅约束顺序_缺失时不影响加载()
	{
		var withSoft = Plan(
			Mod("com.t.s", "1.0.0", Soft("com.t.h")),
			Mod("com.t.h"));

		Assert.Equal(["com.t.h", "com.t.s"], withSoft.Ordered.Select(m => m.Uid));
		Assert.Empty(withSoft.Skipped);

		var withoutSoft = Plan(Mod("com.t.s", "1.0.0", Soft("com.t.ghost")));

		Assert.Empty(withoutSoft.Skipped);
		Assert.Equal(["com.t.s"], withoutSoft.Ordered.Select(m => m.Uid));
	}

	[Fact]
	public void 软依赖目标失败_约束解除_该模组仍加载()
	{
		var plan = Plan(
			Mod("com.t.f", "1.0.0", Hard("com.t.ghost")),
			Mod("com.t.s", "1.0.0", Soft("com.t.f")));

		Assert.Equal(["com.t.s"], plan.Ordered.Select(m => m.Uid));
		Assert.Single(plan.Skipped, s => s.Uid == "com.t.f");
	}

	[Fact]
	public void 模组uid跨清单冲突_先扫描者胜_后者跳过_D18()
	{
		var plan = Plan(
			File("a.mod.json", Entry("com.t.dup")),
			File("b.mod.json", Entry("com.t.dup")));

		Assert.Single(plan.Ordered, i => i.Uid == "com.t.dup" && i.Manifest.SourcePath == "a.mod.json");
		var loser = Assert.Single(plan.Skipped, s => s.Uid == "com.t.dup");
		Assert.Contains("主根优先", loser.Reason);
	}

	[Fact]
	public void 模组uid冲突时依赖方绑定胜者_D18()
	{
		var plan = Plan(
			File("a.mod.json", Entry("com.t.dup")),
			File("b.mod.json", Entry("com.t.dup")),
			Mod("com.t.c", "1.0.0", Hard("com.t.dup")));

		// 胜者与依赖方正常加载;仅后者副本跳过
		Assert.Equal(["com.t.dup", "com.t.c"], plan.Ordered.Select(m => m.Uid));
		Assert.Single(plan.Skipped, s => s.Uid == "com.t.dup");
	}

	[Fact]
	public void 模组uid同文件内重复_双方均跳过_D18()
	{
		var plan = Plan(File("a.mod.json", Entry("com.t.dup"), Entry("com.t.dup")));

		Assert.Empty(plan.Ordered);
		Assert.Equal(2, plan.Skipped.Count);
		Assert.All(plan.Skipped, s => Assert.Contains("双方均放弃", s.Reason));
	}

	[Fact]
	public void 区间语义_版本落在区间内_正常加载()
	{
		var plan = Plan(
			Mod("com.t.y", "1.4.0"),
			Mod("com.t.x", "1.0.0", HardRange("com.t.y", "^1.2.0")));

		Assert.Empty(plan.Skipped);
		Assert.Equal(2, plan.Ordered.Count);
	}

	[Fact]
	public void 区间语义_版本不在区间内_依赖方跳过()
	{
		var plan = Plan(
			Mod("com.t.y", "2.4.0"),
			Mod("com.t.x", "1.0.0", HardRange("com.t.y", "^1.2.0")));

		Assert.False(plan.BatchRejected);
		Assert.Equal(["com.t.y"], plan.Ordered.Select(m => m.Uid));
		Assert.Contains(plan.Skipped, s => s.Uid == "com.t.x" && s.Reason.Contains("版本不满足"));
		Assert.Contains(plan.Skipped, s => s.Uid == "com.t.x" && s.Reason.Contains("^1.2.0"));
	}

	[Fact]
	public void 区间语义_上限开区间_端点版本被拒()
	{
		var plan = Plan(
			Mod("com.t.y", "2.0.0"),
			Mod("com.t.x", "1.0.0", HardRange("com.t.y", ">=1.0.0")));

		// >=1.0.0 无上限,2.0.0 满足 → 正常
		Assert.Empty(plan.Skipped);

		var plan2 = Plan(
			Mod("com.t.y", "2.0.0"),
			Mod("com.t.x", "1.0.0", HardRange("com.t.y", "<2.0.0")));

		Assert.Contains(plan2.Skipped, s => s.Uid == "com.t.x" && s.Reason.Contains("版本不满足"));
	}
}
