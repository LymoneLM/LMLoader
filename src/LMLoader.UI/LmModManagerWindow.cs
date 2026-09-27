// SPDX-License-Identifier: LGPL-3.0-or-later

using Godot;
using LMLoader.Core;
using LMLoader.Core.Management;

namespace LMLoader.UI;

/// <summary>
/// 模组管理窗口(可选组件,D19):按清单分组展示全部已发现模组(含被禁用者),
/// uid 级启停开关 + 全局总开关(纯净模式);禁用时预览"将因硬依赖缺失而无法加载"的模组;
/// 横幅提示变更重启后生效。**重启时机与方式由游戏决定**(保存→退出→重新启动进程);
/// 经代码 <c>new</c> 创建(不注册 Godot 脚本),宿主经 <c>OpenModManager()</c> 打开/切换。
/// 刷新采用轮询(与日志窗口同模式),不依赖事件线程。
/// </summary>
public sealed class LmModManagerWindow : Control
{
	private const float PollIntervalSeconds = 0.25f;

	private readonly ModManager _manager;
	private readonly CheckBox _globalToggle;
	private readonly Label _banner;
	private readonly VBoxContainer _listRoot;
	private readonly Label _warning;
	private double _pollCooldown;
	private bool _lastPending;
	private bool _lastGlobal;
	private bool _rebuilding;

	public LmModManagerWindow(ModManager manager)
	{
		_manager = manager ?? throw new ArgumentNullException(nameof(manager));

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
		header.AddChild(new Label { Text = "模组管理" });
		var closeButton = new Button { Text = "关闭" };
		closeButton.Pressed += () => Visible = false;
		header.AddChild(closeButton);
		layout.AddChild(header);

		_globalToggle = new CheckBox { Text = "启用全部模组(总开关;关闭 = 纯净模式)" };
		_globalToggle.Toggled += OnGlobalToggled;
		layout.AddChild(_globalToggle);

		_banner = new Label
		{
			Text = "开关变更在下次启动游戏时生效。",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		_banner.Modulate = new Color(1, 0.85f, 0.3f);
		layout.AddChild(_banner);

		_warning = new Label
		{
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		_warning.Modulate = new Color(1, 0.45f, 0.35f);
		layout.AddChild(_warning);

		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		layout.AddChild(scroll);
		_listRoot = new VBoxContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		scroll.AddChild(_listRoot);

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
		if (_manager.PendingRestart != _lastPending || _manager.GlobalEnabled != _lastGlobal)
		{
			Refresh();
		}
	}

	/// <summary>立即按当前状态重建列表与提示(轮询之外的手动刷新入口)。</summary>
	public void Refresh()
	{
		var discovered = _manager.GetDiscoveredMods();
		_lastPending = _manager.PendingRestart;
		_lastGlobal = _manager.GlobalEnabled;

		_rebuilding = true;
		_globalToggle.ButtonPressed = _manager.GlobalEnabled;
		foreach (var child in _listRoot.GetChildren())
		{
			child.QueueFree();
		}

		foreach (var group in discovered.GroupBy(m => m.SourcePath).OrderBy(g => g.Key, StringComparer.Ordinal))
		{
			_listRoot.AddChild(new Label { Text = group.Key });
			foreach (var mod in group.OrderBy(m => m.Uid, StringComparer.Ordinal))
			{
				var row = new HBoxContainer();
				var toggle = new CheckBox { ButtonPressed = mod.Enabled };
				toggle.Toggled += pressed => OnModToggled(mod.Uid, pressed);
				row.AddChild(toggle);
				row.AddChild(new Label
				{
					Text = $"{mod.Name} v{mod.Version} — {mod.Uid}",
					TooltipText = mod.Detail.Length > 0 ? $"{mod.Status}: {mod.Detail}" : mod.Status.ToString(),
				});
				_listRoot.AddChild(row);
			}
		}

		_rebuilding = false;
		RefreshBanner();
	}

	private void OnGlobalToggled(bool pressed)
	{
		if (_rebuilding)
		{
			return;
		}

		_manager.SetAllEnabled(pressed);
		Refresh();
	}

	private void OnModToggled(string uid, bool pressed)
	{
		if (_rebuilding)
		{
			return;
		}

		_manager.SetModEnabled(uid, pressed);

		// 依赖预计算:仅禁用方向有影响面(D19:只预览不强制)
		var impacts = pressed ? Array.Empty<DependentImpact>() : _manager.PreviewDisable(uid);
		_pendingWarning = impacts.Count == 0
			? null
			: $"禁用 {uid} 将导致以下模组无法加载: " +
				string.Join(", ", impacts.Select(i => $"{i.Name}({i.Chain})"));
		Refresh();
	}

	private string? _pendingWarning;

	private void RefreshBanner()
	{
		var lines = new List<string>();
		if (_manager.PendingRestart)
		{
			lines.Add("有启停变更待生效:重启游戏后加载。");
		}

		if (_pendingWarning is not null)
		{
			lines.Add(_pendingWarning);
		}

		_banner.Text = lines.Count > 0 ? string.Join("\n", lines) : "开关变更在下次启动游戏时生效。";
	}
}
