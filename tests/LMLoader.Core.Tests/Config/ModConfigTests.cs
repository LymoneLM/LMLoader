using System.Reflection;
using LMLoader.Api.Config;

namespace LMLoader.Core.Tests.Config;

public class ModConfigTests
{
	private enum Mode { Off, Normal, Turbo }

	[Fact]
	public void 绑定_元数据完整()
	{
		var config = new ModConfig();

		var entry = config.Bind("patch", "multiplier", 100, "乘数", requiresRestart: true);

		Assert.Equal("patch", entry.Section);
		Assert.Equal("multiplier", entry.Key);
		Assert.Equal("乘数", entry.Description);
		Assert.True(entry.RequiresRestart);
		Assert.Equal(typeof(int), entry.SettingType);
		Assert.Equal(100, entry.BoxedDefaultValue);
		Assert.Equal(100, entry.BoxedValue);
		Assert.Equal(100, entry.Value);
	}

	[Fact]
	public void 重复绑定同键返回同实例_值保持()
	{
		var config = new ModConfig();
		var first = config.Bind("patch", "multiplier", 100);
		first.Value = 42;

		var second = config.Bind("patch", "multiplier", 999);

		Assert.Same(first, second);
		Assert.Equal(42, second.Value);
	}

	[Fact]
	public void 同键不同类型绑定抛错()
	{
		var config = new ModConfig();
		config.Bind("patch", "multiplier", 100);

		Assert.Throws<InvalidOperationException>(() => config.Bind("patch", "multiplier", "x"));
	}

	[Theory]
	[InlineData(typeof(DateTime))]
	[InlineData(typeof(object))]
	public void 不支持的类型绑定抛NotSupportedException(Type t)
	{
		var config = new ModConfig();
		var bind = typeof(ModConfig).GetMethod(nameof(ModConfig.Bind))!.MakeGenericMethod(t);

		var ex = Assert.Throws<TargetInvocationException>(
			() => bind.Invoke(config, ["s", "k", Activator.CreateInstance(t), null, false, null]));

		Assert.IsType<NotSupportedException>(ex.InnerException);
	}

	[Fact]
	public void 顶层裸键_空节名合法()
	{
		var config = new ModConfig();

		var entry = config.Bind("", "enabled", true);

		Assert.Equal("", entry.Section);
	}

	[Fact]
	public void 纯空白节名抛错()
	{
		var config = new ModConfig();

		Assert.Throws<ArgumentException>(() => config.Bind("  ", "k", 1));
	}

	[Fact]
	public void 声明顺序保持稳定()
	{
		var config = new ModConfig();
		config.Bind("a", "k1", 1);
		config.Bind("b", "k2", 2);
		config.Bind("a", "k3", 3);

		Assert.Equal(
			[("a", "k1"), ("b", "k2"), ("a", "k3")],
			config.Entries.Select(e => (e.Section, e.Key)).ToArray());
	}

	[Fact]
	public void 范围约束_赋值越界钳制到边界()
	{
		var config = new ModConfig();
		var entry = config.Bind("patch", "multiplier", 50, acceptableValues: new AcceptableValueRange<int>(0, 100));

		entry.Value = 500;
		Assert.Equal(100, entry.Value);

		entry.Value = -10;
		Assert.Equal(0, entry.Value);
	}

	[Fact]
	public void 范围约束_默认值越界同样钳制()
	{
		var config = new ModConfig();

		var entry = config.Bind("patch", "m", 500, acceptableValues: new AcceptableValueRange<int>(0, 100));

		Assert.Equal(100, entry.Value);
	}

	[Fact]
	public void 范围约束_下界大于上界抛错()
	{
		Assert.Throws<ArgumentException>(() => new AcceptableValueRange<int>(10, 0));
	}

	[Fact]
	public void 列表约束_默认值越界构造即抛()
	{
		Assert.Throws<ArgumentException>(() => new ModConfig().Bind(
			"mode", "m", (Mode)99, acceptableValues: new AcceptableValueList<Mode>(Mode.Off, Mode.Normal)));
	}

	[Fact]
	public void 列表约束_赋值越界抛错()
	{
		var entry = new ModConfig().Bind(
			"mode", "m", Mode.Off, acceptableValues: new AcceptableValueList<Mode>(Mode.Off, Mode.Normal));

		Assert.Throws<ArgumentException>(() => entry.Value = (Mode)99);
	}

	[Fact]
	public void 列表约束_合法赋值生效()
	{
		var config = new ModConfig();
		var entry = config.Bind("mode", "m", Mode.Off, acceptableValues: new AcceptableValueList<Mode>(Mode.Off, Mode.Turbo));

		entry.Value = Mode.Turbo;

		Assert.Equal(Mode.Turbo, entry.Value);
	}

	[Fact]
	public void 约束类型与绑定类型不一致抛错()
	{
		var config = new ModConfig();

		Assert.Throws<ArgumentException>(() => config.Bind(
			"s", "k", 1, acceptableValues: new AcceptableValueRange<long>(0, 10)));
	}

	[Fact]
	public void 赋值变更触发事件_未变更不触发()
	{
		var config = new ModConfig();
		var entry = config.Bind("s", "k", 1);
		var observed = new List<ConfigEntry<int>>();
		entry.SettingChanged += e => observed.Add(e);

		entry.Value = 2;
		entry.Value = 2;

		var single = Assert.Single(observed);
		Assert.Same(entry, single);
	}

	[Fact]
	public void 文件值写入_long转int并触发事件()
	{
		var config = new ModConfig();
		var entry = config.Bind("s", "k", 1);
		var fired = 0;
		entry.SettingChanged += _ => fired++;

		var outcome = entry.TrySetFromRaw(42L);

		Assert.Equal(ConfigSetOutcome.Applied, outcome);
		Assert.Equal(42, entry.Value);
		Assert.Equal(1, fired);
	}

	[Fact]
	public void 文件值写入_同值不重复触发事件()
	{
		var config = new ModConfig();
		var entry = config.Bind("s", "k", 42);
		var fired = 0;
		entry.SettingChanged += _ => fired++;

		Assert.Equal(ConfigSetOutcome.Applied, entry.TrySetFromRaw(42L));
		Assert.Equal(0, fired);
	}

	[Theory]
	[InlineData("abc")]
	[InlineData(null)]
	public void 文件值写入_类型不符保持原值(object? raw)
	{
		var config = new ModConfig();
		var entry = config.Bind("s", "k", 1);

		Assert.Equal(ConfigSetOutcome.TypeMismatch, entry.TrySetFromRaw(raw));
		Assert.Equal(1, entry.Value);
	}

	[Fact]
	public void 文件值写入_字符串数字按不变文化解析()
	{
		var config = new ModConfig();
		var entry = config.Bind("s", "k", 1);

		Assert.Equal(ConfigSetOutcome.Applied, entry.TrySetFromRaw("7"));

		Assert.Equal(7, entry.Value);
	}

	[Fact]
	public void 文件值写入_enum未定义值Mismatch_已定义值生效()
	{
		var config = new ModConfig();
		var entry = config.Bind("s", "k", Mode.Off);

		Assert.Equal(ConfigSetOutcome.TypeMismatch, entry.TrySetFromRaw(99L));
		Assert.Equal(Mode.Off, entry.Value);

		Assert.Equal(ConfigSetOutcome.Applied, entry.TrySetFromRaw(2L));
		Assert.Equal(Mode.Turbo, entry.Value);
	}

	[Fact]
	public void 文件值写入_字符串转enum忽略大小写()
	{
		var config = new ModConfig();
		var entry = config.Bind("s", "k", Mode.Off);

		Assert.Equal(ConfigSetOutcome.Applied, entry.TrySetFromRaw("turbo"));

		Assert.Equal(Mode.Turbo, entry.Value);
	}

	[Fact]
	public void 文件值写入_未定义enum字符串与数字均Mismatch()
	{
		var config = new ModConfig();
		var entry = config.Bind("s", "k", Mode.Off);

		// 回归:数字字符串曾经 Enum.TryParse 穿透未定义检查
		Assert.Equal(ConfigSetOutcome.TypeMismatch, entry.TrySetFromRaw("99"));
		Assert.Equal(ConfigSetOutcome.TypeMismatch, entry.TrySetFromRaw("NoSuchMode"));
		Assert.Equal(Mode.Off, entry.Value);
	}

	[Fact]
	public void 文件值写入_跨类型标量转换()
	{
		var config = new ModConfig();
		var f = config.Bind("s", "f", 0.5f);
		var b = config.Bind("s", "b", false);
		var text = config.Bind("s", "text", "x");

		Assert.Equal(ConfigSetOutcome.Applied, f.TrySetFromRaw(1.25));
		Assert.Equal(1.25f, f.Value);

		Assert.Equal(ConfigSetOutcome.Applied, b.TrySetFromRaw(1L));
		Assert.True(b.Value);

		Assert.Equal(ConfigSetOutcome.Applied, text.TrySetFromRaw(true));
		Assert.Equal("True", text.Value);
	}

	[Fact]
	public void 文件值写入_越界范围钳制()
	{
		var config = new ModConfig();
		var entry = config.Bind("s", "k", 50, acceptableValues: new AcceptableValueRange<int>(0, 100));

		Assert.Equal(ConfigSetOutcome.Applied, entry.TrySetFromRaw(500L));

		Assert.Equal(100, entry.Value);
	}

	[Fact]
	public void 文件值写入_列表外值Mismatch()
	{
		var config = new ModConfig();
		var entry = config.Bind("s", "k", Mode.Off, acceptableValues: new AcceptableValueList<Mode>(Mode.Off, Mode.Normal));

		Assert.Equal(ConfigSetOutcome.TypeMismatch, entry.TrySetFromRaw(2L));

		Assert.Equal(Mode.Off, entry.Value);
	}

	[Fact]
	public void 回退默认值()
	{
		var config = new ModConfig();
		var entry = config.Bind("s", "k", 1);
		entry.TrySetFromRaw(9L);

		entry.ResetToDefault();

		Assert.Equal(1, entry.Value);
	}

	[Fact]
	public void 按节键定位条目_Core合并路径()
	{
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 1);
		config.Bind("", "top", 2);

		Assert.True(config.TryGetEntry("patch", "m", out var found));
		Assert.Same(entry, found);
		Assert.True(config.TryGetEntry("", "top", out var top));
		Assert.Equal(2, Assert.IsType<ConfigEntry<int>>(top).Value);
		Assert.False(config.TryGetEntry("patch", "missing", out var none));
		Assert.Null(none);
	}
}
