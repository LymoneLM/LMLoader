// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Api;

namespace LMLoader.Core.Config;

/// <summary>
/// 配置热重载监听(D11):监视配置根目录的 *.toml,按路径防抖(默认 500ms)后触发
/// <see cref="ConfigManager.ReloadFromFile"/>。回调在监视线程上执行,连锁的
/// ConfigEntry.SettingChanged 亦然——涉及主线程亲和的 API(如 Godot 节点)由订阅方自行调度。
/// </summary>
public sealed class ConfigWatcher : IDisposable
{
	private readonly ConfigManager _manager;
	private readonly string _rootPath;
	private readonly ILmLogger _logger;
	private readonly TimeSpan _debounce;
	private readonly object _gate = new();
	private readonly Dictionary<string, Timer> _pending = new(StringComparer.Ordinal);
	private readonly FileSystemWatcher _watcher;
	private bool _disposed;

	/// <summary>防抖窗口内同一路径的多次写入只触发一次重载。</summary>
	public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

	public ConfigWatcher(
		ConfigManager manager,
		string rootPath,
		ILmLogger? logger = null,
		TimeSpan? debounce = null)
	{
		_manager = manager ?? throw new ArgumentNullException(nameof(manager));
		_rootPath = rootPath;
		_logger = logger ?? ConfigNullLogger.Instance;
		_debounce = debounce ?? DefaultDebounce;
		if (_debounce <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(debounce), "防抖窗口必须为正");
		}

		// 配置目录可能尚不存在(首次运行写回时才创建);监听前补齐
		Directory.CreateDirectory(rootPath);
		_watcher = new FileSystemWatcher(rootPath, "*.toml")
		{
			NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
			IncludeSubdirectories = false,
			InternalBufferSize = 8192,
		};
		_watcher.Changed += (_, e) => Schedule(e.FullPath);
		_watcher.Created += (_, e) => Schedule(e.FullPath);
		_watcher.Renamed += (_, e) => Schedule(e.FullPath);
		_watcher.Error += OnError;
		_watcher.EnableRaisingEvents = true;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_watcher.Dispose();
		lock (_gate)
		{
			foreach (var timer in _pending.Values)
			{
				timer.Dispose();
			}

			_pending.Clear();
		}
	}

	private void Schedule(string fullPath)
	{
		if (_disposed)
		{
			return;
		}

		lock (_gate)
		{
			if (_pending.TryGetValue(fullPath, out var existing))
			{
				existing.Change(_debounce, Timeout.InfiniteTimeSpan);
				return;
			}

			_pending[fullPath] = new Timer(OnDebounceElapsed, fullPath, _debounce, Timeout.InfiniteTimeSpan);
		}
	}

	private void OnDebounceElapsed(object? state)
	{
		var fullPath = (string)state!;
		lock (_gate)
		{
			if (_pending.Remove(fullPath, out var timer))
			{
				timer.Dispose();
			}
		}

		if (_disposed)
		{
			return;
		}

		try
		{
			var modUid = Path.GetFileNameWithoutExtension(fullPath);
			if (_manager.TryGetConfig(modUid, out var config) && config is not null)
			{
				_manager.ReloadFromFile(modUid, config);
			}
		}
		catch (Exception ex)
		{
			// 单次重载失败不放大(D7):文件仍在,下次变更可重试
			_logger.Warn($"配置热重载失败({fullPath}): {ex.Message}");
		}
	}

	private void OnError(object? sender, ErrorEventArgs e)
	{
		// 缓冲区溢出等:漏掉的事件无法逐一重放,提示宿主可全量重载
		_logger.Warn("配置目录监听出错,期间发生的变更可能被遗漏(下次变更将正常触发)");
	}
}
