// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Reflection;
using LMLoader.Api;
using LMLoader.Api.Config;
using LMLoader.Core.Dependency;
using LMLoader.Core.Loading;
using LMLoader.Core.Logging;

namespace LMLoader.Core.Lifecycle;

/// <summary>
/// 生命周期执行器(宽松失败传播,D18):
/// - 程序集加载与特性扫描先行(按拓扑序全量执行),再按序实例化与三阶段批处理:
///   PreLoad(全部) → Load(全部) → PostLoad(仅 PreLoad/Load 成功且非纯资源者);
///   程序集先行加载使"一 dll 服务多模组"的联合绑定与拓扑序无关;
/// - 绑定:模组声明的 assemblies 扫描出的 <c>[LmMod(uid)]</c> 类型按 uid 全局登记,
///   同 uid 多类型声明 → 绑定歧义报错;声明无类 → 该模组失败;类无声明 → 孤立警告;
/// - 无 assemblies 的纯资源/依赖锚模组无实例、无钩子,直接成功;
/// - 全部 hook 边界 try/catch,单模组异常不杀游戏、不阻塞无依赖模组;
/// - 运行期失败级联跳过尚未执行到对应阶段的硬依赖模组(跳过不回退已完成的阶段);
/// - strict 开关预留:任一失败即中止,v1 默认宽松。
/// </summary>
public sealed class LifecycleRunner
{
	/// <summary>loader 自身日志的 uid 标识(日志按模组划分,loader 视为特殊模组)。</summary>
	public const string LoaderLogUid = "LMLoader";

	private const string StrictAbortNote = "strict 模式:前序模组失败,中止加载";

	private readonly ModAssemblyLoader _assemblyLoader;
	private readonly LoggerRouter _loggerRouter;
	private readonly ServiceRegistry? _serviceRegistry;
	private readonly bool _strict;
	private readonly Config.ConfigManager? _configManager;
	private readonly Dictionary<string, Api.Config.ModConfig> _modConfigs = new(StringComparer.Ordinal);

	/// <summary>特性扫描登记:特性 uid → 候选类型(同 uid 多类型 = 绑定歧义,实例化时报错)。</summary>
	private readonly Dictionary<string, List<Type>> _typesByUid = new(StringComparer.Ordinal);

	/// <summary>特性扫描警告(非致命;孤立类/未标注/标注未继承/不可实例化)。</summary>
	private readonly List<string> _bindingWarnings = new();

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
		var loadedAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
		// 声明过的 uid 全集(含规划期跳过者):孤立类判定以"是否被任何清单声明"为准
		var declaredUids = plan.Ordered.Select(i => i.Uid)
			.Concat(plan.Skipped.Select(s => s.Uid))
			.ToHashSet(StringComparer.Ordinal);
		var aborted = false;

		// ---- 程序集加载 + 特性扫描(先行,全量按拓扑序) ----
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

			try
			{
				LoadAssembliesAndScan(state, loadedAssemblies);
			}
			catch (Exception ex)
			{
				Fail(state, Unwrap(ex), hardDependents, loaderLogger);
				aborted |= _strict;
			}
		}

		// ---- PreLoad:实例化 + OnPreLoad ----
		foreach (var state in states)
		{
			if (aborted)
			{
				if (state.FailedStage is null && !state.Skipped && !state.ResourceOnly)
				{
					MarkSkipped(state, StrictAbortNote);
				}

				continue;
			}

			if (state.Skipped || state.FailedStage is not null)
			{
				continue;
			}

			if (state.ResourceOnly)
			{
				continue; // 纯资源模组:无实例无钩子,直接成功
			}

			try
			{
				state.Instance = Instantiate(state);
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

		// ---- 配置合并:PreLoad 声明完毕后、OnLoad 之前读入磁盘值 ----
		_configManager?.ApplyAfterPreLoad(_modConfigs.ToList());

		// ---- Load ----
		foreach (var state in states)
		{
			if (aborted)
			{
				// 已完成 PreLoad 者保留其进度,仅记录未继续执行
				if (!state.LoadDone && state.FailedStage is null && !state.Skipped && !state.ResourceOnly)
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

		// ---- PostLoad(仅对 PreLoad/Load 成功且非纯资源者;失败不级联) ----
		foreach (var state in states)
		{
			if (aborted)
			{
				// 已成功 Load 者保留成功状态,仅记录 PostLoad 未执行
				if (state.LoadDone && state.FailedStage is null)
				{
					state.Note ??= "strict 中止:PostLoad 未执行";
				}

				continue;
			}

			if (!state.LoadDone || state.FailedStage is not null || state.Skipped || state.ResourceOnly)
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
					$"模组 \"{state.Item.Uid}\" PostLoad 失败(不影响其他模组): {ex.Message}");
			}
		}

		// ---- 孤立类警告:特性 uid 未被本次加载的任何清单声明 ----
		foreach (var (uid, types) in _typesByUid)
		{
			if (declaredUids.Contains(uid))
			{
				continue;
			}

			foreach (var type in types)
			{
				var message = $"扫描到特性类 \"{type.FullName}\" 声明的 uid \"{uid}\" 未被本次加载的清单声明,已忽略" +
					"(请在 mod.json mods[] 中声明该 uid,或移除 [LmMod] 特性)";
				_bindingWarnings.Add(message);
				loaderLogger.Warn(message);
			}
		}

		stopwatch.Stop();

		return new LifecycleReport
		{
			Results = states.Select(s => s.ToResult()).ToArray(),
			StrictAborted = aborted,
			Duration = stopwatch.Elapsed,
			BindingWarnings = _bindingWarnings,
		};
	}

	private static Dictionary<string, List<RunState>> BuildHardDependents(RunState[] states)
	{
		var byUid = states.ToDictionary(s => s.Item.Uid, StringComparer.Ordinal);
		var dependents = new Dictionary<string, List<RunState>>(StringComparer.Ordinal);

		foreach (var state in states)
		{
			foreach (var dependency in state.Item.Entry.Depends.Where(d => !d.Soft))
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

	/// <summary>
	/// 加载模组声明的全部程序集并扫描特性类(AssemblyLoad 阶段)。
	/// 同一路径跨模组重复声明只加载一次;扫描为全局联合登记,类可位于任一模组声明的程序集。
	/// </summary>
	private void LoadAssembliesAndScan(RunState state, Dictionary<string, Assembly> loadedAssemblies)
	{
		state.CurrentStage = LifecycleStage.AssemblyLoad;

		if (state.Item.Entry.Assemblies.Count == 0)
		{
			state.ResourceOnly = true;
			state.Note = "纯资源模组(无代码入口)";
			return;
		}

		foreach (var assemblyName in state.Item.Entry.Assemblies)
		{
			var fullPath = Path.GetFullPath(Path.Combine(state.Item.Manifest.Directory, assemblyName));
			if (loadedAssemblies.TryGetValue(fullPath, out _))
			{
				continue; // 同路径已加载(含特性扫描),联合绑定共享
			}

			var assembly = _assemblyLoader.LoadModAssembly(state.Item.Uid, fullPath);
			loadedAssemblies[fullPath] = assembly;
			ScanAssembly(assembly);
		}
	}

	/// <summary>
	/// 扫描程序集中带 <see cref="LmModAttribute"/> 的类型并按 uid 登记。
	/// 继承未标注、标注未继承、abstract/非 public/泛型定义 → 告警忽略;类型部分加载失败逐条告警。
	/// </summary>
	private void ScanAssembly(Assembly assembly)
	{
		Type[] types;
		try
		{
			types = assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
			foreach (var loaderException in ex.LoaderExceptions.Where(e => e is not null).Select(e => e!))
			{
				var message = $"程序集 \"{assembly.GetName().Name}\" 部分类型加载失败: {loaderException.Message}";
				_bindingWarnings.Add(message);
			}
		}

		foreach (var type in types)
		{
			var attribute = type.GetCustomAttribute<LmModAttribute>(inherit: false);
			var isSubclass = typeof(LmMod).IsAssignableFrom(type);

			if (attribute is null)
			{
				if (isSubclass)
				{
					var message = $"类型 \"{type.FullName}\" 继承 LmMod 但未标注 [LmMod(uid)],已忽略";
					_bindingWarnings.Add(message);
				}

				continue;
			}

			if (!isSubclass)
			{
				var message = $"类型 \"{type.FullName}\" 标注了 [LmMod] 但未继承 LMLoader.Api.LmMod,已忽略";
				_bindingWarnings.Add(message);
				continue;
			}

			if (type.IsAbstract || type.IsGenericTypeDefinition || !type.IsVisible)
			{
				var message = $"类型 \"{type.FullName}\" 标注了 [LmMod] 但不可实例化(须 public、非 abstract、非泛型定义),已忽略";
				_bindingWarnings.Add(message);
				continue;
			}

			if (string.IsNullOrWhiteSpace(attribute.Uid))
			{
				var message = $"类型 \"{type.FullName}\" 的 [LmMod] 特性 uid 为空,已忽略";
				_bindingWarnings.Add(message);
				continue;
			}

			if (!_typesByUid.TryGetValue(attribute.Uid, out var candidates))
			{
				_typesByUid[attribute.Uid] = candidates = new List<Type>();
			}

			candidates.Add(type);
		}
	}

	private LmMod Instantiate(RunState state)
	{
		state.CurrentStage = LifecycleStage.Instantiation;

		if (!_typesByUid.TryGetValue(state.Item.Uid, out var candidates) || candidates.Count == 0)
		{
			throw new MissingMemberException(
				$"未找到与模组 \"{state.Item.Uid}\" 绑定的类型:清单声明了程序集但其中没有标注 " +
				$"[LmMod(\"{state.Item.Uid}\")] 的 public LmMod 子类(扫描过: {string.Join(", ", state.Item.Entry.Assemblies)})");
		}

		if (candidates.Count > 1)
		{
			throw new InvalidOperationException(
				$"模组 \"{state.Item.Uid}\" 绑定歧义:多个类型声明了相同 uid —— " +
				string.Join(", ", candidates.Select(t => $"\"{t.FullName}\"")));
		}

		var type = candidates[0];
		var instance = (LmMod)(Activator.CreateInstance(type) ?? throw new MissingMemberException(
			$"类型 \"{type.FullName}\" 缺少公共无参构造函数"));

		state.Config = GetOrCreateModConfig(state.Item.Uid);
		instance.Attach(new LmModContext(
			state.Item.Uid,
			state.Item.Manifest.Directory,
			_loggerRouter.GetLogger(state.Item.Uid),
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
		loaderLogger.Error($"模组 \"{state.Item.Uid}\" 在 {state.CurrentStage} 阶段失败: {exception.Message}", exception);

		// 运行期失败级联:跳过硬依赖它的、尚未完成的模组(跳过不回退已完成的阶段)
		var pending = new Queue<string>();
		pending.Enqueue(state.Item.Uid);
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
				pending.Enqueue(dependent.Item.Uid);
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
		public RunState(ModPlanItem item)
		{
			Item = item;
		}

		public ModPlanItem Item { get; }

		public LmMod? Instance { get; set; }

		/// <summary>模组配置声明面;实例化时创建,PreLoad 后合并磁盘值。</summary>
		public Api.Config.ModConfig? Config { get; set; }

		/// <summary>纯资源/依赖锚模组(无 assemblies,无实例无钩子)。</summary>
		public bool ResourceOnly { get; set; }

		public LifecycleStage CurrentStage { get; set; } = LifecycleStage.AssemblyLoad;

		public bool PreLoadDone { get; set; }

		public bool LoadDone { get; set; }

		public bool PostLoadExecuted { get; set; }

		public LifecycleStage? FailedStage { get; set; }

		public Exception? Exception { get; set; }

		public bool Skipped { get; set; }

		public string? Note { get; set; }

		public ModRunResult ToResult() => new()
		{
			Uid = Item.Uid,
			Name = Item.Entry.Name,
			SourcePath = Item.Manifest.SourcePath,
			Succeeded = ResourceOnly || (PreLoadDone && LoadDone && FailedStage is null),
			Skipped = Skipped,
			ResourceOnly = ResourceOnly,
			PostLoadExecuted = PostLoadExecuted,
			FailedStage = FailedStage,
			Exception = Exception,
			Note = Note,
			Instance = Instance,
		};
	}
}
