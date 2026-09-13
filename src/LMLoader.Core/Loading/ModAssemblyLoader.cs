// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Reflection;
using System.Runtime.Loader;
using LMLoader.Api;

namespace LMLoader.Core.Loading;

/// <summary>
/// 模组程序集加载器(决策 D1):全部模组装进<b>单一共享非收集式</b> AssemblyLoadContext;
/// 公共库(HarmonyX/Tomlyn/LMLoader.Api)由 loader 统一供给,模组不得捆绑。
/// 程序集解析回退顺序:共享注册表 → 游戏程序集 → 模组目录 → 默认上下文。
/// (P0-1 实证:Godot 将游戏程序集装在自身 ALC,默认上下文按名不可达,须由游戏解析器显式返回。)
/// </summary>
public sealed class ModAssemblyLoader : IDisposable
{
	private const string LoaderOrigin = "<loader>";

	private readonly ModLoadContext _context;
	private readonly ILmLogger _logger;
	private readonly Func<AssemblyName, Assembly?>? _gameAssemblyResolver;
	private readonly object _gate = new();

	/// <summary>loader 供给的公共库:简单名 → 程序集。</summary>
	private readonly Dictionary<string, Assembly> _sharedRegistry = new(StringComparer.Ordinal);

	/// <summary>模组目录解析器,按模组加载顺序排列(先载入者胜)。</summary>
	private readonly List<(string ModUid, AssemblyDependencyResolver Resolver, string ModDirectory)> _modResolvers = new();

	/// <summary>已加载程序集来源:简单名 → (模组, 路径)。</summary>
	private readonly Dictionary<string, (string ModUid, string Path)> _origins = new(StringComparer.Ordinal);

	public ModAssemblyLoader(
		ILmLogger logger,
		IEnumerable<Assembly>? sharedLibraries = null,
		Func<AssemblyName, Assembly?>? gameAssemblyResolver = null)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_gameAssemblyResolver = gameAssemblyResolver;
		_context = new ModLoadContext(this);

		if (sharedLibraries is not null)
		{
			foreach (var assembly in sharedLibraries)
			{
				RegisterSharedLibrary(assembly);
			}
		}
	}

	/// <summary>登记 loader 供给的公共库(D1:模组捆绑同名库将被忽略并告警)。</summary>
	public void RegisterSharedLibrary(Assembly assembly)
	{
		ArgumentNullException.ThrowIfNull(assembly);

		var simpleName = assembly.GetName().Name;
		if (string.IsNullOrEmpty(simpleName))
		{
			return;
		}

		lock (_gate)
		{
			// 同名库重复登记:保持先登记者(D1 先载入者胜的一致语义)
			if (!_sharedRegistry.TryAdd(simpleName, assembly))
			{
				_logger.Warn($"公共库 \"{simpleName}\" 重复登记,保留先登记版本");
			}
		}
	}

	/// <summary>
	/// 加载模组主程序集进共享上下文。同一模组只应加载一次;重复加载同路径无副作用。
	/// </summary>
	public Assembly LoadModAssembly(string modUid, string modDllPath)
	{
		if (string.IsNullOrEmpty(modUid))
		{
			throw new ArgumentException("modUid 不能为空", nameof(modUid));
		}

		var fullPath = Path.GetFullPath(modDllPath);
		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException($"模组程序集不存在: {fullPath}", fullPath);
		}

		lock (_gate)
		{
			// 注册顺序 = 加载顺序,决定后续依赖解析的"先载入者胜"
			_modResolvers.Add((modUid, new AssemblyDependencyResolver(fullPath), Path.GetDirectoryName(fullPath) ?? ""));

			WarnDuplicateBundledAssemblies(modUid, fullPath);

			var simpleName = AssemblyName.GetAssemblyName(fullPath).Name;
			if (!string.IsNullOrEmpty(simpleName))
			{
				_origins.TryAdd(simpleName, (modUid, fullPath));
			}

			return _context.LoadFromAssemblyPath(fullPath);
		}
	}

	/// <summary>诊断:程序集简单名 → 来源描述(模组/路径)。测试与排错用。</summary>
	internal IReadOnlyDictionary<string, (string ModUid, string Path)> SnapshotOrigins()
	{
		lock (_gate)
		{
			return new Dictionary<string, (string, string)>(_origins, _origins.Comparer);
		}
	}

	public void Dispose()
	{
		// 非收集式上下文无需卸载;实现 IDisposable 仅为语义完整与未来扩展
	}

	/// <summary>
	/// 模组目录重名捆绑扫描(D1):与公共库重名 → 告警(loader 版本胜);
	/// 与先前模组捆绑库重名 → 告警(先载入者胜)。扫描到的捆绑库同时登记来源,
	/// 使后续模组加载时的检测不依赖前一模组是否已实际触发该库的 JIT 加载。
	/// </summary>
	private void WarnDuplicateBundledAssemblies(string modUid, string modDllPath)
	{
		var modDirectory = Path.GetDirectoryName(modDllPath);
		if (modDirectory is null || !Directory.Exists(modDirectory))
		{
			return;
		}

		foreach (var dll in Directory.EnumerateFiles(modDirectory, "*.dll", SearchOption.TopDirectoryOnly))
		{
			if (string.Equals(Path.GetFullPath(dll), modDllPath, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var simpleName = Path.GetFileNameWithoutExtension(dll);
			if (_sharedRegistry.ContainsKey(simpleName))
			{
				_logger.Warn(
					$"模组 \"{modUid}\" 捆绑了公共库 \"{simpleName}\",loader 将统一供给该库,捆绑副本被忽略(D1);" +
					"请从模组依赖中移除 loader 公共库");
				continue;
			}

			if (_origins.TryGetValue(simpleName, out var origin) && origin.ModUid != modUid)
			{
				_logger.Warn(
					$"程序集 \"{simpleName}\" 已由模组 \"{origin.ModUid}\" 先行提供," +
					$"模组 \"{modUid}\" 的捆绑副本将被忽略(先载入者胜,D1);涉事模组: {origin.ModUid}, {modUid}");
				continue;
			}

			_origins.TryAdd(simpleName, (modUid, dll));
		}
	}

	private Assembly? Resolve(AssemblyName assemblyName)
	{
		var simpleName = assemblyName.Name;
		if (string.IsNullOrEmpty(simpleName))
		{
			return null;
		}

		// 1) loader 公共库
		if (_sharedRegistry.TryGetValue(simpleName, out var shared))
		{
			return shared;
		}

		// 2) 游戏程序集(宿主环境注入的解析器;P0-1:默认上下文按名不可达)
		if (_gameAssemblyResolver?.Invoke(assemblyName) is { } gameAssembly)
		{
			return gameAssembly;
		}

		// 3) 模组目录:先 deps.json 解析,缺失时直探 {name}.dll(降低模组作者打包门槛)
		lock (_gate)
		{
			foreach (var (modUid, resolver, modDirectory) in _modResolvers)
			{
				var path = resolver.ResolveAssemblyToPath(assemblyName) ?? ProbeModDirectory(modDirectory, simpleName);
				if (path is null)
				{
					continue;
				}

				_origins.TryAdd(simpleName, (modUid, path));
				return _context.LoadFromAssemblyPath(path);
			}
		}

		// 4) 默认上下文
		return null;
	}

	private static string? ProbeModDirectory(string modDirectory, string simpleName)
	{
		if (modDirectory.Length == 0)
		{
			return null;
		}

		var candidate = Path.Combine(modDirectory, simpleName + ".dll");
		return File.Exists(candidate) ? candidate : null;
	}

	private sealed class ModLoadContext(ModAssemblyLoader owner) : AssemblyLoadContext("LMLoader-Mods", isCollectible: false)
	{
		protected override Assembly? Load(AssemblyName assemblyName) => owner.Resolve(assemblyName);
	}
}
