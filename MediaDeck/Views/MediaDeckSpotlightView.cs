using MediaDeck.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinIsland.Core;

namespace MediaDeck.Views;

/// <summary>
/// 「声枢」超级展开聚光卡（三列独立容器布局 + 零阻塞缓存渲染）：
///   - 顶部：标题 + 定时暂停紧凑快捷档位 + 刷新按钮
///   - 主体自左向右三个独立容器：
///     1. 音频输出设备（左）
///     2. Windows 音量合成器（中）
///     3. Windows 空间音效（右）
/// </summary>
public sealed class MediaDeckSpotlightView : UserControl
{
    private readonly IIslandTheme _theme;
    private readonly AudioDeviceService _audioService;
    private readonly SleepTimerService _sleepTimer;
    private readonly DispatcherQueueTimer _timer;

    // 共享主题画刷
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _paneBgBrush = new();
    private readonly SolidColorBrush _paneBorderBrush = new();
    private readonly SolidColorBrush _itemBgBrush = new();
    private readonly SolidColorBrush _itemSelectedBgBrush = new();
    private readonly SolidColorBrush _itemHoverBrush = new();
    private readonly SolidColorBrush _dividerBrush = new();
    private readonly SolidColorBrush _accentBrush = new(Color.FromArgb(255, 14, 165, 233));
    private readonly SolidColorBrush _btBlueBrush = new(Color.FromArgb(255, 24, 132, 255));

    private readonly TextBlock _timerStatusText;
    private readonly StackPanel _devicesPanel;
    private readonly StackPanel _mixerPanel;
    private readonly StackPanel _spatialPanel;

    private bool _isDraggingSlider;
    private bool _isLoaded;
    private long _renderedDevicesVer = -1;
    private long _renderedMixerVer = -1;
    private long _renderedSpatialVer = -1;

    public MediaDeckSpotlightView(
        IIslandTheme theme,
        AudioDeviceService audioService,
        SleepTimerService sleepTimer)
    {
        _theme = theme;
        _audioService = audioService;
        _sleepTimer = sleepTimer;

        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        // ==================== 顶部栏：标题 + 定时暂停 + 刷新 ====================
        var headerTitle = new TextBlock
        {
            Text = "声枢 · 音频控制中心",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _timerStatusText = new TextBlock
        {
            Text = "定时暂停：未开启",
            FontSize = 11.5,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var titleStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { headerTitle, _timerStatusText },
        };

        var timerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        timerRow.Children.Add(new FontIcon
        {
            Glyph = "\uE916",
            FontSize = 13,
            Foreground = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
        });

        foreach (var (label, mins) in new[] { ("关", 0), ("15分", 15), ("30分", 30), ("45分", 45), ("60分", 60) })
        {
            int m = mins;
            var chip = new Button
            {
                Content = label,
                FontSize = 11.5,
                Foreground = _textBrush,
                Padding = new Thickness(8, 3, 8, 3),
                CornerRadius = new CornerRadius(12),
                Background = _itemBgBrush,
                BorderThickness = new Thickness(0),
            };
            chip.Click += (_, _) =>
            {
                if (m <= 0) _sleepTimer.Cancel();
                else _sleepTimer.Start(TimeSpan.FromMinutes(m));
                UpdateTimerLabel();
            };
            timerRow.Children.Add(chip);
        }

        var refreshBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE72C", FontSize = 12, Foreground = _textBrush },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = _itemBgBrush,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
        };
        refreshBtn.Click += (_, _) => _audioService.RequestRefreshAll();
        timerRow.Children.Add(refreshBtn);

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleStack, 0);
        Grid.SetColumn(timerRow, 1);
        headerGrid.Children.Add(titleStack);
        headerGrid.Children.Add(timerRow);

        // ==================== 主体：自左向右三个独立容器 ====================
        _devicesPanel = new StackPanel { Spacing = 4 };
        _mixerPanel = new StackPanel { Spacing = 4 };
        _spatialPanel = new StackPanel { Spacing = 4 };

        var colDevices = BuildColumnCard("音频输出设备", "\uE7F6", _devicesPanel);
        var colMixer = BuildColumnCard("音量合成器", "\uE995", _mixerPanel);
        var colSpatial = BuildColumnCard("空间音效", "\uE909", _spatialPanel);

        var columnsGrid = new Grid { ColumnSpacing = 12 };
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.30, GridUnitType.Star) });
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95, GridUnitType.Star) });

        Grid.SetColumn(colDevices, 0);
        Grid.SetColumn(colMixer, 1);
        Grid.SetColumn(colSpatial, 2);
        columnsGrid.Children.Add(colDevices);
        columnsGrid.Children.Add(colMixer);
        columnsGrid.Children.Add(colSpatial);

        var root = new Grid
        {
            Padding = new Thickness(20, 16, 20, 16),
            RowSpacing = 12,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(columnsGrid, 1);
        root.Children.Add(headerGrid);
        root.Children.Add(columnsGrid);

        Content = root;

        // 后台缓存刷新完后回 UI 重绘（不阻塞主线程）
        _audioService.CacheUpdated += OnAudioCacheUpdated;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1.5);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) =>
        {
            UpdateTimerLabel();
            if (!_isDraggingSlider)
            {
                _audioService.RequestRefreshMixer();
            }
        };

        Loaded += (_, _) =>
        {
            _isLoaded = true;
            // 仅重绘有变更的列（预热过则 0ms 直接展示，不阻塞飞入动画）
            RenderChangedSectionsFromCache();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            _timer.Stop();
        };
    }

    public void OnHostClosed()
    {
        _isLoaded = false;
        _timer.Stop();
    }

    public void RefreshAllSections()
    {
        if (!_isLoaded) return;
        RenderChangedSectionsFromCache();
    }

    private void OnAudioCacheUpdated()
    {
        if (!_isLoaded || _isDraggingSlider) return;
        DispatcherQueue?.TryEnqueue(RenderChangedSectionsFromCache);
    }

    private void RenderChangedSectionsFromCache()
    {
        UpdateTimerLabel();

        var dVer = _audioService.DevicesVersion;
        if (dVer != _renderedDevicesVer)
        {
            _renderedDevicesVer = dVer;
            RenderDevicesSection(_audioService.CachedOutputDevices);
        }

        var mVer = _audioService.MixerVersion;
        if (mVer != _renderedMixerVer)
        {
            _renderedMixerVer = mVer;
            RenderMixerSection(_audioService.CachedMixerSessions);
        }

        var sVer = _audioService.SpatialVersion;
        if (sVer != _renderedSpatialVer)
        {
            _renderedSpatialVer = sVer;
            RenderSpatialSection(_audioService.CachedSpatialOptions);
        }
    }

    private Border BuildColumnCard(string title, string glyph, StackPanel listPanel)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Thickness(2, 0, 2, 6),
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 13, Foreground = _accentBrush, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _textBrush, VerticalAlignment = VerticalAlignment.Center },
            },
        };

        var divider = new Border { Height = 1, Background = _dividerBrush, Margin = new Thickness(0, 0, 0, 6) };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = listPanel,
        };

        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(header, 0);
        Grid.SetRow(divider, 1);
        Grid.SetRow(scroll, 2);
        inner.Children.Add(header);
        inner.Children.Add(divider);
        inner.Children.Add(scroll);

        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = _paneBgBrush,
            BorderBrush = _paneBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 10, 10, 10),
            Child = inner,
        };
    }

    private void UpdateTimerLabel()
    {
        if (_sleepTimer.IsActive)
        {
            var r = _sleepTimer.Remaining;
            _timerStatusText.Text = $"定时暂停进行中：剩余 {(int)r.TotalMinutes:D2}:{r.Seconds:D2}";
        }
        else
        {
            _timerStatusText.Text = "定时暂停：未开启";
        }
    }

    private void RenderDevicesSection(IReadOnlyList<AudioDeviceInfo> devices)
    {
        _devicesPanel.Children.Clear();
        if (devices.Count == 0)
        {
            _devicesPanel.Children.Add(EmptyHint("未检测到音频输出设备"));
            return;
        }

        foreach (var dev in devices)
        {
            var accentBar = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1.5),
                Background = _accentBrush,
                Opacity = dev.IsDefault ? 1 : 0,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var iconGrid = new Grid { Width = 22, Height = 20 };
            iconGrid.Children.Add(new FontIcon
            {
                Glyph = dev.Kind == AudioOutputKind.Headphone ? "\uE7F6" : "\uE767",
                FontSize = 14,
                Foreground = dev.IsDefault ? _accentBrush : _textBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (dev.IsBluetooth)
            {
                iconGrid.Children.Add(new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    Background = _btBlueBrush,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new FontIcon
                    {
                        Glyph = "\uE702",
                        FontSize = 6.5,
                        Foreground = new SolidColorBrush(Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
            }

            var nameText = new TextBlock
            {
                Text = dev.ShortName,
                FontSize = 12,
                FontWeight = dev.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = _textBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var row = new Grid { ColumnSpacing = 7 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(accentBar, 0);
            Grid.SetColumn(iconGrid, 1);
            Grid.SetColumn(nameText, 2);
            row.Children.Add(accentBar);
            row.Children.Add(iconGrid);
            row.Children.Add(nameText);

            var btn = CreateCompactRowButton(row, dev.IsDefault, new Thickness(6, 6, 8, 6));
            var id = dev.Id;
            btn.Click += (_, _) => _audioService.SwitchOutputDeviceAsync(id);
            _devicesPanel.Children.Add(btn);
        }
    }

    private void RenderMixerSection(IReadOnlyList<MixerSessionInfo> sessions)
    {
        _mixerPanel.Children.Clear();
        if (sessions.Count == 0)
        {
            _mixerPanel.Children.Add(EmptyHint("暂无发声程序"));
            return;
        }

        foreach (var s in sessions)
        {
            int pid = s.ProcessId;
            bool muted = s.IsMuted;

            var muteIcon = new FontIcon
            {
                Glyph = muted ? "\uE74F" : "\uE767",
                FontSize = 13,
                Foreground = muted ? _faintBrush : _textBrush,
            };
            var muteBtn = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = muteIcon,
            };

            var nameText = new TextBlock
            {
                Text = s.DisplayName,
                FontSize = 12,
                Foreground = _textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = 88,
            };

            var pctText = new TextBlock
            {
                Text = muted ? "静音" : $"{(int)Math.Round(s.Volume * 100)}%",
                FontSize = 11,
                Foreground = _mutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Width = 34,
            };

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = Math.Round(s.Volume * 100),
                VerticalAlignment = VerticalAlignment.Center,
                IsThumbToolTipEnabled = false,
                Margin = new Thickness(0, -4, 0, -4),
            };

            slider.PointerCaptureLost += (_, _) => _isDraggingSlider = false;
            slider.ValueChanged += (_, e) =>
            {
                _isDraggingSlider = true;
                float vol = (float)(e.NewValue / 100.0);
                _audioService.SetSessionVolume(pid, vol);
                if (muted && vol > 0)
                {
                    muted = false;
                    _audioService.SetSessionMute(pid, false);
                    muteIcon.Glyph = "\uE767";
                    muteIcon.Foreground = _textBrush;
                }
                pctText.Text = $"{(int)Math.Round(e.NewValue)}%";
                _isDraggingSlider = false;
            };

            muteBtn.Click += (_, _) =>
            {
                muted = !muted;
                _audioService.SetSessionMute(pid, muted);
                muteIcon.Glyph = muted ? "\uE74F" : "\uE767";
                muteIcon.Foreground = muted ? _faintBrush : _textBrush;
                pctText.Text = muted ? "静音" : $"{(int)Math.Round(slider.Value)}%";
            };

            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(muteBtn, 0);
            Grid.SetColumn(nameText, 1);
            Grid.SetColumn(slider, 2);
            Grid.SetColumn(pctText, 3);
            row.Children.Add(muteBtn);
            row.Children.Add(nameText);
            row.Children.Add(slider);
            row.Children.Add(pctText);

            _mixerPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = _itemBgBrush,
                Padding = new Thickness(6, 3, 8, 3),
                Child = row,
            });
        }
    }

    private void RenderSpatialSection(IReadOnlyList<SpatialSoundOption> options)
    {
        _spatialPanel.Children.Clear();
        if (options.Count == 0)
        {
            _spatialPanel.Children.Add(EmptyHint("当前设备不支持空间音效"));
            return;
        }

        foreach (var opt in options)
        {
            var accentBar = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1.5),
                Background = _accentBrush,
                Opacity = opt.IsActive ? 1 : 0,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var label = new TextBlock
            {
                Text = opt.DisplayName,
                FontSize = 12,
                FontWeight = opt.IsActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = _textBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(accentBar, 0);
            Grid.SetColumn(label, 1);
            row.Children.Add(accentBar);
            row.Children.Add(label);

            var btn = CreateCompactRowButton(row, opt.IsActive, new Thickness(6, 6, 8, 6));
            var subtype = opt.Subtype;
            btn.Click += (_, _) => _audioService.SetSpatialSoundAsync(subtype);
            _spatialPanel.Children.Add(btn);
        }
    }

    private Button CreateCompactRowButton(UIElement content, bool isSelected, Thickness padding)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = padding,
            CornerRadius = new CornerRadius(8),
            Background = isSelected ? _itemSelectedBgBrush : _itemBgBrush,
            BorderThickness = new Thickness(0),
            Content = content,
        };
        btn.PointerEntered += (_, _) => btn.Background = _itemHoverBrush;
        btn.PointerExited += (_, _) => btn.Background = isSelected ? _itemSelectedBgBrush : _itemBgBrush;
        return btn;
    }

    private TextBlock EmptyHint(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = _faintBrush,
        Margin = new Thickness(4, 4, 4, 4),
    };

    private void ApplyThemeColors()
    {
        bool light = _theme.IsLight;
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(175);
        _faintBrush.Color = Neutral(125);
        _paneBgBrush.Color = light ? Color.FromArgb(14, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255);
        _paneBorderBrush.Color = light ? Color.FromArgb(24, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255);
        _itemBgBrush.Color = light ? Color.FromArgb(16, 0, 0, 0) : Color.FromArgb(20, 255, 255, 255);
        _itemSelectedBgBrush.Color = light ? Color.FromArgb(32, 14, 165, 233) : Color.FromArgb(42, 56, 189, 248);
        _itemHoverBrush.Color = light ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(38, 255, 255, 255);
        _dividerBrush.Color = Neutral(26);
    }

    private Color Neutral(byte alpha) => _theme.IsLight
        ? Color.FromArgb(alpha, 0, 0, 0)
        : Color.FromArgb(alpha, 255, 255, 255);
}
