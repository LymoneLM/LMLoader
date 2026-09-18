using LMLoader.Core.Dependency;
using LMLoader.Core.Manifest;
using LMLoader.Core.Versioning;

namespace LMLoader.Core.Tests.Dependency;

public class DependencyPlannerTests
{
	private static ModuleDependency Hard(string uid, string? version = null) =>
		new(uid, version is null ? null : SemVer.Parse(version), Soft: false);

	private static ModuleDependency Soft(string uid) => new(uid, null, Soft: true);

	private static ModuleDependency HardRange(string uid, string range) =>
		new(uid, null, Soft: false) { Range = VersionRange.Parse(range) };

	private static ModuleEntry Module(string uid, params ModuleDependency[] depends) =>
		new() { Uid = uid, Type = $"Test.{uid.Replace('.', '_')}", Depends = depends };

	private static ModManifest Mod(string uid, string version, params ModuleEntry[] modules) =>
		new()
		{
			SchemaVersion = 1,
			Uid = uid,
			Name = uid,
			Version = SemVer.Parse(version),
			GameId = "com.game.test",
			LoaderVersion = VersionRange.Parse("1.0.0"),
			EntryAssembly = "Mod.dll",
			Modules = modules,
			SourcePath = $"{uid}.mod.json",
			Directory = $"/mods/{uid}",
		};

	private static LoadPlan Plan(params ModManifest[] mods) => DependencyPlanner.Plan(mods);

	[Fact]
	public void 线性链按拓扑序输出()
	{
		var plan = Plan(
			Mod("com.t.a", "1.0.0", Module("com.t.a.main")),
			Mod("com.t.b", "1.0.0", Module("com.t.b.main", Hard("com.t.a.main"))),
			Mod("com.t.c", "1.0.0", Module("com.t.c.main", Hard("com.t.b.main"))));

		Assert.False(plan.BatchRejected);
		Assert.Empty(plan.Skipped);
		Assert.Equal(
			["com.t.a.main", "com.t.b.main", "com.t.c.main"],
			plan.Ordered.Select(m => m.ModuleUid));
	}

	[Fact]
	public void 并列时按UID字典序tie_break()
	{
		var plan = Plan(
			Mod("com.t.z", "1.0.0", Module("com.t.z.main")),
			Mod("com.t.a", "1.0.0", Module("com.t.a.main")),
			Mod("com.t.m", "1.0.0", Module("com.t.m.main")));

		Assert.Equal(
			["com.t.a.main", "com.t.m.main", "com.t.z.main"],
			plan.Ordered.Select(m => m.ModuleUid));
	}

	[Fact]
	public void 菱形依赖_共享依赖只出现一次()
	{
		var plan = Plan(
			Mod("com.t.d", "1.0.0", Module("com.t.d.top", Hard("com.t.b.mid"), Hard("com.t.c.mid"))),
			Mod("com.t.b", "1.0.0", Module("com.t.b.mid", Hard("com.t.a.base"))),
			Mod("com.t.c", "1.0.0", Module("com.t.c.mid", Hard("com.t.a.base"))),
			Mod("com.t.a", "1.0.0", Module("com.t.a.base")));

		var order = plan.Ordered.Select(m => m.ModuleUid).ToList();

		Assert.Equal(4, order.Count);
		Assert.Equal("com.t.a.base", order[0]); // 唯一入度 0 者
		Assert.Equal("com.t.d.top", order[3]); // 必须最后
	}

	[Fact]
	public void 同一Assembly内模块互依赖_以模块为节点()
	{
		var plan = Plan(Mod("com.t.a", "1.0.0",
			Module("com.t.a.ui", Hard("com.t.a.core")),
			Module("com.t.a.core")));

		Assert.Equal(["com.t.a.core", "com.t.a.ui"], plan.Ordered.Select(m => m.ModuleUid));
	}

	[Fact]
	public void 循环依赖_整批拒绝并输出完整依赖链()
	{
		var plan = Plan(
			Mod("com.t.a", "1.0.0", Module("com.t.a.main", Hard("com.t.b.main"))),
			Mod("com.t.b", "1.0.0", Module("com.t.b.main", Hard("com.t.a.main"))),
			Mod("com.t.c", "1.0.0", Module("com.t.c.main"))); // 无关模块同样被整批拒绝

		Assert.True(plan.BatchRejected);
		Assert.Empty(plan.Ordered);
		Assert.NotNull(plan.BatchRejectReason);
		Assert.Contains("com.t.a.main", plan.BatchRejectReason);
		Assert.Contains("com.t.b.main", plan.BatchRejectReason);
		Assert.Equal(3, plan.Skipped.Count);
		Assert.All(plan.Skipped, s => Assert.Contains("循环依赖", s.Reason));
	}

	[Fact]
	public void 自依赖_环链为自指()
	{
		var plan = Plan(Mod("com.t.a", "1.0.0", Module("com.t.a.main", Hard("com.t.a.main"))));

		Assert.True(plan.BatchRejected);
		Assert.Equal("com.t.a.main → com.t.a.main", plan.BatchRejectReason);
	}

	[Fact]
	public void 环外尾部节点不混入环链()
	{
		// tail → a → b → a
		var plan = Plan(
			Mod("com.t.t", "1.0.0", Module("com.t.t.tail", Hard("com.t.a.main"))),
			Mod("com.t.a", "1.0.0", Module("com.t.a.main", Hard("com.t.b.main"))),
			Mod("com.t.b", "1.0.0", Module("com.t.b.main", Hard("com.t.a.main"))));

		Assert.True(plan.BatchRejected);
		Assert.Equal("com.t.a.main → com.t.b.main → com.t.a.main", plan.BatchRejectReason);
	}

	[Fact]
	public void 硬依赖缺失_该模块跳过并级联()
	{
		var plan = Plan(
			Mod("com.t.x", "1.0.0", Module("com.t.x.main", Hard("com.t.ghost.main"))),
			Mod("com.t.z", "1.0.0", Module("com.t.z.main", Hard("com.t.x.main"))),
			Mod("com.t.w", "1.0.0", Module("com.t.w.main")));

		Assert.False(plan.BatchRejected);
		Assert.Equal(["com.t.w.main"], plan.Ordered.Select(m => m.ModuleUid));

		var xSkip = Assert.Single(plan.Skipped, s => s.ModuleUid == "com.t.x.main");
		Assert.Contains("未提供", xSkip.Reason);
		Assert.Contains("com.t.ghost.main", xSkip.Reason);

		var zSkip = Assert.Single(plan.Skipped, s => s.ModuleUid == "com.t.z.main");
		Assert.Contains("级联跳过", zSkip.Reason);
		Assert.Contains("com.t.x.main", zSkip.Reason);
	}

	[Fact]
	public void 版本不满足_依赖方跳过_被依赖方正常加载()
	{
		var plan = Plan(
			Mod("com.t.y", "1.0.0", Module("com.t.y.main")),
			Mod("com.t.x", "1.0.0", Module("com.t.x.main", Hard("com.t.y.main", "2.0.0"))));

		Assert.False(plan.BatchRejected);
		Assert.Equal(["com.t.y.main"], plan.Ordered.Select(m => m.ModuleUid));
		Assert.Contains(plan.Skipped, s => s.ModuleUid == "com.t.x.main" && s.Reason.Contains("版本不满足"));
	}

	[Fact]
	public void 版本精确匹配_build元数据不影响()
	{
		var plan = Plan(
			Mod("com.t.y", "1.0.0+build.7", Module("com.t.y.main")),
			Mod("com.t.x", "1.0.0", Module("com.t.x.main", Hard("com.t.y.main", "1.0.0"))));

		Assert.Empty(plan.Skipped);
		Assert.Equal(2, plan.Ordered.Count);
	}

	[Fact]
	public void 软依赖存在时仅约束顺序_缺失时不影响加载()
	{
		var withSoft = Plan(
			Mod("com.t.s", "1.0.0", Module("com.t.s.main", Soft("com.t.h.main"))),
			Mod("com.t.h", "1.0.0", Module("com.t.h.main")));

		Assert.Equal(["com.t.h.main", "com.t.s.main"], withSoft.Ordered.Select(m => m.ModuleUid));
		Assert.Empty(withSoft.Skipped);

		var withoutSoft = Plan(Mod("com.t.s", "1.0.0", Module("com.t.s.main", Soft("com.t.ghost.main"))));

		Assert.Empty(withoutSoft.Skipped);
		Assert.Equal(["com.t.s.main"], withoutSoft.Ordered.Select(m => m.ModuleUid));
	}

	[Fact]
	public void 软依赖目标失败_约束解除_该模块仍加载()
	{
		var plan = Plan(
			Mod("com.t.f", "1.0.0", Module("com.t.f.main", Hard("com.t.ghost.main"))),
			Mod("com.t.s", "1.0.0", Module("com.t.s.main", Soft("com.t.f.main"))));

		Assert.Equal(["com.t.s.main"], plan.Ordered.Select(m => m.ModuleUid));
		Assert.Single(plan.Skipped, s => s.ModuleUid == "com.t.f.main");
	}

	[Fact]
	public void 模块uid跨模组冲突_双方跳过_依赖方级联()
	{
		var plan = Plan(
			Mod("com.t.a", "1.0.0", Module("com.t.dup")),
			Mod("com.t.b", "1.0.0", Module("com.t.dup")),
			Mod("com.t.c", "1.0.0", Module("com.t.c.main", Hard("com.t.dup"))));

		// 两个冲突副本与级联的依赖方均不入列
		Assert.Empty(plan.Ordered);
		Assert.Equal(3, plan.Skipped.Count);

		var cSkip = Assert.Single(plan.Skipped, s => s.ModuleUid == "com.t.c.main");
		Assert.Contains("com.t.dup", cSkip.Reason);
		Assert.Equal(2, plan.Skipped.Count(s => s.ModuleUid == "com.t.dup"));
	}

	[Fact]
	public void 区间语义_版本落在区间内_正常加载()
	{
		var plan = Plan(
			Mod("com.t.y", "1.4.0", Module("com.t.y.main")),
			Mod("com.t.x", "1.0.0", Module("com.t.x.main", HardRange("com.t.y.main", "^1.2.0"))));

		Assert.Empty(plan.Skipped);
		Assert.Equal(2, plan.Ordered.Count);
	}

	[Fact]
	public void 区间语义_版本不在区间内_依赖方跳过()
	{
		var plan = Plan(
			Mod("com.t.y", "2.4.0", Module("com.t.y.main")),
			Mod("com.t.x", "1.0.0", Module("com.t.x.main", HardRange("com.t.y.main", "^1.2.0"))));

		Assert.False(plan.BatchRejected);
		Assert.Equal(["com.t.y.main"], plan.Ordered.Select(m => m.ModuleUid));
		Assert.Contains(plan.Skipped, s => s.ModuleUid == "com.t.x.main" && s.Reason.Contains("版本不满足"));
		Assert.Contains(plan.Skipped, s => s.ModuleUid == "com.t.x.main" && s.Reason.Contains("^1.2.0"));
	}

	[Fact]
	public void 区间语义_上限开区间_端点版本被拒()
	{
		var plan = Plan(
			Mod("com.t.y", "2.0.0", Module("com.t.y.main")),
			Mod("com.t.x", "1.0.0", Module("com.t.x.main", HardRange("com.t.y.main", ">=1.0.0"))));

		// >=1.0.0 无上限,2.0.0 满足 → 正常
		Assert.Empty(plan.Skipped);

		var plan2 = Plan(
			Mod("com.t.y", "2.0.0", Module("com.t.y.main")),
			Mod("com.t.x", "1.0.0", Module("com.t.x.main", HardRange("com.t.y.main", "<2.0.0"))));

		Assert.Contains(plan2.Skipped, s => s.ModuleUid == "com.t.x.main" && s.Reason.Contains("版本不满足"));
	}
}
