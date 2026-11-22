using MediaDeck.Services;
using MediaDeck.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;
using WinIsland.Core;

namespace MediaDeck;

/// <summary>
/// 声枢（MediaDeck）插件入口。
/// </summary>
public sealed class MediaDeckPlugin : IslandPluginBase
{
    private AudioDeviceService _audioService = null!;
    private MediaSessionService _mediaService = null!;
    private LyricsService _lyricsService = null!;
    private SleepTimerService _sleepTimer = null!;

    private MediaDeckView _view = null!;
    private MediaDeckSpotlightView? _spotlightView;
    private IslandLiveContent _content = null!;
    private bool _stopped;
    private int _tickCount;

    // 动态优先级状态
    private DateTimeOffset? _allPausedSince;
    private int _appliedPriority = int.MinValue;

    protected override async Task OnInitializeAsync()
    {
        _stopped = false;
        _allPausedSince = null;
        _appliedPriority = int.MinValue;
        Log.Info($"启动：{Manifest.Name} ({Manifest.Id}) v{Manifest.Version}");

        _audioService = new AudioDeviceService(msg => Log.Warn(msg));
        _lyricsService = new LyricsService(msg => Log.Debug(msg));
        _mediaService = new MediaSessionService(_audioService, _lyricsService, msg => Log.Info(msg))
        {
            EnableFallbackPlayers = Settings.Get("fallback_players", true),
        };
        _sleepTimer = new SleepTimerService();

        Context.Register(_audioService);
        Context.Register(_mediaService);
        Context.Register(_lyricsService);

        _view = new MediaDeckView(
            Theme,
            _audioService.GetCurrentAudioPeak,
            () => (_audioService.MasterVolumePercent, _audioService.MasterMuted));
        ApplyVisualEffectSetting();

        // 启动时预先构造聚光卡视图树，首次点击秒开
        _spotlightView = CreateSpotlightView();

        // 绑定大岛卡片交互
        _view.ActivateSourceRequested += () =>
        {
            if (!_mediaService.ActivateCurrentSourceWindow())
            {
                Context.Island.ShowMessage(new IslandMessage
                {
                    Title = Manifest.Name,
                    Text = "未找到可激活的播放器窗口",
                    Glyph = "\uE8D6",
                    Duration = TimeSpan.FromSeconds(2),
                });
            }
        };
        _view.PlayPauseRequested += () => _ = _mediaService.TogglePlayPauseAsync();
        _view.PreviousRequested += () => _ = _mediaService.PreviousAsync();
        _view.NextRequested += () => _ = _mediaService.NextAsync();
        _view.SeekRatioRequested += async ratio =>
        {
            var cur = _mediaService.CurrentInstance;
            if (cur is not null && cur.Duration > TimeSpan.Zero)
            {
                var target = TimeSpan.FromSeconds(cur.Duration.TotalSeconds * ratio);
                bool nativeSeeked = await _mediaService.SeekAsync(target);
                if (!nativeSeeked && !_stopped)
                {
                    RunOnUI(() =>
                    {
                        Context.Island.ShowMessage(new IslandMessage
                        {
                            Title = "已校准歌词进度",
                            Text = $"{cur.SourceDisplayName} 客户端未开放外部跳转接口，已为您校准本地歌词时间",
                            Glyph = "\uE8D6",
                            Duration = TimeSpan.FromSeconds(2.5),
                        });
                    });
                }
            }
        };
        _view.SwitchInstanceRequested += delta => _mediaService.SwitchInstance(delta);
        _view.SelectInstanceRequested += idx => _mediaService.SelectInstance(idx);
        _view.SwitchOutputDeviceRequested += deviceId => _audioService.SwitchOutputDeviceAsync(deviceId);
        _view.MasterVolumeChangedRequested += pct => _audioService.SetMasterVolume(pct);
        _view.MasterVolumeStepRequested += delta => _audioService.StepMasterVolume(delta);

        // 服务事件监听
        _mediaService.SessionsUpdated += () =>
        {
            if (_stopped) return;
            RunOnUI(() =>
            {
                RenderMediaSnapshot();
                EvaluateDynamicPriority();
            });
        };

        _audioService.DevicesChanged += () =>
        {
            if (_stopped) return;
            RunOnUI(() =>
            {
                RefreshDevicesOnUI();
                _spotlightView?.RefreshAllSections();
            });
        };
        _audioService.CacheUpdated += () =>
        {
            if (_stopped) return;
            RunOnUI(RefreshDevicesOnUI);
        };

        _audioService.BluetoothHeadphonesConnected += dev =>
        {
            if (_stopped || !Settings.Get("bt_notify", true)) return;
            RunOnUI(() =>
            {
                Context.Island.ShowMessage(new IslandMessage
                {
                    Title = "蓝牙耳机已连接",
                    Text = dev.ShortName,
                    Glyph = "\uE7F6",
                    AccentColor = Color.FromArgb(255, 24, 132, 255),
                    Duration = TimeSpan.FromSeconds(3.5),
                });
            });
        };

        _sleepTimer.StateChanged += () =>
        {
            if (_stopped) return;
            RunOnUI(() => _view.UpdateSleepTimer(_sleepTimer.IsActive, _sleepTimer.Remaining));
        };

        _sleepTimer.Expired += async () =>
        {
            if (_stopped) return;
            bool pausedAny = await _mediaService.ExecuteSleepTimerActionAsync();
            if (_stopped) return;
            RunOnUI(() =>
            {
                Context.Island.ShowMessage(new IslandMessage
                {
                    Title = pausedAny ? "定时暂停已触发" : "定时操作已触发",
                    Text = pausedAny ? "已自动暂停当前媒体播放" : "倒计时结束时无正在播放的媒体，已按设定替换为触发播放",
                    Glyph = "\uE916",
                    AccentColor = Color.FromArgb(255, 56, 189, 248),
                    Duration = TimeSpan.FromSeconds(3.5),
                });
            });
        };

        int initialPriority = Settings.Get("auto_priority", true)
            ? Settings.Get("idle_priority", 0)
            : Settings.Get("fixed_priority", 80);
        _appliedPriority = initialPriority;

        _content = CreateLiveContent(initialPriority);

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE8D6", BuildSettingsPage, order: 90));

        Context.Register(Context.OnSettingsChanged("enabled", () =>
        {
            SetContent(Settings.Get("enabled", true) ? _content : null);
        }));
        Context.Register(Context.OnSettingsChanged("visual_effect_mode", () =>
        {
            RunOnUI(ApplyVisualEffectSetting);
        }));
        Context.Register(Context.OnSettingsChanged("fallback_players", () =>
        {
            _mediaService.EnableFallbackPlayers = Settings.Get("fallback_players", true);
            _ = _mediaService.RefreshAsync();
        }));
        Context.Register(Context.OnSettingsChanged("lyrics_enabled", () => RunOnUI(RenderMediaSnapshot)));

        foreach (var key in new[] { "auto_priority", "playing_priority", "idle_priority", "idle_delay_seconds", "fixed_priority" })
        {
            Context.Register(Context.OnSettingsChanged(key, () => RunOnUI(EvaluateDynamicPriority)));
        }

        if (Settings.Get("enabled", true))
        {
            SetContent(_content);
        }

        RefreshDevicesOnUI();

        _ = Task.Run(async () =>
        {
            try
            {
                await _mediaService.InitializeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("媒体服务初始化异常", ex);
            }
        });

        // 主定时器：每 500ms 刷新进度条与歌词；每 3 秒做一次全量会话兜底同步
        Context.CreateTimer(TimeSpan.FromMilliseconds(500), repeat: true, OnTick);
        await Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        _stopped = true;
        try { SetContent(null); } catch { }
        Log.Info("声枢已停用");
        return Task.CompletedTask;
    }

    private void OnTick()
    {
        if (_stopped) return;
        _tickCount++;

        if (_tickCount % 2 == 0)
        {
            _sleepTimer.Tick();
            EvaluateDynamicPriority();
        }

        if (_tickCount % 3 == 0)
        {
            _ = _mediaService.RefreshAsync();
        }

        RenderMediaSnapshot();
    }

    private void EvaluateDynamicPriority()
    {
        if (_stopped || !Settings.Get("enabled", true)) return;

        bool auto = Settings.Get("auto_priority", true);
        int targetPriority;

        if (!auto)
        {
            _allPausedSince = null;
            targetPriority = Math.Clamp(Settings.Get("fixed_priority", 80), 0, 999);
        }
        else
        {
            bool anyPlaying = _mediaService.Instances.Any(x => x.IsPlaying);
            int playingPrio = Math.Clamp(Settings.Get("playing_priority", 110), 0, 999);
            int idlePrio = Math.Clamp(Settings.Get("idle_priority", 0), 0, 999);
            int delaySec = Math.Clamp(Settings.Get("idle_delay_seconds", 60), 5, 3600);

            if (anyPlaying)
            {
                _allPausedSince = null;
                targetPriority = playingPrio;
            }
            else
            {
                _allPausedSince ??= DateTimeOffset.UtcNow;
                var pausedFor = DateTimeOffset.UtcNow - _allPausedSince.Value;
                targetPriority = pausedFor.TotalSeconds >= delaySec ? idlePrio : _appliedPriority;
                if (_appliedPriority == int.MinValue)
                {
                    targetPriority = idlePrio;
                }
            }
        }

        if (targetPriority != _appliedPriority)
        {
            _appliedPriority = targetPriority;
            _content = CreateLiveContent(targetPriority);
            try
            {
                UpdateContent(_content);
                Log.Info($"动态优先级已更新为：{targetPriority}");
            }
            catch { }
        }
    }

    private IslandLiveContent CreateLiveContent(int priority) => new()
    {
        Priority = priority,
        OwnerLabel = Manifest.Name,
        OwnerGlyph = Manifest.IconGlyph ?? "\uE8D6",
        OwnerAccent = Color.FromArgb(255, 56, 189, 248),
        MorphView = _view,
        CompactSize = new Size(250, 40),
        ExpandedSize = new Size(560, 208),
        OnTap = OpenSpotlight,
    };

    private void RenderMediaSnapshot()
    {
        if (_stopped) return;

        var current = _mediaService.CurrentInstance;
        bool lyricsOn = Settings.Get("lyrics_enabled", true);

        LyricTriplet triplet;
        if (current is null)
        {
            triplet = new LyricTriplet("", "暂无媒体播放", "", false);
        }
        else if (!lyricsOn)
        {
            triplet = new LyricTriplet("", current.Title, current.Artist, false);
        }
        else
        {
            _lyricsService.EnsureLyricsAsync(current.Title, current.Artist, current.Duration, () =>
            {
                if (!_stopped) RunOnUI(RenderMediaSnapshot);
            }, current.NetEaseSongId);
            triplet = _lyricsService.GetTriplet(current.Title, current.Artist, current.EstimatedPosition);
        }

        _view.UpdateMedia(current, _mediaService.Instances.Count, _mediaService.SelectedIndex, triplet);
    }

    private void ApplyVisualEffectSetting()
    {
        int raw = Settings.Get("visual_effect_mode", 0);
        var mode = raw switch
        {
            1 => VisualEffectMode.AlbumContour,
            2 => VisualEffectMode.Off,
            _ => VisualEffectMode.AuroraWave,
        };
        _view.SetVisualEffectMode(mode);
    }

    private void RefreshDevicesOnUI()
    {
        if (_stopped) return;
        _view.UpdateOutputDevices(_audioService.CachedOutputDevices);
    }

    private MediaDeckSpotlightView CreateSpotlightView() => new(
        Theme,
        _audioService,
        _sleepTimer,
        () => _mediaService.HasAnyPlaying,
        warningText =>
        {
            if (_stopped) return;
            Context.Island.ShowMessage(new IslandMessage
            {
                Title = "定时暂停提示",
                Text = warningText,
                Glyph = "\uE916",
                AccentColor = Color.FromArgb(255, 245, 158, 11),
                Duration = TimeSpan.FromSeconds(4),
            });
        });

    private void OpenSpotlight()
    {
        try
        {
            _spotlightView ??= CreateSpotlightView();
            Context.Island.OpenSpotlight(new IslandSpotlight
            {
                Content = _spotlightView,
                Size = new Size(860, 440),
                OnClosed = () => _spotlightView?.OnHostClosed(),
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开聚光卡失败", ex);
        }
    }

    private UIElement BuildSettingsPage()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{Manifest.Name} (MediaDeck)",
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });

        panel.Children.Add(MakeToggleCard("在灵动岛上显示", "enabled", true));

        // 视觉特效模式选择（极光流光波浪 vs 专辑封面物理轮廓光 vs 关闭）
        bool loadingVisual = true;
        var visualCombo = new ComboBox
        {
            Header = "胶囊与卡片音频视觉效果",
            MinWidth = 340,
            Items =
            {
                "流光极光波浪（随音频频率起伏流动）",
                "专辑封面物理轮廓光（取封面多色合成渐变，底部最亮自下而上衰减）",
                "关闭背景视觉效果",
            },
            SelectedIndex = Math.Clamp(Settings.Get("visual_effect_mode", 0), 0, 2),
        };
        visualCombo.SelectionChanged += (_, _) =>
        {
            if (loadingVisual || visualCombo.SelectedIndex < 0) return;
            if (visualCombo.SelectedIndex != Settings.Get("visual_effect_mode", 0))
            {
                Settings.Set("visual_effect_mode", visualCombo.SelectedIndex);
            }
        };
        loadingVisual = false;
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = visualCombo,
        });

        panel.Children.Add(MakeToggleCard("蓝牙耳机接入提醒", "bt_notify", true));
        panel.Children.Add(MakeToggleCard("卡片上显示同步歌词", "lyrics_enabled", true));
        panel.Children.Add(MakeToggleCard("兼容未接入系统媒体控制的播放器（如网易云音乐）", "fallback_players", true));

        // ---- 动态优先级设置区 ----
        var prioStack = new StackPanel { Spacing = 10 };
        var autoToggle = new ToggleSwitch
        {
            Header = "播放时自动提高优先级，全部暂停超时后自动退位",
            IsOn = Settings.Get("auto_priority", true),
        };
        autoToggle.Toggled += (_, _) => Settings.Set("auto_priority", autoToggle.IsOn);

        var playingBox = MakeIntNumberBox("播放中优先级（占据胶囊显示，0–999）", "playing_priority", 110, 0, 999);
        var idleBox = MakeIntNumberBox("全部暂停/无响应后优先级（退位让给其他插件，0–999）", "idle_priority", 0, 0, 999);
        var delayBox = MakeIntNumberBox("全部暂停多久后自动降低优先级（秒，5–3600）", "idle_delay_seconds", 60, 5, 3600);
        var fixedBox = MakeIntNumberBox("关闭自动调整时的固定优先级（0–999）", "fixed_priority", 80, 0, 999);

        var tipText = new TextBlock
        {
            Text = "提示：请保持「插件管理 → 声枢 → 优先级」为默认，否则宿主手动固定值会覆盖动态优先级。",
            FontSize = 12,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        };

        prioStack.Children.Add(autoToggle);
        prioStack.Children.Add(playingBox);
        prioStack.Children.Add(idleBox);
        prioStack.Children.Add(delayBox);
        prioStack.Children.Add(fixedBox);
        prioStack.Children.Add(tipText);

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = prioStack,
        });

        var testBtBtn = new Button { Content = "预览蓝牙耳机接入提示" };
        testBtBtn.Click += (_, _) => Context.Island.ShowMessage(new IslandMessage
        {
            Title = "蓝牙耳机已连接",
            Text = "WH-1000XM5 (示例预览)",
            Glyph = "\uE7F6",
            AccentColor = Color.FromArgb(255, 24, 132, 255),
            Duration = TimeSpan.FromSeconds(3),
        });
        panel.Children.Add(testBtBtn);

        return panel;
    }

    private NumberBox MakeIntNumberBox(string header, string key, int defaultVal, int min, int max)
    {
        bool loading = true;
        var box = new NumberBox
        {
            Header = header,
            Minimum = min,
            Maximum = max,
            SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = Settings.Get(key, defaultVal),
            MaxWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        void Commit()
        {
            if (loading || double.IsNaN(box.Value)) return;
            int val = Math.Clamp((int)Math.Round(box.Value), min, max);
            if (val != Settings.Get(key, defaultVal))
            {
                Settings.Set(key, val);
            }
        }

        box.ValueChanged += (_, _) => Commit();
        box.LostFocus += (_, _) => Commit();
        loading = false;
        return box;
    }

    private Border MakeToggleCard(string header, string key, bool defaultVal)
    {
        var toggle = new ToggleSwitch
        {
            Header = header,
            IsOn = Settings.Get(key, defaultVal),
        };
        toggle.Toggled += (_, _) => Settings.Set(key, toggle.IsOn);

        return new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = toggle,
        };
    }
}
