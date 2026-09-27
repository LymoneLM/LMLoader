using System.Reflection;
using LMLoader.Api;

namespace LMLoader.Core.Tests.Api;

public class LmModContractTests
{
	private sealed class NullLogger : ILmLogger
	{
		public List<(LmLogLevel Level, string Message)> Calls { get; } = new();

		public void Log(LmLogLevel level, string message, Exception? exception = null) =>
			Calls.Add((level, message));
	}

	[LmMod("com.test.mod")]
	private sealed class EmptyMod : LmMod { }

	private static LmModContext NewContext(ILmLogger? logger = null) =>
		new("com.test.mod", @"C:\mods\test", logger ?? new NullLogger());

	[Fact]
	public void Context_未注入时访问抛出()
	{
		var mod = new EmptyMod();
		Assert.Throws<InvalidOperationException>(() => _ = mod.Context);
	}

	[Fact]
	public void Attach_注入后可读Uid与Logger_重复注入抛出()
	{
		var mod = new EmptyMod();
		var context = NewContext();
		mod.Attach(context);

		Assert.Equal("com.test.mod", mod.Uid);
		Assert.Same(context, mod.Context);
		Assert.Throws<InvalidOperationException>(() => mod.Attach(NewContext()));
	}

	[Fact]
	public void 默认生命周期钩子为空操作可直接调用()
	{
		var mod = new EmptyMod();
		mod.Attach(NewContext());

		mod.OnPreLoad();
		mod.OnLoad();
		mod.OnPostLoad();
	}

	[Fact]
	public void 特性uid可反射读取()
	{
		var attribute = typeof(EmptyMod).GetCustomAttribute<LmModAttribute>(inherit: false);

		Assert.NotNull(attribute);
		Assert.Equal("com.test.mod", attribute!.Uid);
	}

	[Fact]
	public void ILmLogger_默认接口方法路由到Log()
	{
		ILmLogger logger = new NullLogger();

		logger.Trace("t");
		logger.Debug("d");
		logger.Info("i");
		logger.Warn("w");
		logger.Error("e");

		Assert.Equal(
			[LmLogLevel.Trace, LmLogLevel.Debug, LmLogLevel.Info, LmLogLevel.Warning, LmLogLevel.Error],
			((NullLogger)logger).Calls.Select(c => c.Level));
	}
}
