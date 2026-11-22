using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using MediaDeck.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Streams;
using Windows.UI;
using WinIsland.Core;

namespace MediaDeck.Views;

/// <summary>
/// 「声枢」超级展开聚光卡（三列独立容器布局 + 零阻塞缓存渲染）：
///   1. 音频输出设备（左）：区分 HDMI/显示器(\uE7F4)、耳机(\uE7F6)+蓝牙角标、扬声器(\uE7F5)
///   2. Windows 音量合成器（中）：显示各自程序真实图标，右上角挂载 Quietify 5柱动态发声/5点静音角标，悬停提示“静音该应用”
///   3. Windows 空间音效（右）：与 Windows 11 系统列表完全一致
/// </summary>
public sealed class MediaDeckSpotlightView : UserControl
{
    // Quietify SVG 动画关键帧（精确还原 viewBox 0 0 135 140 下的 height 与 y 序列）
    private static readonly double[] WaveHeights = [120, 110, 100, 90, 80, 70, 60, 50, 40, 140, 120];
    private static readonly double[] WaveYs = [10, 15, 20, 25, 30, 35, 40, 45, 50, 0, 10];
    private static readonly double[] BarPhaseOffsets = [0.50, 0.25, 0.00, 0.25, 0.50];

    private sealed class QuietifyBadgeController
    {
        public required Border BadgeHost { get; init; }
        public required Rectangle[] Bars { get; init; }
        public bool IsMuted { get; set; }
        public bool IsGeneratingSound { get; set; }
    }

    private readonly IIslandTheme _theme;
    private readonly AudioDeviceService _audioService;
    private readonly SleepTimerService _sleepTimer;
    private readonly Func<bool> _hasAnyPlayingProvider;
    private readonly Action<string>? _onTimerNoMediaWarning;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _waveTimer;
    private readonly Dictionary<string, BitmapImage> _appIconBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<QuietifyBadgeController> _activeBadges = [];

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
    private readonly SolidColorBrush _hintBgBrush = new();
    private readonly SolidColorBrush _badgeBgBrush = new();
    private readonly SolidColorBrush _badgeMutedBgBrush = new(Color.FromArgb(230, 100, 116, 139));
    private readonly SolidColorBrush _accentBrush = new(Color.FromArgb(255, 14, 165, 233));
    private readonly SolidColorBrush _btBlueBrush = new(Color.FromArgb(255, 24, 132, 255));
    private readonly SolidColorBrush _whiteBrush = new(Colors.White);

    private readonly TextBlock _timerStatusText;
    private readonly StackPanel _devicesPanel;
    private readonly StackPanel _mixerPanel;
    private readonly StackPanel _spatialPanel;

    // 悬停提示层
    private readonly Canvas _hintCanvas;
    private readonly Border _hintPill;
    private readonly TextBlock _hintText;

    private bool _isDraggingSlider;
    private bool _isLoaded;
    private long _renderedDevicesVer = -1;
    private long _renderedMixerVer = -1;
    private long _renderedSpatialVer = -1;

    public MediaDeckSpotlightView(
        IIslandTheme theme,
        AudioDeviceService audioService,
        SleepTimerService sleepTimer,
        Func<bool> hasAnyPlayingProvider,
        Action<string>? onTimerNoMediaWarning = null)
    {
        _theme = theme;
        _audioService = audioService;
        _sleepTimer = sleepTimer;
        _hasAnyPlayingProvider = hasAnyPlayingProvider;
        _onTimerNoMediaWarning = onTimerNoMediaWarning;

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
                if (m <= 0)
                {
                    _sleepTimer.Cancel();
                }
                else
                {
                    _sleepTimer.Start(TimeSpan.FromMinutes(m));
                    if (!_hasAnyPlayingProvider())
                    {
                        _onTimerNoMediaWarning?.Invoke("当前无正在播放的媒体：若倒计时结束时仍无媒体播放，将把暂停行为替换为触发播放；若倒计时内有媒体播放则正常执行暂停。");
                    }
                }
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

        var colDevices = BuildColumnCard("音频输出设备", "\uE7F6", _devicesPanel, "ms-settings:sound", "打开 Windows 音频输出设备设置");
        var colMixer = BuildColumnCard("音量合成器", "\uE995", _mixerPanel, "ms-settings:apps-volume", "打开 Windows 音量合成器设置");
        var colSpatial = BuildColumnCard("空间音效", "\uE909", _spatialPanel, "ms-settings:sound-defaultoutputproperties", "打开 Windows 空间音效设置");

        var columnsGrid = new Grid { ColumnSpacing = 12 };
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.10, GridUnitType.Star) });
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.30, GridUnitType.Star) });
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.92, GridUnitType.Star) });

        Grid.SetColumn(colDevices, 0);
        Grid.SetColumn(colMixer, 1);
        Grid.SetColumn(colSpatial, 2);
        columnsGrid.Children.Add(colDevices);
        columnsGrid.Children.Add(colMixer);
        columnsGrid.Children.Add(colSpatial);

        var layoutGrid = new Grid
        {
            Padding = new Thickness(20, 16, 20, 16),
            RowSpacing = 12,
        };
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(columnsGrid, 1);
        layoutGrid.Children.Add(headerGrid);
        layoutGrid.Children.Add(columnsGrid);

        // 悬停文本提示层
        _hintText = new TextBlock { FontSize = 11, Foreground = _textBrush };
        _hintPill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Background = _hintBgBrush,
            BorderBrush = _paneBorderBrush,
            BorderThickness = new Thickness(1),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = _hintText,
        };
        _hintCanvas = new Canvas
        {
            IsHitTestVisible = false,
            Children = { _hintPill },
        };

        Content = new Grid
        {
            Children = { layoutGrid, _hintCanvas },
        };

        _audioService.CacheUpdated += OnAudioCacheUpdated;

        // 每 800ms 刷新一次音量合成器发声状态
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(800);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) =>
        {
            UpdateTimerLabel();
            if (!_isDraggingSlider)
            {
                _audioService.RequestRefreshMixer();
            }
        };

        // Quietify 5柱发声角标逐帧波形定时器（约 20fps，逐帧赋值，不使用 Storyboard）
        _waveTimer = DispatcherQueue.CreateTimer();
        _waveTimer.Interval = TimeSpan.FromMilliseconds(50);
        _waveTimer.IsRepeating = true;
        _waveTimer.Tick += (_, _) => OnWaveTick();

        Loaded += (_, _) =>
        {
            _isLoaded = true;
            RenderChangedSectionsFromCache();
            _timer.Start();
            _waveTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            _timer.Stop();
            _waveTimer.Stop();
            HideHint();
        };
    }

    public void OnHostClosed()
    {
        _isLoaded = false;
        _timer.Stop();
        _waveTimer.Stop();
        HideHint();
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

    private Border BuildColumnCard(
        string title,
        string glyph,
        StackPanel listPanel,
        string settingsUri,
        string settingsTooltip)
    {
        var titleLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 13, Foreground = _accentBrush, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _textBrush, VerticalAlignment = VerticalAlignment.Center },
            },
        };

        var settingsBtn = new Button
        {
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uE8A7", FontSize = 11.5, Foreground = _mutedBrush },
        };
        settingsBtn.PointerEntered += (_, _) => settingsBtn.Background = _itemHoverBrush;
        settingsBtn.PointerExited += (_, _) => settingsBtn.Background = new SolidColorBrush(Colors.Transparent);
        AttachCursorHint(settingsBtn, () => settingsTooltip);
        settingsBtn.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(settingsUri) { UseShellExecute = true });
            }
            catch { }
        };

        var header = new Grid { Padding = new Thickness(2, 0, 2, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleLeft, 0);
        Grid.SetColumn(settingsBtn, 1);
        header.Children.Add(titleLeft);
        header.Children.Add(settingsBtn);

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
            bool playing = _hasAnyPlayingProvider();
            _timerStatusText.Text = playing
                ? $"定时暂停进行中：剩余 {(int)r.TotalMinutes:D2}:{r.Seconds:D2}"
                : $"定时进行中（剩余 {(int)r.TotalMinutes:D2}:{r.Seconds:D2}）：当前无播放，到期若仍无播放将替换为触发播放";
        }
        else
        {
            _timerStatusText.Text = "定时暂停：未开启";
        }
    }

    // ==================== 1. 左列：音频输出设备（区分 HDMI / 耳机 / 扬声器） ====================

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
                Glyph = dev.Glyph, // HDMI 显示器为 \uE7F4，耳机为 \uE7F6，扬声器为 \uE7F5
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
                        Foreground = _whiteBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
            }

            var nameText = new TextBlock
            {
                Text = dev.Name,
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

    // ==================== 2. 中列：音量合成器（程序真图标 + Quietify 发声/静音角标） ====================

    private void RenderMixerSection(IReadOnlyList<MixerSessionInfo> sessions)
    {
        _mixerPanel.Children.Clear();
        _activeBadges.Clear();

        if (sessions.Count == 0)
        {
            _mixerPanel.Children.Add(EmptyHint("暂无发声程序"));
            return;
        }

        foreach (var s in sessions)
        {
            int pid = s.ProcessId;
            bool muted = s.IsMuted;
            bool generating = s.IsGeneratingSound;

            // 应用程序图标 + 右上角 Quietify 状态角标
            var appImg = new Image
            {
                Width = 20,
                Height = 20,
                Stretch = Stretch.UniformToFill,
            };
            var fallbackIcon = new FontIcon
            {
                Glyph = "\uE767",
                FontSize = 13,
                Foreground = _textBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            LoadOrBindAppIcon(s.ProcessName, s.AppIconBytes, appImg, fallbackIcon);

            var badgeCtrl = CreateQuietifyBadge(muted, generating);
            _activeBadges.Add(badgeCtrl);

            // 以程序图标为中心的悬停阴影框（26x26 居中包裹 20x20 图标），右上角角标超出该阴影框显示
            var iconHoverBox = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Grid
                {
                    Width = 20,
                    Height = 20,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { fallbackIcon, appImg },
                },
            };

            badgeCtrl.BadgeHost.Margin = new Thickness(0, -2, -1, 0);

            var muteHost = new Grid
            {
                Width = 32,
                Height = 28,
                Background = new SolidColorBrush(Colors.Transparent),
                Children = { iconHoverBox, badgeCtrl.BadgeHost },
            };
            muteHost.PointerEntered += (_, _) => iconHoverBox.Background = _itemHoverBrush;
            muteHost.PointerExited += (_, _) => iconHoverBox.Background = new SolidColorBrush(Colors.Transparent);
            AttachCursorHint(muteHost, () => "静音该应用");

            var nameText = new TextBlock
            {
                Text = s.DisplayName,
                FontSize = 12,
                Foreground = _textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = 84,
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
                    badgeCtrl.IsMuted = false;
                    _audioService.SetSessionMute(pid, false);
                    ApplyBadgeStaticState(badgeCtrl);
                }
                pctText.Text = $"{(int)Math.Round(e.NewValue)}%";
                _isDraggingSlider = false;
            };

            muteHost.Tapped += (_, e) =>
            {
                e.Handled = true;
                muted = !muted;
                badgeCtrl.IsMuted = muted;
                _audioService.SetSessionMute(pid, muted);
                ApplyBadgeStaticState(badgeCtrl);
                pctText.Text = muted ? "静音" : $"{(int)Math.Round(slider.Value)}%";
            };

            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(muteHost, 0);
            Grid.SetColumn(nameText, 1);
            Grid.SetColumn(slider, 2);
            Grid.SetColumn(pctText, 3);
            row.Children.Add(muteHost);
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

        OnWaveTick();
    }

    /// <summary>
    /// 构建 Quietify 开源 SVG 同款 5 柱发声 / 5 点静音角标（viewBox 0 0 135 140，5 根宽 15、圆角 6 的矩形）。
    /// </summary>
    private QuietifyBadgeController CreateQuietifyBadge(bool isMuted, bool isGeneratingSound)
    {
        var canvas = new Canvas { Width = 135, Height = 140 };
        var bars = new Rectangle[5];
        double[] xs = [0, 30, 60, 90, 120];

        for (int i = 0; i < 5; i++)
        {
            var rect = new Rectangle
            {
                Width = 15,
                Height = 15,
                RadiusX = 6,
                RadiusY = 6,
                Fill = _whiteBrush,
            };
            Canvas.SetLeft(rect, xs[i]);
            Canvas.SetTop(rect, 70);
            canvas.Children.Add(rect);
            bars[i] = rect;
        }

        var viewbox = new Viewbox
        {
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Child = canvas,
        };

        // 移除圆形灰色底色，保持背景完全透明
        var host = new Border
        {
            Width = 14,
            Height = 14,
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -1, -1, 0),
            Child = viewbox,
        };

        var ctrl = new QuietifyBadgeController
        {
            BadgeHost = host,
            Bars = bars,
            IsMuted = isMuted,
            IsGeneratingSound = isGeneratingSound,
        };
        ApplyBadgeStaticState(ctrl);
        return ctrl;
    }

    private void ApplyBadgeStaticState(QuietifyBadgeController ctrl)
    {
        if (ctrl.IsMuted)
        {
            // 静音 SVG：5 个圆点 (y=70, height=15, rx=6)，背景透明
            ctrl.BadgeHost.Opacity = 1;
            for (int i = 0; i < 5; i++)
            {
                ctrl.Bars[i].Fill = _textBrush;
                ctrl.Bars[i].Height = 15;
                Canvas.SetTop(ctrl.Bars[i], 70);
            }
        }
        else if (ctrl.IsGeneratingSound)
        {
            ctrl.BadgeHost.Opacity = 1;
            for (int i = 0; i < 5; i++)
            {
                ctrl.Bars[i].Fill = _accentBrush;
            }
        }
        else
        {
            ctrl.BadgeHost.Opacity = 0;
        }
    }

    private void OnWaveTick()
    {
        if (_activeBadges.Count == 0) return;
        double sec = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        foreach (var ctrl in _activeBadges)
        {
            if (ctrl.IsMuted || !ctrl.IsGeneratingSound) continue;

            for (int i = 0; i < 5; i++)
            {
                // 还原 SVG <animate dur="1s" begin="0.5s / 0.25s / 0s" values="120;110;...;140;120" />
                double phase = ((sec - BarPhaseOffsets[i]) % 1.0 + 1.0) % 1.0;
                double pos = phase * (WaveHeights.Length - 1);
                int idx = Math.Clamp((int)pos, 0, WaveHeights.Length - 2);
                double frac = pos - idx;

                double h = WaveHeights[idx] + (WaveHeights[idx + 1] - WaveHeights[idx]) * frac;
                double y = WaveYs[idx] + (WaveYs[idx + 1] - WaveYs[idx]) * frac;

                ctrl.Bars[i].Height = h;
                Canvas.SetTop(ctrl.Bars[i], y);
            }
        }
    }

    private async void LoadOrBindAppIcon(string processName, byte[]? iconBytes, Image targetImg, FontIcon fallback)
    {
        if (string.IsNullOrWhiteSpace(processName) || iconBytes is null || iconBytes.Length == 0)
        {
            targetImg.Source = null;
            fallback.Opacity = 1;
            return;
        }

        if (_appIconBitmaps.TryGetValue(processName, out var cachedBmp))
        {
            targetImg.Source = cachedBmp;
            fallback.Opacity = 0;
            return;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(iconBytes.AsBuffer());
            stream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            _appIconBitmaps[processName] = bmp;
            targetImg.Source = bmp;
            fallback.Opacity = 0;
        }
        catch
        {
            fallback.Opacity = 1;
        }
    }

    // ==================== 3. 右列：Windows 空间音效 ====================

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

    private void AttachCursorHint(UIElement target, Func<string> textProvider)
    {
        target.PointerEntered += (_, e) => UpdateCursorHint(textProvider(), e);
        target.PointerMoved += (_, e) => UpdateCursorHint(textProvider(), e);
        target.PointerExited += (_, _) => HideHint();
    }

    private void UpdateCursorHint(string text, PointerRoutedEventArgs e)
    {
        _hintText.Text = text;
        var pt = e.GetCurrentPoint(_hintCanvas).Position;
        double maxX = Math.Max(4, _hintCanvas.ActualWidth - 110);
        double maxY = Math.Max(4, _hintCanvas.ActualHeight - 26);
        Canvas.SetLeft(_hintPill, Math.Clamp(pt.X, 4, maxX));
        Canvas.SetTop(_hintPill, Math.Clamp(pt.Y, 4, maxY));
        _hintPill.Opacity = 1;
    }

    private void HideHint() => _hintPill.Opacity = 0;

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
        _hintBgBrush.Color = light ? Color.FromArgb(242, 255, 255, 255) : Color.FromArgb(238, 24, 28, 36);
        _badgeBgBrush.Color = Color.FromArgb(235, 14, 165, 233);
    }

    private Color Neutral(byte alpha) => _theme.IsLight
        ? Color.FromArgb(alpha, 0, 0, 0)
        : Color.FromArgb(alpha, 255, 255, 255);
}
