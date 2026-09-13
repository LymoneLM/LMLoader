using LMLoader.Api;
using LMLoader.Core.Logging;

namespace LMLoader.Core.Tests.Logging;

[Trait("Category", "UsesFileSystem")]
public class FileLogSinkTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmloader-log-{Guid.NewGuid():N}");

	private readonly LogEvent _event;

	public FileLogSinkTests()
	{
		Directory.CreateDirectory(_root);
		// 用本地偏移构造,使 LocalDateTime 恰为 12:30:45.123(跨时区稳定)
		var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 14));
		_event = new LogEvent(
			new DateTimeOffset(2026, 9, 14, 12, 30, 45, 123, localOffset),
			LmLogLevel.Warning, "com.a.mod", "something odd",
			new InvalidOperationException("boom"));
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private string PathOf(string name) => Path.Combine(_root, name);

	[Fact]
	public void 追加模式_写入含完整日期级别模块消息与异常()
	{
		var path = PathOf("LMLoader.log");
		using var sink = new FileLogSink(path);

		sink.Emit(_event);
		sink.Dispose();

		var text = File.ReadAllText(path);
		Assert.Contains("[2026-09-14 12:30:45.123] [WARNING] [com.a.mod] something odd", text);
		Assert.Contains("InvalidOperationException: boom", text);
		Assert.Equal(1, sink.EmittedCount);
	}

	[Fact]
	public void 追加模式_多次写入保留历史()
	{
		var path = PathOf("LMLoader.log");
		using var sink = new FileLogSink(path);

		sink.Emit(_event with { Message = "first" });
		sink.Emit(_event with { Message = "second" });
		sink.Dispose();

		var text = File.ReadAllText(path);
		Assert.Contains("first", text);
		Assert.Contains("second", text);
	}

	[Fact]
	public void 截断模式_第二次会话不保留旧内容()
	{
		var path = PathOf("LMLoader.log");
		using (var first = new FileLogSink(path, append: false))
		{
			first.Emit(_event with { Message = "old-session" });
		}

		using (var fresh = new FileLogSink(path, append: false))
		{
			fresh.Emit(_event with { Message = "new-session" });
		}

		var text = File.ReadAllText(path);
		Assert.DoesNotContain("old-session", text);
		Assert.Contains("new-session", text);
	}

	[Fact]
	public void 目录不存在自动创建()
	{
		using var sink = new FileLogSink(PathOf(Path.Combine("deep", "dir", "LMLoader.log")));

		sink.Emit(_event);

		Assert.True(File.Exists(PathOf(Path.Combine("deep", "dir", "LMLoader.log"))));
	}

	[Fact]
	public void 释放后事件静默丢弃()
	{
		var path = PathOf("LMLoader.log");
		var sink = new FileLogSink(path);
		sink.Emit(_event with { Message = "kept" });
		sink.Dispose();

		sink.Emit(_event with { Message = "dropped-after-dispose" });

		Assert.Equal(1, sink.EmittedCount);
	}

	[Fact]
	public void IO失败不放大_恢复后继续写入()
	{
		var path = PathOf("LMLoader.log");
		var sink = new FileLogSink(path);

		// Windows 下独占句柄会迫使写入失败并被丢弃(不抛);Linux 共享规则不同可能成功——
		// 两种行为都只要求 sink 不向上抛异常
		using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
		{
			sink.Emit(_event with { Message = "during-lock" });
		}

		sink.Emit(_event with { Message = "after-lock" });
		sink.Dispose();

		Assert.True(sink.EmittedCount is 1 or 2);
		Assert.Contains("after-lock", File.ReadAllText(path));
	}

	[Fact]
	public void 多线程写入不丢失()
	{
		var path = PathOf("LMLoader.log");
		using var sink = new FileLogSink(path);

		Parallel.For(0, 100, i => sink.Emit(_event with { Message = $"line-{i}", Exception = null }));
		var emitted = sink.EmittedCount;
		sink.Dispose();

		Assert.Equal(100, emitted);
		Assert.Equal(100, File.ReadAllLines(path).Length);
	}
}

public class RingBufferLogSinkTests
{
	private static LogEvent Make(int i) =>
		new(DateTimeOffset.UtcNow, LmLogLevel.Info, "com.a.mod", $"m{i}", null);

	[Fact]
	public void 未满容量_全量按序返回()
	{
		var sink = new RingBufferLogSink(4);
		for (var i = 0; i < 3; i++)
		{
			sink.Emit(Make(i));
		}

		Assert.Equal(["m0", "m1", "m2"], sink.Snapshot().Select(e => e.Message));
	}

	[Fact]
	public void 超容量_丢弃最旧_保持时序()
	{
		var sink = new RingBufferLogSink(3);
		for (var i = 0; i < 5; i++)
		{
			sink.Emit(Make(i));
		}

		Assert.Equal(["m2", "m3", "m4"], sink.Snapshot().Select(e => e.Message));
	}

	[Fact]
	public void 空缓冲快照为空()
	{
		var sink = new RingBufferLogSink(4);

		Assert.Empty(sink.Snapshot());
	}

	[Fact]
	public void 快照为副本_与后续写入解耦()
	{
		var sink = new RingBufferLogSink(4);
		sink.Emit(Make(0));
		var snapshot = sink.Snapshot();

		sink.Emit(Make(1));

		Assert.Single(snapshot);
		Assert.Equal(2, sink.Snapshot().Length);
	}

	[Fact]
	public void 并发写入_事件总数守恒()
	{
		var sink = new RingBufferLogSink(1024);

		Parallel.For(0, 512, i => sink.Emit(Make(i)));

		Assert.Equal(512, sink.Snapshot().Length);
	}

	[Fact]
	public void 容量非法抛错()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new RingBufferLogSink(0));
	}
}
