// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Collections.Concurrent;
using LMLoader.Api;

namespace LMLoader.Core.Logging;

/// <summary>
/// 日志中枢(D6):持有 sink 集合,按模组发放 <see cref="ILmLogger"/>,统一过滤最低级别。
/// 线程安全:sink 列表快照 + per-mod logger 缓存。
/// </summary>
public sealed class LoggerRouter
{
	private readonly List<ILogSink> _sinks = new();
	private readonly ConcurrentDictionary<string, ModLogger> _loggers = new(StringComparer.Ordinal);
	private readonly object _gate = new();

	/// <summary>低于该级别的事件被丢弃;默认 Info(Loader 自身与模组日志的常规可见线)。</summary>
	public LmLogLevel MinimumLevel { get; set; } = LmLogLevel.Info;

	public LoggerRouter()
	{
	}

	public LoggerRouter(params ILogSink[] sinks)
	{
		ArgumentNullException.ThrowIfNull(sinks);

		foreach (var sink in sinks)
		{
			AddSink(sink);
		}
	}

	public void AddSink(ILogSink sink)
	{
		ArgumentNullException.ThrowIfNull(sink);

		lock (_gate)
		{
			_sinks.Add(sink);
		}
	}

	/// <summary>取得某模组(per-mod)的 logger;同 uid 复用同一实例。</summary>
	public ILmLogger GetLogger(string modUid)
	{
		if (string.IsNullOrEmpty(modUid))
		{
			throw new ArgumentException("modUid 不能为空", nameof(modUid));
		}

		return _loggers.GetOrAdd(modUid, uid => new ModLogger(this, uid));
	}

	internal void Write(string modUid, LmLogLevel level, string message, Exception? exception)
	{
		if (level < MinimumLevel)
		{
			return;
		}

		var @event = new LogEvent(DateTimeOffset.Now, level, modUid, message, exception);

		lock (_gate)
		{
			foreach (var sink in _sinks)
			{
				sink.Emit(@event);
			}
		}
	}

	private sealed class ModLogger(LoggerRouter router, string modUid) : ILmLogger
	{
		public void Log(LmLogLevel level, string message, Exception? exception = null) =>
			router.Write(modUid, level, message, exception);
	}
}
