using LMLoader.Core.Dependency;
using LMLoader.Core.Lifecycle;
using LMLoader.Core.Reporting;

namespace LMLoader.Core.Tests.Reporting;

public class LoadReportFormatterTests
{
	private static LoadPlan Plan(bool rejected = false, string? chain = null, params (string ModuleUid, string Reason)[] skipped) =>
		new()
		{
			Ordered = Array.Empty<ModulePlanItem>(),
			Skipped = skipped.Select(s => new SkippedModule
			{
				ModuleUid = s.ModuleUid,
				ModUid = "com.t.mod",
				Reason = s.Reason,
			}).ToArray(),
			BatchRejected = rejected,
			BatchRejectReason = chain,
		};

	private static LifecycleReport Lifecycle(params ModuleRunResult[] results) =>
		new() { Results = results, StrictAborted = false, Duration = TimeSpan.FromMilliseconds(123) };

	[Fact]
	public void 成功_失败_跳过_行状态与原因完整()
	{
		var rows = LoadReportFormatter.BuildRows(
			Plan(skipped: ("com.t.meta.main", "清单错误: uid 非法")),
			Lifecycle(
				new ModuleRunResult
				{
					ModuleUid = "com.t.ok.main", ModUid = "com.t.ok",
					Succeeded = true,
				},
				new ModuleRunResult
				{
					ModuleUid = "com.t.bad.main", ModUid = "com.t.bad",
					FailedStage = LifecycleStage.Load,
					Exception = new NullReferenceException("Object reference not set"),
				},
				new ModuleRunResult
				{
					ModuleUid = "com.t.dep.main", ModUid = "com.t.dep",
					Skipped = true,
					Note = "硬依赖 \"com.t.bad.main\" 加载失败,级联跳过",
				}));

		Assert.Equal(4, rows.Count);

		var ok = rows.Single(r => r.ModuleUid == "com.t.ok.main");
		Assert.Equal(ModuleLoadStatus.Loaded, ok.Status);

		var bad = rows.Single(r => r.ModuleUid == "com.t.bad.main");
		Assert.Equal(ModuleLoadStatus.Failed, bad.Status);
		Assert.Contains("Load失败", bad.Detail);
		Assert.Contains("NullReferenceException: Object reference not set", bad.Detail);

		var dep = rows.Single(r => r.ModuleUid == "com.t.dep.main");
		Assert.Equal(ModuleLoadStatus.Skipped, dep.Status);
		Assert.Contains("级联跳过", dep.Detail);

		var meta = rows.Single(r => r.ModuleUid == "com.t.meta.main");
		Assert.Equal(ModuleLoadStatus.Skipped, meta.Status);
		Assert.Contains("清单错误", meta.Detail);
	}

	[Fact]
	public void 渲染_包含计数与耗时()
	{
		var rows = LoadReportFormatter.BuildRows(null, Lifecycle(
			new ModuleRunResult { ModuleUid = "com.t.ok.main", ModUid = "com.t.ok", Succeeded = true },
			new ModuleRunResult
			{
				ModuleUid = "com.t.bad.main",
				ModUid = "com.t.bad",
				FailedStage = LifecycleStage.PreLoad,
				Exception = new Exception("boom"),
			}));

		var text = LoadReportFormatter.Render(rows, lifecycle: new LifecycleReport
		{
			Results = rows.Select(r => new ModuleRunResult
			{
				ModuleUid = r.ModuleUid,
				ModUid = r.ModUid,
				Succeeded = r.Status == ModuleLoadStatus.Loaded,
				FailedStage = r.Status == ModuleLoadStatus.Failed ? LifecycleStage.PreLoad : null,
			}).ToArray(),
			StrictAborted = false,
			Duration = TimeSpan.FromMilliseconds(123),
		});

		Assert.Contains("LMLoader 加载汇总", text);
		Assert.Contains("模块 2 个:成功 1,失败 1,跳过 0,未执行 0 | 耗时 123 ms", text);
		Assert.Contains("com.t.ok.main", text);
		Assert.Contains("PreLoad失败: Exception: boom", text);
	}

	[Fact]
	public void 渲染_整批拒绝时输出依赖链()
	{
		var plan = Plan(rejected: true, chain: "com.t.a.main → com.t.b.main → com.t.a.main",
			("com.t.a.main", "存在循环依赖,整批拒绝加载"));

		var text = LoadReportFormatter.Render(LoadReportFormatter.BuildRows(plan, null), plan);

		Assert.Contains("整批拒绝", text);
		Assert.Contains("com.t.a.main → com.t.b.main → com.t.a.main", text);
	}

	[Fact]
	public void 异常摘要超长时截断_不输出堆栈()
	{
		var longMessage = new string('x', 500);
		var rows = LoadReportFormatter.BuildRows(null, Lifecycle(
			new ModuleRunResult
			{
				ModuleUid = "com.t.bad.main",
				ModUid = "com.t.bad",
				FailedStage = LifecycleStage.Load,
				Exception = new Exception(longMessage),
			}));

		var bad = rows.Single(r => r.ModuleUid == "com.t.bad.main");
		Assert.True(bad.Detail.Length < 200);
		Assert.EndsWith("…", bad.Detail);
		Assert.DoesNotContain("at LMLoader", bad.Detail); // 堆栈不进汇总
	}

	[Fact]
	public void 渲染_空模块列表()
	{
		var text = LoadReportFormatter.Render(LoadReportFormatter.BuildRows(null, null));

		Assert.Contains("(无模块)", text);
		Assert.Contains("模块 0 个", text);
	}
}
