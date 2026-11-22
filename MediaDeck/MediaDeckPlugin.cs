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
        ApplyCursorGlowSetting();

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
        _view.SetSleepTimerRequested += HandleSetInstanceSleepTimer;

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
            RunOnUI(() =>
            {
                var cur = _mediaService.CurrentInstance;
                _view.UpdateSleepTimer(_sleepTimer.IsActive(cur?.Id), _sleepTimer.GetRemaining(cur?.Id));
                _spotlightView?.RefreshSleepTimersOnly();
            });
        };

        _sleepTimer.Expired += async instanceId =>
        {
            if (_stopped) return;
            var (paused, displayName) = await _mediaService.ExecuteSleepTimerActionAsync(instanceId);
            if (_stopped || string.IsNullOrWhiteSpace(displayName)) return;
            RunOnUI(() =>
            {
                Context.Island.ShowMessage(new IslandMessage
                {
                    Title = paused ? "定时暂停已触发" : "定时操作已触发",
                    Text = paused
                        ? $"已自动暂停 {displayName}"
                        : $"{displayName} 到期时未在播放，已按设定替换为触发播放",
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
        foreach (var key in new[] { "visual_effect_mode", "contour_reactivity" })
        {
            Context.Register(Context.OnSettingsChanged(key, () => RunOnUI(ApplyVisualEffectSetting)));
        }
        foreach (var key in new[] { "cursor_glow_enabled", "cursor_glow_size", "cursor_glow_opacity" })
        {
            Context.Register(Context.OnSettingsChanged(key, () => RunOnUI(ApplyCursorGlowSetting)));
        }
        Context.Register(Context.OnSettingsChanged("fallback_players", () =>
        {
            _mediaService.EnableFallbackPlayers = Settings.Get("fallback_players", true);
            _ = _mediaService.RefreshAsync();
        }));
        Context.Register(Context.OnSettingsChanged("lyrics_enabled", () => RunOnUI(RenderMediaSnapshot)));
        Context.Register(Context.OnSettingsChanged("spotlight_compact_view", () =>
        {
            RunOnUI(() => _spotlightView?.SetCompactView(Settings.Get("spotlight_compact_view", false)));
        }));

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
        try { RunOnUI(() => _view?.StopAllTimers()); } catch { }
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

    private void HandleSetInstanceSleepTimer(string instanceId, int minutes)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return;

        if (minutes <= 0)
        {
            _sleepTimer.Cancel(instanceId);
            return;
        }

        _sleepTimer.Start(instanceId, TimeSpan.FromMinutes(minutes));
        var inst = _mediaService.GetInstanceById(instanceId);
        if (inst is not null && !inst.IsPlaying && !_stopped)
        {
            Context.Island.ShowMessage(new IslandMessage
            {
                Title = "定时暂停提示",
                Text = $"{inst.SourceDisplayName} 当前未在播放：若倒计时结束时仍未播放，将把暂停行为替换为触发播放；若倒计时内开始播放则正常执行暂停。",
                Glyph = "\uE916",
                AccentColor = Color.FromArgb(255, 245, 158, 11),
                Duration = TimeSpan.FromSeconds(4),
            });
        }
    }

    private void RenderMediaSnapshot()
    {
        if (_stopped) return;

        var current = _mediaService.CurrentInstance;
        var compact = _mediaService.CompactInstance;
        bool lyricsOn = Settings.Get("lyrics_enabled", true);

        var triplet = ResolveLyricsTriplet(current, lyricsOn);
        var compactTriplet = compact is not null && current is not null &&
                             string.Equals(compact.Id, current.Id, StringComparison.OrdinalIgnoreCase)
            ? triplet
            : ResolveLyricsTriplet(compact, lyricsOn);

        _view.UpdateMedia(current, _mediaService.Instances.Count, _mediaService.SelectedIndex, triplet, compact, compactTriplet);
        _view.UpdateSleepTimer(_sleepTimer.IsActive(current?.Id), _sleepTimer.GetRemaining(current?.Id));
        _spotlightView?.RefreshMediaInstances();
    }

    private IReadOnlyList<(MediaInstanceInfo Info, LyricTriplet Lyrics)> BuildAllMediaSnapshots()
    {
        bool lyricsOn = Settings.Get("lyrics_enabled", true);
        var list = _mediaService.Instances;
        var result = new List<(MediaInstanceInfo, LyricTriplet)>(list.Count);
        foreach (var inst in list)
        {
            result.Add((inst, ResolveLyricsTriplet(inst, lyricsOn)));
        }
        return result;
    }

    private LyricTriplet ResolveLyricsTriplet(MediaInstanceInfo? inst, bool lyricsOn)
    {
        if (inst is null)
        {
            return new LyricTriplet("", "暂无媒体播放", "", false);
        }
        if (!lyricsOn)
        {
            return new LyricTriplet("", inst.Title, inst.Artist, false);
        }

        _lyricsService.EnsureLyricsAsync(inst.Title, inst.Artist, inst.Duration, () =>
        {
            if (!_stopped) RunOnUI(RenderMediaSnapshot);
        }, inst.NetEaseSongId, inst.SourceAppId, inst.LocalFilePath);
        return _lyricsService.GetTriplet(inst.Title, inst.Artist, inst.EstimatedPosition, inst.LocalFilePath);
    }

    private (VisualEffectMode Mode, double Reactivity, bool GlowOn, double GlowSize, double GlowOpacity) ReadVisualConfig()
    {
        int raw = Settings.Get("visual_effect_mode", 0);
        var mode = raw switch
        {
            1 => VisualEffectMode.AlbumContour,
            2 => VisualEffectMode.Off,
            _ => VisualEffectMode.AuroraWave,
        };
        double reactivity = Math.Clamp(Settings.Get("contour_reactivity", 100), 0, 500) / 100.0;
        bool glowOn = Settings.Get("cursor_glow_enabled", true);
        double glowSize = Math.Clamp(Settings.Get("cursor_glow_size", 96), 40, 320);
        double glowOpacity = Math.Clamp(Settings.Get("cursor_glow_opacity", 30), 5, 100) / 100.0;
        return (mode, reactivity, glowOn, glowSize, glowOpacity);
    }

    private void ApplyVisualEffectSetting()
    {
        var cfg = ReadVisualConfig();
        _view.SetVisualEffectMode(cfg.Mode);
        _view.SetContourReactivity(cfg.Reactivity);
        _spotlightView?.SetVisualConfig(cfg.Mode, cfg.Reactivity, cfg.GlowOn, cfg.GlowSize, cfg.GlowOpacity);
    }

    private void ApplyCursorGlowSetting()
    {
        var cfg = ReadVisualConfig();
        _view.SetCursorGlowSettings(cfg.GlowOn, cfg.GlowSize, cfg.GlowOpacity);
        _spotlightView?.SetVisualConfig(cfg.Mode, cfg.Reactivity, cfg.GlowOn, cfg.GlowSize, cfg.GlowOpacity);
    }

    private void RefreshDevicesOnUI()
    {
        if (_stopped) return;
        _view.UpdateOutputDevices(_audioService.CachedOutputDevices);
    }

    private MediaDeckSpotlightView CreateSpotlightView()
    {
        var view = new MediaDeckSpotlightView(
            Theme,
            _audioService,
            _sleepTimer,
            BuildAllMediaSnapshots);

        var cfg = ReadVisualConfig();
        view.SetVisualConfig(cfg.Mode, cfg.Reactivity, cfg.GlowOn, cfg.GlowSize, cfg.GlowOpacity);
        view.SetCompactView(Settings.Get("spotlight_compact_view", false));

        view.ActivateSourceRequested += id =>
        {
            if (!_mediaService.ActivateCurrentSourceWindow(id))
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
        view.PlayPauseRequested += id => _ = _mediaService.TogglePlayPauseAsync(id);
        view.PreviousRequested += id => _ = _mediaService.PreviousAsync(id);
        view.NextRequested += id => _ = _mediaService.NextAsync(id);
        view.SeekRatioRequested += async (id, ratio) =>
        {
            var inst = _mediaService.GetInstanceById(id);
            if (inst is not null && inst.Duration > TimeSpan.Zero)
            {
                var target = TimeSpan.FromSeconds(inst.Duration.TotalSeconds * ratio);
                bool nativeSeeked = await _mediaService.SeekAsync(target, id);
                if (!nativeSeeked && !_stopped)
                {
                    RunOnUI(() =>
                    {
                        Context.Island.ShowMessage(new IslandMessage
                        {
                            Title = "已校准歌词进度",
                            Text = $"{inst.SourceDisplayName} 客户端未开放外部跳转接口，已为您校准本地歌词时间",
                            Glyph = "\uE8D6",
                            Duration = TimeSpan.FromSeconds(2.5),
                        });
                    });
                }
            }
        };
        view.SetSleepTimerRequested += HandleSetInstanceSleepTimer;
        return view;
    }

    private void OpenSpotlight()
    {
        try
        {
            bool compact = Settings.Get("spotlight_compact_view", false);
            _spotlightView ??= CreateSpotlightView();
            _spotlightView.SetCompactView(compact);
            Context.Island.OpenSpotlight(new IslandSpotlight
            {
                Content = _spotlightView,
                Size = compact ? new Size(880, 620) : new Size(880, 500),
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

        // 视觉特效模式选择（极光流光波浪 vs 专辑封面物理轮廓光 vs 关闭）+ 轮廓光音频律动强度滑动条（0–500%）
        bool loadingVisual = true;
        int initVisualMode = Math.Clamp(Settings.Get("visual_effect_mode", 0), 0, 2);
        int initReactivity = Math.Clamp(Settings.Get("contour_reactivity", 100), 0, 500);

        var visualCombo = new ComboBox
        {
            Header = "胶囊与卡片音频视觉效果",
            MinWidth = 340,
            Items =
            {
                "流光极光波浪（随音频频率起伏流动）",
                "专辑封面轮廓光（取封面多色底部向内渐变光束）",
                "关闭背景视觉效果",
            },
            SelectedIndex = initVisualMode,
        };

        var reactivitySlider = new Slider
        {
            Header = initReactivity == 0 ? "轮廓光音频律动强度：0%（静态光）" : $"轮廓光音频律动强度：{initReactivity}%",
            Minimum = 0,
            Maximum = 500,
            StepFrequency = 10,
            Value = initReactivity,
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = initVisualMode == 1 ? Visibility.Visible : Visibility.Collapsed,
        };

        visualCombo.SelectionChanged += (_, _) =>
        {
            if (loadingVisual || visualCombo.SelectedIndex < 0) return;
            reactivitySlider.Visibility = visualCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (visualCombo.SelectedIndex != Settings.Get("visual_effect_mode", 0))
            {
                Settings.Set("visual_effect_mode", visualCombo.SelectedIndex);
            }
        };
        reactivitySlider.ValueChanged += (_, e) =>
        {
            int val = Math.Clamp((int)Math.Round(e.NewValue), 0, 500);
            reactivitySlider.Header = val == 0 ? "轮廓光音频律动强度：0%（静态光）" : $"轮廓光音频律动强度：{val}%";
            if (!loadingVisual && val != Settings.Get("contour_reactivity", 100))
            {
                Settings.Set("contour_reactivity", val);
            }
        };
        loadingVisual = false;

        var visualStack = new StackPanel
        {
            Spacing = 10,
            Children = { visualCombo, reactivitySlider },
        };
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = visualStack,
        });

        // 鼠标跟随圆形光效设置（开关 + 光圈大小 + 透明度，默认值：开 / 96 px / 30%）
        var cursorGlowStack = new StackPanel { Spacing = 10 };
        bool initGlowOn = Settings.Get("cursor_glow_enabled", true);
        int initGlowSize = Math.Clamp(Settings.Get("cursor_glow_size", 96), 40, 320);
        int initGlowOpacity = Math.Clamp(Settings.Get("cursor_glow_opacity", 30), 5, 100);

        var cursorGlowToggle = new ToggleSwitch
        {
            Header = "鼠标跟随圆形光效",
            IsOn = initGlowOn,
        };

        var glowSizeSlider = new Slider
        {
            Header = $"光圈大小：{initGlowSize} px",
            Minimum = 40,
            Maximum = 320,
            StepFrequency = 8,
            Value = initGlowSize,
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = initGlowOn,
        };

        var glowOpacitySlider = new Slider
        {
            Header = $"光圈透明度：{initGlowOpacity}%",
            Minimum = 5,
            Maximum = 100,
            StepFrequency = 5,
            Value = initGlowOpacity,
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = initGlowOn,
        };

        bool loadingCursorGlow = true;
        cursorGlowToggle.Toggled += (_, _) =>
        {
            if (loadingCursorGlow) return;
            bool on = cursorGlowToggle.IsOn;
            glowSizeSlider.IsEnabled = on;
            glowOpacitySlider.IsEnabled = on;
            Settings.Set("cursor_glow_enabled", on);
        };
        glowSizeSlider.ValueChanged += (_, e) =>
        {
            int val = Math.Clamp((int)Math.Round(e.NewValue), 40, 320);
            glowSizeSlider.Header = $"光圈大小：{val} px";
            if (!loadingCursorGlow && val != Settings.Get("cursor_glow_size", 96))
            {
                Settings.Set("cursor_glow_size", val);
            }
        };
        glowOpacitySlider.ValueChanged += (_, e) =>
        {
            int val = Math.Clamp((int)Math.Round(e.NewValue), 5, 100);
            glowOpacitySlider.Header = $"光圈透明度：{val}%";
            if (!loadingCursorGlow && val != Settings.Get("cursor_glow_opacity", 30))
            {
                Settings.Set("cursor_glow_opacity", val);
            }
        };
        loadingCursorGlow = false;

        cursorGlowStack.Children.Add(cursorGlowToggle);
        cursorGlowStack.Children.Add(glowSizeSlider);
        cursorGlowStack.Children.Add(glowOpacitySlider);
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = cursorGlowStack,
        });

        panel.Children.Add(MakeToggleCard("聚光卡使用紧凑视图（关闭时分页展示：首页音频控制，第二页媒体实例）", "spotlight_compact_view", false));
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
