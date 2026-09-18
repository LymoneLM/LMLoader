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

	private const string ModuleSourceTemplate = """
		using System;
		using LMLoader.Api;

		namespace Fixture;

		public class #CLASS# : LmModule
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

	/// <summary>在模组目录生成真实 dll(共享记录器 + 模块),返回清单。</summary>
	private ModManifest BuildModuleMod(
		string uid,
		string className,
		string preload,
		string load,
		string postload,
		params (string TargetUid, bool Soft)[] dependencies)
	{
		var dir = ModDir(uid.Replace('.', '_'));

		// 每个模组目录都带一份记录器;共享 ALC 先载入者胜 → 各模块共享同一静态列表
		var recorderDll = TestCompiler.CompileToDirectory(dir, "CallRecorderLib", RecorderSource);
		var source = ModuleSourceTemplate
			.Replace("#CLASS#", className)
			.Replace("#PRELOAD#", preload)
			.Replace("#LOAD#", load)
			.Replace("#POSTLOAD#", postload);
		TestCompiler.CompileToDirectory(dir, className + "Asm", source, recorderDll, typeof(LmModule).Assembly.Location);

		var modules = new[]
		{
			new ModuleEntry
			{
				Uid = uid + ".main",
				Type = "Fixture." + className,
				Depends = dependencies.Select(d => new ModuleDependency(d.TargetUid + ".main", null, d.Soft)).ToArray(),
			},
		};

		return new ModManifest
		{
			SchemaVersion = 1,
			Uid = uid,
			Name = uid,
			Version = SemVer.Parse("1.0.0"),
			GameId = "com.game.test",
			LoaderVersion = VersionRange.Parse("1.0.0"),
			EntryAssembly = className + "Asm.dll",
			Modules = modules,
			SourcePath = Path.Combine(dir, "mod.mod.json"),
			Directory = dir,
		};
	}

	private static LifecycleReport Run(bool strict, params ModManifest[] mods)
	{
		var plan = DependencyPlanner.Plan(mods);
		Assert.False(plan.BatchRejected, "测试用例不应出现循环依赖");
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmModule).Assembly]);
		var runner = new LifecycleRunner(assemblyLoader, new LoggerRouter(), strict);
		return runner.Execute(plan);
	}

	/// <summary>从任一成功模块实例读取共享记录器内容(全局执行顺序)。</summary>
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
			BuildModuleMod("com.t.c", "ModC", @"Record(""c:preload"");", @"Record(""c:load"");", @"Record(""c:postload"");"),
			BuildModuleMod("com.t.a", "ModA", @"Record(""a:preload"");", @"Record(""a:load"");", @"Record(""a:postload"");"),
			BuildModuleMod("com.t.b", "ModB", @"Record(""b:preload"");", @"Record(""b:load"");", @"Record(""b:postload"");"));

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
			BuildModuleMod("com.t.bad", "ModBad", "", @"throw new Exception(""boom"");", @"Record(""bad:postload"");"),
			BuildModuleMod("com.t.ok", "ModOk", @"Record(""ok:preload"");", @"Record(""ok:load"");", @"Record(""ok:postload"");"));

		Assert.Equal(1, report.SucceededCount);
		Assert.Equal(1, report.FailedCount);

		var bad = report.Results.Single(r => r.ModuleUid == "com.t.bad.main");
		Assert.Equal(LifecycleStage.Load, bad.FailedStage);
		Assert.False(bad.PostLoadExecuted);
		Assert.Equal("boom", bad.Exception!.Message);

		// 无依赖模块不受影响,PostLoad 正常
		var ok = report.Results.Single(r => r.ModuleUid == "com.t.ok.main");
		Assert.True(ok.PostLoadExecuted);
		Assert.DoesNotContain("bad:postload", RecordedCalls(report));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void PreLoad失败_硬依赖级联跳过()
	{
		var report = Run(false,
			BuildModuleMod("com.t.bad", "ModBad", @"throw new Exception(""early boom"");", "", ""),
			BuildModuleMod("com.t.child", "ModChild", @"Record(""child:preload"");", @"Record(""child:load"");", "", ("com.t.bad", false)));

		var bad = report.Results.Single(r => r.ModuleUid == "com.t.bad.main");
		Assert.Equal(LifecycleStage.PreLoad, bad.FailedStage);

		var child = report.Results.Single(r => r.ModuleUid == "com.t.child.main");
		Assert.True(child.Skipped);
		Assert.Null(child.Instance);
		Assert.Contains("com.t.bad.main", child.Note);
		// Skipped=true ⇒ 实例从未创建 ⇒ 任何 hook 均未执行,无需运行期断言
		Assert.False(report.SucceededCount > 0);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 软依赖失败_不级联_该模块仍加载()
	{
		var report = Run(false,
			BuildModuleMod("com.t.bad", "ModBad", @"throw new Exception(""boom"");", "", "", ("com.t.soft", true)),
			BuildModuleMod("com.t.soft", "ModSoft", @"Record(""soft:preload"");", @"Record(""soft:load"");", ""));

		var soft = report.Results.Single(r => r.ModuleUid == "com.t.soft.main");
		Assert.True(soft.Succeeded);
		Assert.False(soft.Skipped);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 类型未继承LmModule_实例化失败()
	{
		var dir = ModDir("wrong-shape");
		TestCompiler.CompileToDirectory(dir, "WrongAsm", """
			namespace Fixture;

			public class NotAModule
			{
			}
			""");
		var manifest = BareManifest("com.t.wrong", dir, "WrongAsm.dll",
			new ModuleEntry { Uid = "com.t.wrong.main", Type = "Fixture.NotAModule" });

		var plan = DependencyPlanner.Plan([manifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmModule).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		var result = Assert.Single(report.Results);
		Assert.Equal(LifecycleStage.Instantiation, result.FailedStage);
		Assert.Contains("未继承", result.Exception!.Message);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 类型不存在_实例化失败并提示全名()
	{
		var dir = ModDir("missing-type");
		TestCompiler.CompileToDirectory(dir, "EmptyAsm", "namespace Fixture;");
		var manifest = BareManifest("com.t.missing", dir, "EmptyAsm.dll",
			new ModuleEntry { Uid = "com.t.missing.main", Type = "Fixture.NoSuchModule" });

		var plan = DependencyPlanner.Plan([manifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmModule).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		var result = Assert.Single(report.Results);
		Assert.Equal(LifecycleStage.Instantiation, result.FailedStage);
		Assert.Contains("未找到类型", result.Exception!.Message);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 程序集文件缺失_AssemblyLoad失败()
	{
		var dir = ModDir("ghost-asm");
		var manifest = BareManifest("com.t.ghost", dir, "Ghost.dll",
			new ModuleEntry { Uid = "com.t.ghost.main", Type = "Fixture.GhostModule" });

		var plan = DependencyPlanner.Plan([manifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmModule).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		var result = Assert.Single(report.Results);
		Assert.Equal(LifecycleStage.AssemblyLoad, result.FailedStage);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void strict模式_失败后中止剩余模块()
	{
		var report = Run(true,
			BuildModuleMod("com.t.a", "ModA", @"Record(""a:preload"");", @"Record(""a:load"");", ""),
			BuildModuleMod("com.t.bad", "ModBad", @"Record(""bad:preload"");", @"throw new Exception(""boom"");", ""),
			BuildModuleMod("com.t.z", "ModZ", @"Record(""z:preload"");", @"Record(""z:load"");", ""));

		Assert.True(report.StrictAborted);
		var bad = report.Results.Single(r => r.ModuleUid == "com.t.bad.main");
		Assert.Equal(LifecycleStage.Load, bad.FailedStage);

		// 已 PreLoad 的 z:不继续 Load,记录原因
		var z = report.Results.Single(r => r.ModuleUid == "com.t.z.main");
		Assert.False(z.Succeeded);
		Assert.NotNull(z.Note);

		// 已成功 Load 的 a:保留成功状态,仅记录 PostLoad 未执行
		var a = report.Results.Single(r => r.ModuleUid == "com.t.a.main");
		Assert.True(a.Succeeded);
		Assert.False(a.PostLoadExecuted);

		var calls = RecordedCalls(report);
		Assert.DoesNotContain("z:load", calls);
		Assert.DoesNotContain("a:postload", calls);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 同模组多模块共享一次程序集加载()
	{
		var dir = ModDir("twin");
		var recorderDll = TestCompiler.CompileToDirectory(dir, "CallRecorderLib", RecorderSource);
		var source = """
			using LMLoader.Api;

			namespace Fixture;

			public class First : LmModule
			{
			    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

			    public override void OnLoad() => CallRecorder.Items.Add("first:load");
			}

			public class Second : LmModule
			{
			    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

			    public override void OnLoad() => CallRecorder.Items.Add("second:load");
			}
			""";
		TestCompiler.CompileToDirectory(dir, "TwinAsm", source, recorderDll, typeof(LmModule).Assembly.Location);

		var manifest = BareManifest("com.t.twin", dir, "TwinAsm.dll",
			new ModuleEntry { Uid = "com.t.twin.first", Type = "Fixture.First" },
			new ModuleEntry { Uid = "com.t.twin.second", Type = "Fixture.Second" });

		var plan = DependencyPlanner.Plan([manifest]);
		using var assemblyLoader = new ModAssemblyLoader(new NullLogger(), [typeof(LmModule).Assembly]);
		var report = new LifecycleRunner(assemblyLoader, new LoggerRouter()).Execute(plan);

		Assert.Equal(2, report.SucceededCount);
		Assert.Equal(["first:load", "second:load"], RecordedCalls(report));
	}

	private static ModManifest BareManifest(string uid, string dir, string assembly, params ModuleEntry[] modules) =>
		new()
		{
			SchemaVersion = 1,
			Uid = uid,
			Name = uid,
			Version = SemVer.Parse("1.0.0"),
			GameId = "com.game.test",
			LoaderVersion = VersionRange.Parse("1.0.0"),
			EntryAssembly = assembly,
			Modules = modules,
			SourcePath = Path.Combine(dir, "mod.mod.json"),
			Directory = dir,
		};
}
