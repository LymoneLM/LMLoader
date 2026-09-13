// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Reflection;
using LMLoader.Api;
using LMLoader.Api.Config;
using LMLoader.Core.Dependency;
using LMLoader.Core.Loading;
using LMLoader.Core.Logging;

namespace LMLoader.Core.Lifecycle;

/// <summary>
/// 生命周期执行器(D7 宽松失败传播):
/// - 三阶段批处理:PreLoad(全部) → Load(全部) → PostLoad(仅 PreLoad/Load 成功者);
///   程序集加载与实例化内联于 PreLoad 阶段,级联跳过的模块不产生实例;
/// - 全部 hook 边界 try/catch,单模块异常不杀游戏、不阻塞无依赖模块;
/// - 运行期失败级联跳过尚未执行到对应阶段的硬依赖模块(跳过不回退已完成的阶段);
/// - strict 开关预留:任一失败即中止,v1 默认宽松。
/// </summary>
public sealed class LifecycleRunner
{
	/// <summary>loader 自身日志的 modUid 标识(D6:日志按模组划分,loader 视为特殊模组)。</summary>
	public const string LoaderLogUid = "LMLoader";

	private const string StrictAbortNote = "strict 模式:前序模块失败,中止加载(D7 预留开关)";

	private readonly ModAssemblyLoader _assemblyLoader;
	private readonly LoggerRouter _loggerRouter;
	private readonly ServiceRegistry? _serviceRegistry;
	private readonly bool _strict;
	private readonly Config.ConfigManager? _configManager;
	private readonly Dictionary<string, Api.Config.ModConfig> _modConfigs = new(StringComparer.Ordinal);

	public LifecycleRunner(
		ModAssemblyLoader assemblyLoader,
		LoggerRouter loggerRouter,
		bool strict = false,
		ServiceRegistry? serviceRegistry = null,
		Config.ConfigManager? configManager = null)
	{
		_assemblyLoader = assemblyLoader ?? throw new ArgumentNullException(nameof(assemblyLoader));
		_loggerRouter = loggerRouter ?? throw new ArgumentNullException(nameof(loggerRouter));
		_serviceRegistry = serviceRegistry;
		_configManager = configManager;
		_strict = strict;
	}

	public LifecycleReport Execute(LoadPlan plan)
	{
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		var loaderLogger = _loggerRouter.GetLogger(LoaderLogUid);
		_modConfigs.Clear();
		var states = plan.Ordered.Select(item => new RunState(item)).ToArray();

		var hardDependents = BuildHardDependents(states);
		var modAssemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
		var aborted = false;

		// ---- PreLoad:程序集加载 + 实例化 + OnPreLoad ----
		foreach (var state in states)
		{
			if (aborted)
			{
				if (state.FailedStage is null && !state.Skipped)
				{
					MarkSkipped(state, StrictAbortNote);
				}

				continue;
			}

			if (state.Skipped || state.FailedStage is not null)
			{
				continue;
			}

			try
			{
				state.Instance = Instantiate(state, modAssemblies);
				state.CurrentStage = LifecycleStage.PreLoad;
				state.Instance.OnPreLoad();
				state.PreLoadDone = true;
			}
			catch (Exception ex)
			{
				Fail(state, Unwrap(ex), hardDependents, loaderLogger);
				aborted |= _strict;
			}
		}

		// ---- 配置合并(D11):PreLoad 声明完毕后、OnLoad 之前读入磁盘值 ----
		_configManager?.ApplyAfterPreLoad(_modConfigs.ToList());

		// ---- Load ----
		foreach (var state in states)
		{
			if (aborted)
			{
				// 已完成 PreLoad 者保留其进度,仅记录未继续执行
				if (!state.LoadDone && state.FailedStage is null && !state.Skipped)
				{
					state.Note ??= StrictAbortNote;
				}

				continue;
			}

			if (!state.PreLoadDone || state.Skipped || state.FailedStage is not null)
			{
				continue;
			}

			try
			{
				state.CurrentStage = LifecycleStage.Load;
				state.Instance!.OnLoad();
				state.LoadDone = true;
			}
			catch (Exception ex)
			{
				Fail(state, ex, hardDependents, loaderLogger);
				aborted |= _strict;
			}
		}

		// ---- PostLoad(仅对 PreLoad/Load 成功者;失败不级联) ----
		foreach (var state in states)
		{
			if (aborted)
			{
				// 已成功 Load 者保留成功状态,仅记录 PostLoad 未执行
				if (state.LoadDone && state.FailedStage is null)
				{
					state.Note ??= "strict 中止:PostLoad 未执行(D7 预留开关)";
				}

				continue;
			}

			if (!state.LoadDone || state.FailedStage is not null || state.Skipped)
			{
				continue;
			}

			try
			{
				state.CurrentStage = LifecycleStage.PostLoad;
				state.Instance!.OnPostLoad();
				state.PostLoadExecuted = true;
			}
			catch (Exception ex)
			{
				state.FailedStage = LifecycleStage.PostLoad;
				state.Exception = ex;
				loaderLogger.Warn(
					$"模块 \"{state.Item.ModuleUid}\" PostLoad 失败(不影响其他模块): {ex.Message}");
			}
		}

		stopwatch.Stop();

		return new LifecycleReport
		{
			Results = states.Select(s => s.ToResult()).ToArray(),
			StrictAborted = aborted,
			Duration = stopwatch.Elapsed,
		};
	}

	private static Dictionary<string, List<RunState>> BuildHardDependents(RunState[] states)
	{
		var byUid = states.ToDictionary(s => s.Item.ModuleUid, StringComparer.Ordinal);
		var dependents = new Dictionary<string, List<RunState>>(StringComparer.Ordinal);

		foreach (var state in states)
		{
			foreach (var dependency in state.Item.Module.Depends.Where(d => !d.Soft))
			{
				// 依赖已在规划期校验存活;仅当依赖确在本次计划内时建立反向边
				if (!byUid.ContainsKey(dependency.Uid))
				{
					continue;
				}

				if (!dependents.TryGetValue(dependency.Uid, out var list))
				{
					dependents[dependency.Uid] = list = new List<RunState>();
				}

				list.Add(state);
			}
		}

		return dependents;
	}

	private LmModule Instantiate(RunState state, Dictionary<string, Assembly> modAssemblies)
	{
		state.CurrentStage = LifecycleStage.AssemblyLoad;
		if (!modAssemblies.TryGetValue(state.Item.ModUid, out var assembly))
		{
			var dllPath = Path.Combine(state.Item.Mod.Directory, state.Item.Mod.EntryAssembly);
			assembly = _assemblyLoader.LoadModAssembly(state.Item.ModUid, dllPath);
			modAssemblies[state.Item.ModUid] = assembly;
		}

		state.CurrentStage = LifecycleStage.Instantiation;
		var type = assembly.GetType(state.Item.Module.Type, throwOnError: false);
		if (type is null)
		{
			throw new MissingMemberException(
				$"程序集 \"{state.Item.Mod.EntryAssembly}\" 中未找到类型 \"{state.Item.Module.Type}\"" +
				"(须为完整命名空间全名)");
		}

		if (!typeof(LmModule).IsAssignableFrom(type))
		{
			throw new InvalidCastException($"类型 \"{state.Item.Module.Type}\" 未继承 LMLoader.Api.LmModule");
		}

		var instance = (LmModule)(Activator.CreateInstance(type) ?? throw new MissingMemberException(
			$"类型 \"{state.Item.Module.Type}\" 缺少公共无参构造函数"));

		state.Config = GetOrCreateModConfig(state.Item.ModUid);
		instance.Attach(new LmModuleContext(
			state.Item.ModuleUid,
			state.Item.ModUid,
			state.Item.Mod.Directory,
			_loggerRouter.GetLogger(state.Item.ModUid),
			_serviceRegistry,
			state.Config));

		return instance;
	}

	private Api.Config.ModConfig GetOrCreateModConfig(string modUid)
	{
		if (!_modConfigs.TryGetValue(modUid, out var config))
		{
			config = new Api.Config.ModConfig();
			_modConfigs[modUid] = config;
			_configManager?.Register(modUid, config);
		}

		return config;
	}

	private void Fail(
		RunState state,
		Exception exception,
		Dictionary<string, List<RunState>> hardDependents,
		ILmLogger loaderLogger)
	{
		state.FailedStage = state.CurrentStage;
		state.Exception = exception;
		state.Instance = null;
		loaderLogger.Error($"模块 \"{state.Item.ModuleUid}\" 在 {state.CurrentStage} 阶段失败: {exception.Message}", exception);

		// 运行期失败级联:跳过硬依赖它的、尚未完成的模块(跳过不回退已完成的阶段)
		var pending = new Queue<string>();
		pending.Enqueue(state.Item.ModuleUid);
		while (pending.Count > 0)
		{
			var failedUid = pending.Dequeue();
			if (!hardDependents.TryGetValue(failedUid, out var dependents))
			{
				continue;
			}

			foreach (var dependent in dependents)
			{
				if (dependent.Skipped || dependent.FailedStage is not null)
				{
					continue;
				}

				MarkSkipped(dependent, $"硬依赖 \"{failedUid}\" 加载失败,级联跳过");
				pending.Enqueue(dependent.Item.ModuleUid);
			}
		}
	}

	private static void MarkSkipped(RunState state, string note)
	{
		state.Skipped = true;
		state.Note = note;
	}

	private static Exception Unwrap(Exception exception) =>
		exception is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : exception;

	private sealed class RunState
	{
		public RunState(ModulePlanItem item)
		{
			Item = item;
		}

		public ModulePlanItem Item { get; }

		public LmModule? Instance { get; set; }

		/// <summary>模块配置声明面(D11);实例化时创建,4.3 起在 PreLoad 后合并磁盘值。</summary>
		public Api.Config.ModConfig? Config { get; set; }

		public LifecycleStage CurrentStage { get; set; } = LifecycleStage.AssemblyLoad;

		public bool PreLoadDone { get; set; }

		public bool LoadDone { get; set; }

		public bool PostLoadExecuted { get; set; }

		public LifecycleStage? FailedStage { get; set; }

		public Exception? Exception { get; set; }

		public bool Skipped { get; set; }

		public string? Note { get; set; }

		public ModuleRunResult ToResult() => new()
		{
			ModuleUid = Item.ModuleUid,
			ModUid = Item.ModUid,
			Succeeded = PreLoadDone && LoadDone && FailedStage is null,
			Skipped = Skipped,
			PostLoadExecuted = PostLoadExecuted,
			FailedStage = FailedStage,
			Exception = Exception,
			Note = Note,
			Instance = Instance,
		};
	}
}
