// SPDX-License-Identifier: LGPL-3.0-or-later

using Godot;
using LMLoader.Api;
using LMLoader.Core.Logging;

namespace LMLoader.UI;

/// <summary>
/// 日志窗口(可选组件):两个页签——「日志」轮询展示 <see cref="RingBufferLogSink"/>
/// 最近事件并支持按最低级别过滤(分级查看);「加载报告」展示加载汇总文本。
/// 类型经代码 <c>new</c> 创建(不注册 Godot 脚本),宿主决定何时打开/切换。
/// </summary>
public sealed class LmLogWindow : Control
{
	private const float PollIntervalSeconds = 0.25f;

	private readonly RingBufferLogSink _source;
	private readonly RichTextLabel _logLabel;
	private readonly RichTextLabel _summaryLabel;
	private readonly Button _levelButton;
	private double _pollCooldown;
	private (int Length, LmLogLevel Level, long Stamp, string Message) _lastRender;

	public LmLogWindow(RingBufferLogSink source, LmLogLevel minimumLevel = LmLogLevel.Debug)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		MinimumLevel = minimumLevel;

		// 全屏半透明浮层;宿主可用 Visible 切换
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;

		var backdrop = new Panel();
		backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(backdrop);

		var layout = new VBoxContainer();
		layout.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(layout);

		var header = new HBoxContainer();
		layout.AddChild(header);

		_levelButton = new Button { Text = LevelButtonText() };
		_levelButton.Pressed += CycleLevel;
		header.AddChild(_levelButton);

		var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		layout.AddChild(tabs);

		_logLabel = new RichTextLabel { BbcodeEnabled = false, ScrollFollowing = true };
		_logLabel.Name = "日志";
		tabs.AddChild(_logLabel);

		_summaryLabel = new RichTextLabel { BbcodeEnabled = false };
		_summaryLabel.Name = "加载报告";
		tabs.AddChild(_summaryLabel);
	}

	/// <summary>当前过滤最低级别(低于该级别的事件不显示)。</summary>
	public LmLogLevel MinimumLevel { get; private set; }

	/// <summary>日志页当前渲染文本(冒烟/测试断言用)。</summary>
	public string LogText => _logLabel.Text;

	/// <summary>加载报告页文本。</summary>
	public string SummaryText => _summaryLabel.Text;

	/// <summary>设置加载汇总报告(通常传 LoadResult.SummaryText)。</summary>
	public void ShowLoadSummary(string summaryText) => _summaryLabel.Text = summaryText;

	public void SetMinimumLevel(LmLogLevel level)
	{
		MinimumLevel = level;
		_levelButton.Text = LevelButtonText();
		Refresh();
	}

	public override void _Process(double delta)
	{
		_pollCooldown -= delta;
		if (_pollCooldown > 0)
		{
			return;
		}

		_pollCooldown = PollIntervalSeconds;
		// 轮询取快照(线程安全);内容有变化才重建文本
		var snapshot = _source.Snapshot();
		if (snapshot.Length == 0)
		{
			return;
		}

		var last = snapshot[^1];
		var fingerprint = (snapshot.Length, MinimumLevel, last.Timestamp.UtcTicks, last.Message);
		if (fingerprint == (_lastRender.Length, _lastRender.Level, _lastRender.Stamp, _lastRender.Message))
		{
			return;
		}

		_lastRender = (snapshot.Length, MinimumLevel, last.Timestamp.UtcTicks, last.Message);
		_logLabel.Text = RenderLog(snapshot);
	}

	/// <summary>立即按当前过滤重建日志文本(不依赖入树轮询;冒烟/测试直接断言用)。</summary>
	public void Refresh()
	{
		var snapshot = _source.Snapshot();
		_logLabel.Text = RenderLog(snapshot);
		var last = snapshot.Length > 0 ? snapshot[^1] : default;
		_lastRender = (snapshot.Length, MinimumLevel, last.Timestamp.UtcTicks, last.Message);
		_pollCooldown = PollIntervalSeconds;
	}

	private void CycleLevel()
	{
		// Debug → Info → Warning → Error → Debug(Trace 默认排除,日志窗口面向排障)
		var next = MinimumLevel switch
		{
			LmLogLevel.Debug => LmLogLevel.Info,
			LmLogLevel.Info => LmLogLevel.Warning,
			LmLogLevel.Warning => LmLogLevel.Error,
			_ => LmLogLevel.Debug,
		};
		SetMinimumLevel(next);
	}

	private string LevelButtonText() => $"级别 ≥ {MinimumLevel}";

	private string RenderLog(LogEvent[] snapshot)
	{
		var builder = new System.Text.StringBuilder();
		foreach (var @event in snapshot)
		{
			if (@event.Level < MinimumLevel)
			{
				continue;
			}

			builder.Append(@event.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff"));
			builder.Append(" [").Append(@event.Level).Append("] [").Append(@event.ModUid).Append("] ");
			builder.AppendLine(@event.Message);
			if (@event.Exception is not null)
			{
				builder.AppendLine(@event.Exception.ToString());
			}
		}

		return builder.ToString();
	}
}
