// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Api;

namespace LMLoader.Core.Config;

/// <summary>
/// 配置热重载监听(D11):监视配置根目录的 *.toml,按路径防抖(默认 500ms)后触发
/// <see cref="ConfigManager.ReloadFromFile"/>。
/// FileSystemWatcher 不保证事件送达(MS 文档明示,实测存在丢失/秒级延迟),故叠加
/// 周期性 mtime 兜底扫描(默认 2s)作为第二变更来源,两条路径经同一防抖合并。
/// 回调在后台线程执行,连锁的 ConfigEntry.SettingChanged 亦然——涉及主线程亲和的
/// API(如 Godot 节点)由订阅方自行调度。
/// </summary>
public sealed class ConfigWatcher : IDisposable
{
	private readonly ConfigManager _manager;
	private readonly string _rootPath;
	private readonly ILmLogger _logger;
	private readonly TimeSpan _debounce;
	private readonly object _gate = new();
	private readonly Dictionary<string, Timer> _pending = new(StringComparer.Ordinal);
	private readonly Dictionary<string, DateTime> _lastWriteTimes = new(StringComparer.Ordinal);
	private readonly FileSystemWatcher _watcher;
	private readonly Timer _sweepTimer;
	private bool _disposed;

	/// <summary>防抖窗口内同一路径的多次写入只触发一次重载。</summary>
	public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

	/// <summary>mtime 兜底扫描周期;FSW 事件丢失时的下界保障。</summary>
	public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(2);

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
		_watcher.Changed += (_, e) => Schedule(e.FullPath, "fsw");
		_watcher.Created += (_, e) => Schedule(e.FullPath, "fsw");
		_watcher.Renamed += (_, e) => Schedule(e.FullPath, "fsw");
		_watcher.Error += OnError;
		_watcher.EnableRaisingEvents = true;

		// 兜底扫描基线:当前状态不视为变更
		SnapshotWriteTimes(scheduleChanged: false);
		_sweepTimer = new Timer(OnSweep, null, DefaultSweepInterval, DefaultSweepInterval);
		_logger.Debug($"配置监听已启动: {_watcher.Path} (防抖 {_debounce.TotalMilliseconds}ms,兜底扫描 {DefaultSweepInterval.TotalSeconds}s)");
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_sweepTimer.Dispose();
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

	private void Schedule(string fullPath, string source)
	{
		if (_disposed)
		{
			return;
		}

		_logger.Debug($"配置监听收到变更事件({source}): {fullPath}");
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
				_logger.Debug($"触发配置重载: {modUid}");
				_manager.ReloadFromFile(modUid, config);
			}
			else
			{
				_logger.Debug($"跳过未注册模组的配置变更: {modUid}");
			}
		}
		catch (Exception ex)
		{
			// 单次重载失败不放大(D7):文件仍在,下次变更可重试
			_logger.Warn($"配置热重载失败({fullPath}): {ex.Message}");
		}
	}

	private void OnSweep(object? state)
	{
		if (_disposed)
		{
			return;
		}

		SnapshotWriteTimes(scheduleChanged: true);
	}

	/// <summary>记录当前各 toml 的 mtime;scheduleChanged=true 时对有变化的文件走防抖调度。</summary>
	private void SnapshotWriteTimes(bool scheduleChanged)
	{
		string[] paths;
		try
		{
			paths = Directory.Exists(_rootPath)
				? Directory.EnumerateFiles(_rootPath, "*.toml").ToArray()
				: Array.Empty<string>();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.Warn($"配置目录扫描失败: {ex.Message}");
			return;
		}

		foreach (var path in paths)
		{
			DateTime current;
			try
			{
				current = File.GetLastWriteTimeUtc(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				continue; // 文件可能正被替换,下一轮再看
			}

			bool changed;
			lock (_gate)
			{
				changed = !_lastWriteTimes.TryGetValue(path, out var previous) || current != previous;
				_lastWriteTimes[path] = current;
			}

			if (changed && scheduleChanged)
			{
				Schedule(path, "sweep");
			}
		}
	}

	private void OnError(object? sender, ErrorEventArgs e)
	{
		// 缓冲区溢出等:漏掉的事件由 mtime 兜底扫描补齐
		_logger.Warn("配置目录监听出错,期间发生的变更将由兜底扫描补齐");
	}
}
