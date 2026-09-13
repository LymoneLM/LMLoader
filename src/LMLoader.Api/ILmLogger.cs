// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Api;

/// <summary>
/// per-mod 日志接口(D6:日志是 Abstractions 表面的一部分,自研轻量实现,零第三方依赖)。
/// 实现方只需实现 <see cref="Log"/>;便捷方法为默认接口方法。
/// </summary>
public interface ILmLogger
{
	/// <summary>写入一条日志。线程安全性由具体实现保证。</summary>
	void Log(LmLogLevel level, string message, Exception? exception = null);

	void Trace(string message) => Log(LmLogLevel.Trace, message);

	void Debug(string message) => Log(LmLogLevel.Debug, message);

	void Info(string message) => Log(LmLogLevel.Info, message);

	void Warn(string message) => Log(LmLogLevel.Warning, message);

	void Error(string message, Exception? exception = null) => Log(LmLogLevel.Error, message, exception);
}
