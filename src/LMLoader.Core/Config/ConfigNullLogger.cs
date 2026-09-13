// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Api;

namespace LMLoader.Core.Config;

/// <summary>配置子系统内部空日志(未提供 logger 时的默认,全部静默)。</summary>
internal sealed class ConfigNullLogger : ILmLogger
{
	public static readonly ConfigNullLogger Instance = new();

	public void Log(LmLogLevel level, string message, Exception? exception = null) { }
}
