using LMLoader.Api;
using LMLoader.Core.Dependency;
using LMLoader.Core.Lifecycle;
using LMLoader.Core.Loading;
using LMLoader.Core.Logging;
using LMLoader.Core.Manifest;
using LMLoader.Core.Tests.TestInfrastructure;
using LMLoader.Core.Versioning;

namespace LMLoader.Core.Tests.Lifecycle;

public class LifecycleRunnerTests : IDisposable
{
	private const string RecorderSource = """
		using System.Collections.Generic;

		public static class CallRecorder
		{
		    public static List<string> Items = new();
		}
		""";

	private const string ModSourceTemplate = """
		using System;
		using LMLoader.Api;

		namespace Fixture;

		[LmMod("#UID#")]
		public class #CLASS# : LmMod
		{
		    // 经本类型静态属性读取共享记录器(解析结果即共享 ALC 中的那份副本)
		    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

		    private static void Record(string entry) => CallRecorder.Items.Add(entry);

		    public override void OnPreLoad() { #PRELOAD# }
		    public override void OnLoad() { #LOAD# }
		    public override void OnPostLoad() { #POSTLOAD# }
		}
		""";

	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmloader-lifecycle-{Guid.NewGuid():N}");

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private string ModDir(string name)
	{
		var path = Path.Combine(_root, name);
		Directory.CreateDirectory(path);
		return path;
	}

	/// <summary>在模组目录生成真实 dll(共享记录器 + 带 [LmMod] 特性的模组类),返回清单。</summary>
	private ModManifest BuildMod(
		string uid,
		string className,
		string preload,
		string load,
		string postload,
		params (string TargetUid, bool Soft)[] dependencies)
	{
		var dir = ModDir(uid.Replace('.', '_'));

		// 每个模组目录都带一份记录器;共享 ALC 先载入者胜 → 各模组共享同一静态列表
		var recorderDll = TestCompiler.CompileToDirectory(dir, "CallRecorderLib", RecorderSource);
		var source = ModSourceTemplate
			.Replace("#UID#", uid)
			.Replace("#CLASS#", className)
			.Replace("#PRELOAD#", preload)
			.Replace("#LOAD#", load)
			.Replace("#POSTLOAD#", postload);
		TestCompiler.CompileToDirectory(dir, className + "Asm", source, recorderDll, typeof(LmMod).Assembly.Location);

		return Manifest(uid, dir,
			new ModEntry
			{
				Uid = uid,
				Version = SemVer.Parse("1.0.0"),
				Name = uid,
				Assemblies = [className + "Asm.dll"],
				Depends = dependencies.Select(d => new ModDependency(d.TargetUid, null, d.Soft)).ToArray(),
			});
	}

	private static ModManifest Manifest(string uid, string dir, params ModEntry[] mods) =>
		new()
		{
			SchemaVersion = 1,
			GameId = "com.game.test",
			LoaderVersion = VersionRange.Parse("1.0.0"),
			Mods = mods,
			SourcePath = Path.Combine(dir, "mod.mod.json"),
			Directory = dir,
		};

	private static LifecycleReport Run(bool strict, params ModManifest[] mods)
	{
		var plan = DependencyPlanner.Plan(mods);
		Assert.False(plan.BatchRejected, "测试用例不应出现循环依赖");
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmMod).Assembly]);
		var runner = new LifecycleRunner(assemblyLoader, new LoggerRouter(), strict);
		return runner.Execute(plan);
	}

	/// <summary>从任一成功模组实例读取共享记录器内容(全局执行顺序)。</summary>
	private static List<string> RecordedCalls(LifecycleReport report)
	{
		var anyInstance = report.Results.Select(r => r.Instance).FirstOrDefault(i => i is not null);
		Assert.NotNull(anyInstance);

		var property = anyInstance.GetType().GetProperty("Calls");
		Assert.NotNull(property);

		return (List<string>)property.GetValue(null)!;
	}

	private sealed class NullLogger : ILmLogger
	{
		public void Log(LmLogLevel level, string message, Exception? exception = null)
		{
		}
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 全部成功_三阶段批处理执行()
	{
		var report = Run(false,
			BuildMod("com.t.c", "ModC", @"Record(""c:preload"");", @"Record(""c:load"");", @"Record(""c:postload"");"),
			BuildMod("com.t.a", "ModA", @"Record(""a:preload"");", @"Record(""a:load"");", @"Record(""a:postload"");"),
			BuildMod("com.t.b", "ModB", @"Record(""b:preload"");", @"Record(""b:load"");", @"Record(""b:postload"");"));

		Assert.Equal(3, report.SucceededCount);
		Assert.Equal(0, report.FailedCount);
		Assert.All(report.Results, r => Assert.True(r.PostLoadExecuted));

		// 三阶段批处理:先全部 PreLoad(UID 序),再全部 Load,再全部 PostLoad
		Assert.Equal(
		[
			"a:preload", "b:preload", "c:preload",
			"a:load", "b:load", "c:load",
			"a:postload", "b:postload", "c:postload",
		], RecordedCalls(report));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void Load失败_不扩散_自身PostLoad跳过()
	{
		var report = Run(false,
			BuildMod("com.t.bad", "ModBad", "", @"throw new Exception(""boom"");", @"Record(""bad:postload"");"),
			BuildMod("com.t.ok", "ModOk", @"Record(""ok:preload"");", @"Record(""ok:load"");", @"Record(""ok:postload"");"));

		Assert.Equal(1, report.SucceededCount);
		Assert.Equal(1, report.FailedCount);

		var bad = report.Results.Single(r => r.Uid == "com.t.bad");
		Assert.Equal(LifecycleStage.Load, bad.FailedStage);
		Assert.False(bad.PostLoadExecuted);
		Assert.Equal("boom", bad.Exception!.Message);

		// 无依赖模组不受影响,PostLoad 正常
		var ok = report.Results.Single(r => r.Uid == "com.t.ok");
		Assert.True(ok.PostLoadExecuted);
		Assert.DoesNotContain("bad:postload", RecordedCalls(report));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void PreLoad失败_硬依赖级联跳过()
	{
		var report = Run(false,
			BuildMod("com.t.bad", "ModBad", @"throw new Exception(""early boom"");", "", ""),
			BuildMod("com.t.child", "ModChild", @"Record(""child:preload"");", @"Record(""child:load"");", "", ("com.t.bad", false)));

		var bad = report.Results.Single(r => r.Uid == "com.t.bad");
		Assert.Equal(LifecycleStage.PreLoad, bad.FailedStage);

		var child = report.Results.Single(r => r.Uid == "com.t.child");
		Assert.True(child.Skipped);
		Assert.Null(child.Instance);
		Assert.Contains("com.t.bad", child.Note);
		// Skipped=true ⇒ 实例从未创建 ⇒ 任何 hook 均未执行,无需运行期断言
		Assert.False(report.SucceededCount > 0);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 软依赖失败_不级联_该模组仍加载()
	{
		var report = Run(false,
			BuildMod("com.t.bad", "ModBad", @"throw new Exception(""boom"");", "", "", ("com.t.soft", true)),
			BuildMod("com.t.soft", "ModSoft", @"Record(""soft:preload"");", @"Record(""soft:load"");", ""));

		var soft = report.Results.Single(r => r.Uid == "com.t.soft");
		Assert.True(soft.Succeeded);
		Assert.False(soft.Skipped);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 程序集文件缺失_AssemblyLoad失败()
	{
		var dir = ModDir("ghost-asm");
		var manifest = Manifest("com.t.ghost", dir,
			new ModEntry
			{
				Uid = "com.t.ghost",
				Version = SemVer.Parse("1.0.0"),
				Name = "ghost",
				Assemblies = ["Ghost.dll"],
			});

		var plan = DependencyPlanner.Plan([manifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmMod).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		var result = Assert.Single(report.Results);
		Assert.Equal(LifecycleStage.AssemblyLoad, result.FailedStage);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void strict模式_失败后中止剩余模块()
	{
		var report = Run(true,
			BuildMod("com.t.a", "ModA", @"Record(""a:preload"");", @"Record(""a:load"");", ""),
			BuildMod("com.t.bad", "ModBad", @"Record(""bad:preload"");", @"throw new Exception(""boom"");", ""),
			BuildMod("com.t.z", "ModZ", @"Record(""z:preload"");", @"Record(""z:load"");", ""));

		Assert.True(report.StrictAborted);
		var bad = report.Results.Single(r => r.Uid == "com.t.bad");
		Assert.Equal(LifecycleStage.Load, bad.FailedStage);

		// 已 PreLoad 的 z:不继续 Load,记录原因
		var z = report.Results.Single(r => r.Uid == "com.t.z");
		Assert.False(z.Succeeded);
		Assert.NotNull(z.Note);

		// 已成功 Load 的 a:保留成功状态,仅记录 PostLoad 未执行
		var a = report.Results.Single(r => r.Uid == "com.t.a");
		Assert.True(a.Succeeded);
		Assert.False(a.PostLoadExecuted);

		var calls = RecordedCalls(report);
		Assert.DoesNotContain("z:load", calls);
		Assert.DoesNotContain("a:postload", calls);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 一程序集多模组_绑定各自特性类且程序集只加载一次_D18()
	{
		var dir = ModDir("twin");
		var recorderDll = TestCompiler.CompileToDirectory(dir, "CallRecorderLib", RecorderSource);
		var source = """
			using LMLoader.Api;

			namespace Fixture;

			[LmMod("com.t.twin.first")]
			public class First : LmMod
			{
			    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

			    public override void OnLoad() => CallRecorder.Items.Add("first:load");
			}

			[LmMod("com.t.twin.second")]
			public class Second : LmMod
			{
			    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

			    public override void OnLoad() => CallRecorder.Items.Add("second:load");
			}
			""";
		TestCompiler.CompileToDirectory(dir, "TwinAsm", source, recorderDll, typeof(LmMod).Assembly.Location);

		var manifest = Manifest("com.t.twin", dir,
			new ModEntry { Uid = "com.t.twin.first", Version = SemVer.Parse("1.0.0"), Name = "first", Assemblies = ["TwinAsm.dll"] },
			new ModEntry { Uid = "com.t.twin.second", Version = SemVer.Parse("1.0.0"), Name = "second", Assemblies = ["TwinAsm.dll"] });

		var plan = DependencyPlanner.Plan([manifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmMod).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		Assert.Equal(2, report.SucceededCount);
		Assert.Empty(report.BindingWarnings);
		Assert.Equal(["first:load", "second:load"], RecordedCalls(report));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 纯资源模组_无实例无钩子_直接成功_D18()
	{
		var dir = ModDir("assets-only");
		var manifest = Manifest("com.t.assets", dir,
			new ModEntry { Uid = "com.t.assets", Version = SemVer.Parse("1.0.0"), Name = "assets", PckResources = ["pcks/a.pck"] });

		var plan = DependencyPlanner.Plan([manifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmMod).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		var result = Assert.Single(report.Results);
		Assert.True(result.Succeeded);
		Assert.True(result.ResourceOnly);
		Assert.Null(result.Instance);
		Assert.False(result.PostLoadExecuted);
		Assert.Contains("纯资源", result.Note);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 声明assemblies但无绑定类_实例化失败并提示_D18()
	{
		var dir = ModDir("no-binding");
		TestCompiler.CompileToDirectory(dir, "EmptyAsm", "namespace Fixture;", typeof(LmMod).Assembly.Location);
		var manifest = Manifest("com.t.nobind", dir,
			new ModEntry { Uid = "com.t.nobind", Version = SemVer.Parse("1.0.0"), Name = "nobind", Assemblies = ["EmptyAsm.dll"] });

		var plan = DependencyPlanner.Plan([manifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmMod).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		var result = Assert.Single(report.Results);
		Assert.Equal(LifecycleStage.Instantiation, result.FailedStage);
		Assert.Contains("com.t.nobind", result.Exception!.Message);
		Assert.Contains("LmMod", result.Exception.Message);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 继承LmMod未标注特性_告警并忽略_模组绑定失败_D18()
	{
		var dir = ModDir("no-attr");
		TestCompiler.CompileToDirectory(dir, "NoAttrAsm", """
			using LMLoader.Api;

			namespace Fixture;

			public class NoAttribute : LmMod
			{
			}
			""", typeof(LmMod).Assembly.Location);
		var manifest = Manifest("com.t.noattr", dir,
			new ModEntry { Uid = "com.t.noattr", Version = SemVer.Parse("1.0.0"), Name = "noattr", Assemblies = ["NoAttrAsm.dll"] });

		var plan = DependencyPlanner.Plan([manifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmMod).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		var result = Assert.Single(report.Results);
		Assert.Equal(LifecycleStage.Instantiation, result.FailedStage);
		Assert.Contains(report.BindingWarnings, w => w.Contains("未标注 [LmMod(uid)]"));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 孤立特性类_uid未被声明_警告_D18()
	{
		var dir = ModDir("orphan");
		TestCompiler.CompileToDirectory(dir, "OrphanAsm", """
			using LMLoader.Api;

			namespace Fixture;

			[LmMod("com.t.orphan.undeclared")]
			public class Orphan : LmMod
			{
			}
			""", typeof(LmMod).Assembly.Location);
		var manifest = Manifest("com.t.host", dir,
			new ModEntry { Uid = "com.t.host", Version = SemVer.Parse("1.0.0"), Name = "host", Assemblies = ["OrphanAsm.dll"] });

		var plan = DependencyPlanner.Plan([manifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmMod).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		var result = Assert.Single(report.Results);
		Assert.Equal(LifecycleStage.Instantiation, result.FailedStage); // host 自身无绑定类
		Assert.Contains(report.BindingWarnings, w => w.Contains("com.t.orphan.undeclared") && w.Contains("未被本次加载的清单声明"));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 同uid多类型声明_绑定歧义报错_D18()
	{
		var dir = ModDir("dup-uid");
		TestCompiler.CompileToDirectory(dir, "DupAsm", """
			using LMLoader.Api;

			namespace Fixture;

			[LmMod("com.t.dup")]
			public class One : LmMod
			{
			}

			[LmMod("com.t.dup")]
			public class Two : LmMod
			{
			}
			""", typeof(LmMod).Assembly.Location);
		var manifest = Manifest("com.t.dup", dir,
			new ModEntry { Uid = "com.t.dup", Version = SemVer.Parse("1.0.0"), Name = "dup", Assemblies = ["DupAsm.dll"] });

		var plan = DependencyPlanner.Plan([manifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmMod).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		var result = Assert.Single(report.Results);
		Assert.Equal(LifecycleStage.Instantiation, result.FailedStage);
		Assert.Contains("绑定歧义", result.Exception!.Message);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 一dll服务多模组_联合绑定跨清单生效_D18()
	{
		var dir = ModDir("shared-dll");
		var recorderDll = TestCompiler.CompileToDirectory(dir, "CallRecorderLib", RecorderSource);
		var source = """
			using LMLoader.Api;

			namespace Fixture;

			[LmMod("com.t.lib")]
			public class LibMod : LmMod
			{
			    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

			    public override void OnLoad() => CallRecorder.Items.Add("lib:load");
			}

			[LmMod("com.t.consumer")]
			public class ConsumerMod : LmMod
			{
			    public override void OnLoad() => CallRecorder.Items.Add("consumer:load");
			}
			""";
		TestCompiler.CompileToDirectory(dir, "SharedAsm", source, recorderDll, typeof(LmMod).Assembly.Location);

		// consumer 自身未列 assemblies,其类经 lib 的程序集联合绑定
		var libManifest = Manifest("com.t.lib", dir,
			new ModEntry { Uid = "com.t.lib", Version = SemVer.Parse("1.0.0"), Name = "lib", Assemblies = ["SharedAsm.dll"] });
		var consumerManifest = Manifest("com.t.consumer", dir,
			new ModEntry
			{
				Uid = "com.t.consumer",
				Version = SemVer.Parse("1.0.0"),
				Name = "consumer",
				Assemblies = ["SharedAsm.dll"],
				Depends = [new ModDependency("com.t.lib", null, Soft: false)],
			});

		var plan = DependencyPlanner.Plan([libManifest, consumerManifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmMod).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		Assert.Equal(2, report.SucceededCount);
		Assert.Empty(plan.Skipped);
		Assert.Equal(["lib:load", "consumer:load"], RecordedCalls(report));
	}
}
