// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Core.Dependency;
using LMLoader.Core.Lifecycle;

namespace LMLoader.Core.Reporting;

public enum ModLoadStatus
{
	Loaded,
	Failed,
	Skipped,
	NotRun,
}

/// <summary>汇总表中的一行(模组 | 状态 | 原因)。</summary>
public sealed class ModReportRow
{
	/// <summary>模组 uid;清单级失败(无法定位模组 uid)时为清单文件路径占位。</summary>
	public required string Uid { get; init; }

	public required ModLoadStatus Status { get; init; }

	/// <summary>原因/说明;成功时通常为空(或 strict 中止说明)。</summary>
	public required string Detail { get; init; }
}

/// <summary>
/// 人可读加载汇总:单点失败不放大,一切可诊断;堆栈只进调试日志,不进汇总表。
/// </summary>
public static class LoadReportFormatter
{
	private const int MaxDetailLength = 160;

	/// <summary>
	/// 合并规划期跳过(元数据错误/冲突)与运行期结果为统一行集。
	/// <paramref name="manifestFailures"/> 为清单级失败(整份清单拒载,以文件路径标识)。
	/// </summary>
	public static IReadOnlyList<ModReportRow> BuildRows(
		LoadPlan? plan,
		LifecycleReport? lifecycle,
		IEnumerable<(string SourcePath, string Reason)>? manifestFailures = null)
	{
		var rows = new List<ModReportRow>();

		if (manifestFailures is not null)
		{
			foreach (var (sourcePath, reason) in manifestFailures)
			{
				rows.Add(new ModReportRow
				{
					Uid = sourcePath,
					Status = ModLoadStatus.Skipped,
					Detail = reason,
				});
			}
		}

		if (plan is not null)
		{
			// 规划期跳过:清单错误、uid 冲突、循环依赖整批拒绝等(从未进入生命周期)
			foreach (var skipped in plan.Skipped)
			{
				rows.Add(new ModReportRow
				{
					Uid = skipped.Uid,
					Status = ModLoadStatus.Skipped,
					Detail = skipped.Reason,
				});
			}
		}

		if (lifecycle is not null)
		{
			foreach (var result in lifecycle.Results)
			{
				var (status, detail) = result.FailedStage is not null
					? (ModLoadStatus.Failed,
						$"{StageLabel(result.FailedStage.Value)}失败: {Summarize(result.Exception)}")
					: result.Skipped
						? (ModLoadStatus.Skipped, string.IsNullOrEmpty(result.Note) ? "已跳过" : result.Note)
						: result.Succeeded
							? (ModLoadStatus.Loaded, result.Note ?? "")
							: (ModLoadStatus.NotRun, result.Note ?? "未执行");

				rows.Add(new ModReportRow
				{
					Uid = result.Uid,
					Status = status,
					Detail = detail,
				});
			}
		}

		return rows;
	}

	public static string Render(
		IReadOnlyList<ModReportRow> rows,
		LoadPlan? plan = null,
		LifecycleReport? lifecycle = null)
	{
		var separator = new string('─', 64);
		var lines = new List<string> { "LMLoader 加载汇总", separator };

		if (plan is { BatchRejected: true })
		{
			lines.Add($"⛔ 循环依赖,整批拒绝: {plan.BatchRejectReason}");
			lines.Add(separator);
		}

		if (rows.Count == 0)
		{
			lines.Add("(无模组)");
		}

		foreach (var row in rows)
		{
			var line = $"{StatusLabel(row.Status)}  {row.Uid}";
			if (!string.IsNullOrEmpty(row.Detail))
			{
				line += $"  |  {row.Detail}";
			}

			lines.Add(line);
		}

		var counts = (
			Loaded: rows.Count(r => r.Status == ModLoadStatus.Loaded),
			Failed: rows.Count(r => r.Status == ModLoadStatus.Failed),
			Skipped: rows.Count(r => r.Status == ModLoadStatus.Skipped),
			NotRun: rows.Count(r => r.Status == ModLoadStatus.NotRun));

		lines.Add(separator);
		var duration = lifecycle is null ? "" : $" | 耗时 {lifecycle.Duration.TotalMilliseconds:F0} ms";
		lines.Add($"模组 {rows.Count} 个:成功 {counts.Loaded},失败 {counts.Failed},跳过 {counts.Skipped},未执行 {counts.NotRun}{duration}");

		return string.Join(Environment.NewLine, lines);
	}

	private static string StageLabel(LifecycleStage stage) =>
		stage switch
		{
			LifecycleStage.AssemblyLoad => "程序集加载",
			LifecycleStage.Instantiation => "实例化",
			LifecycleStage.PreLoad => "PreLoad",
			LifecycleStage.Load => "Load",
			LifecycleStage.PostLoad => "PostLoad",
			_ => stage.ToString(),
		};

	private static string StatusLabel(ModLoadStatus status) =>
		status switch
		{
			ModLoadStatus.Loaded => "✔ 成功",
			ModLoadStatus.Failed => "✘ 失败",
			ModLoadStatus.Skipped => "⊘ 跳过",
			_ => "· 未执行",
		};

	/// <summary>异常摘要:类型 + 消息,截断到上限;完整堆栈仅进调试日志。</summary>
	private static string Summarize(Exception? exception)
	{
		if (exception is null)
		{
			return "(无异常信息)";
		}

		var text = $"{exception.GetType().Name}: {exception.Message}";
		return text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "…";
	}
}
