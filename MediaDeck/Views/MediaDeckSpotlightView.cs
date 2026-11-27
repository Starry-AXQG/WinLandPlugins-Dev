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
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;
using WinIsland.Core;

namespace MediaDeck.Views;

/// <summary>
/// 「声枢」现代化聚光卡（支持宽松双页视图与紧凑单页视图）：
///   - 宽松视图（默认）：
///       · 标题栏居中显示页面指示器，右侧为「刷新」+「下一页/上一页」按钮；
///       · 点击翻页按钮、点击指示器或在标题栏区域滚动滚轮均可平滑翻页（下方控制区滚轮保持正常列表滚动）；
///       · 首页完整展示三列音频控制容器，第二页完整展示全部媒体播放实例卡片；每次打开固定回到首页。
///   - 紧凑视图（设置可选）：
///       · 单页上下同时展示三列音频控制容器与可滚动媒体播放实例列表。
/// </summary>
public sealed class MediaDeckSpotlightView : UserControl
{
    // Quietify SVG 动画关键帧（viewBox 0 0 135 140）
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
    private readonly Func<IReadOnlyList<(MediaInstanceInfo Info, LyricTriplet Lyrics)>> _mediaSnapshotProvider;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _waveTimer;
    private readonly DispatcherQueueTimer _cardVisualTimer;
    private readonly DispatcherQueueTimer _pageAnimTimer;
    private readonly DispatcherQueueTimer _hintDelayTimer;
    private readonly Dictionary<string, BitmapImage> _appIconBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<QuietifyBadgeController> _activeBadges = [];
    private readonly Dictionary<string, SpotlightMediaCard> _mediaCards = new(StringComparer.OrdinalIgnoreCase);

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
    private readonly SolidColorBrush _accentSoftBgBrush = new();
    private readonly SolidColorBrush _accentBrush = new(Color.FromArgb(255, 14, 165, 233));
    private readonly SolidColorBrush _btBlueBrush = new(Color.FromArgb(255, 24, 132, 255));
    private readonly SolidColorBrush _whiteBrush = new(Colors.White);

    private readonly StackPanel _devicesPanel;
    private readonly StackPanel _mixerPanel;
    private readonly StackPanel _spatialPanel;
    private readonly StackPanel _mediaCardsPanel;
    private readonly TextBlock _mediaCountBadgeText;

    // 布局与翻页控件
    private readonly Grid _bodyGrid;
    private readonly RowDefinition _audioRowDef;
    private readonly RowDefinition _mediaRowDef;
    private readonly Grid _columnsGrid;
    private readonly Grid _mediaSection;
    private readonly Border _pageDotsHost;
    private readonly Border _pageDot0;
    private readonly Border _pageDot1;
    private readonly Button _pageSwitchBtn;
    private readonly FontIcon _pageSwitchLeftGlyph;
    private readonly TextBlock _pageSwitchLabel;
    private readonly FontIcon _pageSwitchRightGlyph;
    private readonly CompositeTransform _bodySlideTx = new();

    private bool _compactView;
    private int _currentPage; // 0 = 音频控制首页, 1 = 媒体实例页
    private DateTimeOffset _pageAnimStart;
    private double _pageAnimFromX;

    // 延迟 1000ms 显示的固定锚点悬停提示层（离开按钮才隐藏）
    private readonly Canvas _hintCanvas;
    private readonly Border _hintPill;
    private readonly TextBlock _hintText;
    private UIElement? _hintTarget;
    private Func<string>? _hintTextProvider;
    private Point _hintPendingPt;
    private bool _hintVisible;

    // 视觉配置（与主卡片同步）
    private VisualEffectMode _visualMode = VisualEffectMode.AuroraWave;
    private double _contourReactivity = 0.50;
    private bool _cursorGlowEnabled = true;
    private double _cursorGlowSize = 96.0;
    private double _cursorGlowOpacity = 0.30;

    private bool _isDraggingSlider;
    private bool _isLoaded;
    private long _renderedDevicesVer = -1;
    private long _renderedMixerVer = -1;
    private long _renderedSpatialVer = -1;

    public event Action<string>? ActivateSourceRequested;
    public event Action<string>? MinimizeSourceRequested;
    public event Action<string>? PlayPauseRequested;
    public event Action<string>? PreviousRequested;
    public event Action<string>? NextRequested;
    public event Action<string, double>? SeekRatioRequested;
    public event Action<string, int>? SetSleepTimerRequested;

    public MediaDeckSpotlightView(
        IIslandTheme theme,
        AudioDeviceService audioService,
        SleepTimerService sleepTimer,
        Func<IReadOnlyList<(MediaInstanceInfo Info, LyricTriplet Lyrics)>> mediaSnapshotProvider)
    {
        _theme = theme;
        _audioService = audioService;
        _sleepTimer = sleepTimer;
        _mediaSnapshotProvider = mediaSnapshotProvider;

        ApplyThemeColors();
        _theme.Changed += OnThemeChanged;

        // ==================== 顶部栏：左侧品牌 + 中间页面指示器 + 右侧刷新与翻页 ====================
        var brandBadge = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(10),
            Background = _accentSoftBgBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "\uE8D6",
                FontSize = 15,
                Foreground = _accentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var titleStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "声枢",
                    FontSize = 15.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _textBrush,
                },
                new TextBlock
                {
                    Text = "媒体与音频控制中心",
                    FontSize = 11,
                    Foreground = _mutedBrush,
                },
            },
        };

        var headerLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { brandBadge, titleStack },
        };

        // 顶部居中页面指示器（宽松视图可见）
        _pageDot0 = new Border
        {
            Width = 18,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = _accentBrush,
            Opacity = 1,
        };
        _pageDot0.Tapped += (_, e) => { e.Handled = true; SwitchPage(0, animate: true); };

        _pageDot1 = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = _faintBrush,
            Opacity = 0.5,
        };
        _pageDot1.Tapped += (_, e) => { e.Handled = true; SwitchPage(1, animate: true); };

        var dotsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _pageDot0, _pageDot1 },
        };

        _pageDotsHost = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = dotsStack,
        };
        AttachCursorHint(_pageDotsHost, () => "点击指示器或在标题栏滚动滚轮切换页面（音频控制 / 媒体实例）");

        // 刷新按钮（在宽松视图下位于翻页按钮左侧）
        var refreshBtn = new Button
        {
            Padding = new Thickness(10, 5, 10, 5),
            CornerRadius = new CornerRadius(10),
            Background = _itemBgBrush,
            BorderBrush = _paneBorderBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new FontIcon { Glyph = "\uE72C", FontSize = 12, Foreground = _textBrush, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "刷新", FontSize = 11.5, Foreground = _textBrush, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        refreshBtn.PointerEntered += (_, _) => refreshBtn.Background = _itemHoverBrush;
        refreshBtn.PointerExited += (_, _) => refreshBtn.Background = _itemBgBrush;
        refreshBtn.Click += (_, _) => _audioService.RequestRefreshAll();
        AttachCursorHint(refreshBtn, () => "立即刷新音频输出设备、音量合成器与空间音效");

        // 下一页 / 上一页按钮（宽松视图可见）
        _pageSwitchLeftGlyph = new FontIcon
        {
            Glyph = "\uE76B",
            FontSize = 11,
            Foreground = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _pageSwitchLabel = new TextBlock
        {
            Text = "下一页",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pageSwitchRightGlyph = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 11,
            Foreground = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pageSwitchBtn = new Button
        {
            Padding = new Thickness(10, 5, 10, 5),
            CornerRadius = new CornerRadius(10),
            Background = _accentSoftBgBrush,
            BorderBrush = _paneBorderBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { _pageSwitchLeftGlyph, _pageSwitchLabel, _pageSwitchRightGlyph },
            },
        };
        _pageSwitchBtn.PointerEntered += (_, _) => _pageSwitchBtn.Background = _itemSelectedBgBrush;
        _pageSwitchBtn.PointerExited += (_, _) => _pageSwitchBtn.Background = _accentSoftBgBrush;
        _pageSwitchBtn.Click += (_, _) => SwitchPage(_currentPage == 0 ? 1 : 0, animate: true);
        AttachCursorHint(_pageSwitchBtn, () => _currentPage == 0 ? "切换到第 2 页：查看全部媒体播放实例" : "返回第 1 页：音频输出、音量合成器与空间音效");

        var headerRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { refreshBtn, _pageSwitchBtn },
        };

        var headerGrid = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent), // 让整个标题栏区域均可响应滚轮翻页
            Children = { headerLeft, _pageDotsHost, headerRight },
        };
        headerGrid.PointerWheelChanged += OnHeaderPointerWheelChanged;

        // ==================== 第 1 页 / 上半区：三列独立现代音频控制容器 ====================
        _devicesPanel = new StackPanel { Spacing = 4 };
        _mixerPanel = new StackPanel { Spacing = 4 };
        _spatialPanel = new StackPanel { Spacing = 4 };

        var colDevices = BuildColumnCard("音频输出设备", "\uE7F6", _devicesPanel, "ms-settings:sound", "打开 Windows 音频输出设备设置");
        var colMixer = BuildColumnCard("音量合成器", "\uE995", _mixerPanel, "ms-settings:apps-volume", "打开 Windows 音量合成器设置");
        var colSpatial = BuildColumnCard("空间音效", "\uE909", _spatialPanel, "ms-settings:sound-defaultoutputproperties", "打开 Windows 空间音效设置");

        _columnsGrid = new Grid
        {
            ColumnSpacing = 12,
        };
        _columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.08, GridUnitType.Star) });
        _columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.32, GridUnitType.Star) });
        _columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.92, GridUnitType.Star) });

        Grid.SetColumn(colDevices, 0);
        Grid.SetColumn(colMixer, 1);
        Grid.SetColumn(colSpatial, 2);
        _columnsGrid.Children.Add(colDevices);
        _columnsGrid.Children.Add(colMixer);
        _columnsGrid.Children.Add(colSpatial);

        // ==================== 第 2 页 / 下半区：全部媒体播放实例（一行一张，可滚动） ====================
        _mediaCountBadgeText = new TextBlock
        {
            Text = "0 个实例",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var countBadge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Background = _accentSoftBgBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _mediaCountBadgeText,
        };

        var mediaHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(2, 2, 2, 0),
            Children =
            {
                new FontIcon { Glyph = "\uE8D6", FontSize = 13, Foreground = _accentBrush, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = "媒体播放实例",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _textBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                countBadge,
            },
        };

        _mediaCardsPanel = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(0, 0, 4, 4),
        };

        var mediaScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _mediaCardsPanel,
        };

        _mediaSection = new Grid { RowSpacing = 8 };
        _mediaSection.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _mediaSection.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(mediaHeader, 0);
        Grid.SetRow(mediaScroll, 1);
        _mediaSection.Children.Add(mediaHeader);
        _mediaSection.Children.Add(mediaScroll);

        // ==================== 主体容器（支持紧凑单页 / 宽松双页无缝切换） ====================
        _audioRowDef = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
        _mediaRowDef = new RowDefinition { Height = new GridLength(0) };
        _bodyGrid = new Grid
        {
            RowSpacing = 0,
            RenderTransform = _bodySlideTx,
        };
        _bodyGrid.RowDefinitions.Add(_audioRowDef);
        _bodyGrid.RowDefinitions.Add(_mediaRowDef);
        Grid.SetRow(_columnsGrid, 0);
        Grid.SetRow(_mediaSection, 1);
        _bodyGrid.Children.Add(_columnsGrid);
        _bodyGrid.Children.Add(_mediaSection);

        var layoutGrid = new Grid
        {
            Padding = new Thickness(20, 14, 20, 16),
            RowSpacing = 12,
        };
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(_bodyGrid, 1);
        layoutGrid.Children.Add(headerGrid);
        layoutGrid.Children.Add(_bodyGrid);

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
            if (!_isDraggingSlider)
            {
                _audioService.RequestRefreshMixer();
            }
        };

        // Quietify 5柱发声角标逐帧波形定时器
        _waveTimer = DispatcherQueue.CreateTimer();
        _waveTimer.Interval = TimeSpan.FromMilliseconds(50);
        _waveTimer.IsRepeating = true;
        _waveTimer.Tick += (_, _) => OnWaveTick();

        // 聚光卡媒体卡片 30fps 视觉特效定时器（仅聚光卡打开时运行）
        _cardVisualTimer = DispatcherQueue.CreateTimer();
        _cardVisualTimer.Interval = TimeSpan.FromMilliseconds(33);
        _cardVisualTimer.IsRepeating = true;
        _cardVisualTimer.Tick += (_, _) => OnCardVisualTick();

        // 翻页滑动动画定时器
        _pageAnimTimer = DispatcherQueue.CreateTimer();
        _pageAnimTimer.Interval = TimeSpan.FromMilliseconds(16);
        _pageAnimTimer.IsRepeating = true;
        _pageAnimTimer.Tick += (_, _) => OnPageAnimTick();

        // 悬停提示 1000ms 延迟触发定时器
        _hintDelayTimer = DispatcherQueue.CreateTimer();
        _hintDelayTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _hintDelayTimer.IsRepeating = false;
        _hintDelayTimer.Tick += (_, _) => OnHintDelayElapsed();

        ApplyLayoutMode(resetToHomePage: true);

        Loaded += (_, _) =>
        {
            _isLoaded = true;
            // 每次打开聚光卡都固定显示首页，并后台即时校验一次输出设备与合成器缓存
            ApplyLayoutMode(resetToHomePage: true);
            RenderChangedSectionsFromCache();
            RefreshMediaInstances();
            _audioService.RequestRefreshDevices();
            _audioService.RequestRefreshMixer();
            _timer.Start();
            _waveTimer.Start();
            _cardVisualTimer.Start();
        };
        Unloaded += (_, _) => OnHostClosed();
    }

    public void OnHostClosed()
    {
        _isLoaded = false;
        _timer.Stop();
        _waveTimer.Stop();
        _cardVisualTimer.Stop();
        _pageAnimTimer.Stop();
        _hintDelayTimer.Stop();
        foreach (var card in _mediaCards.Values)
        {
            card.CloseAlarmPopup();
        }
        HideHint();
    }

    public void SetCompactView(bool compact)
    {
        if (_compactView == compact) return;
        _compactView = compact;
        ApplyLayoutMode(resetToHomePage: true);
    }

    public void SetVisualConfig(VisualEffectMode mode, double contourReactivity, bool cursorGlowEnabled, double cursorGlowSize, double cursorGlowOpacity)
    {
        _visualMode = mode;
        _contourReactivity = Math.Clamp(contourReactivity, 0.0, 5.0);
        _cursorGlowEnabled = cursorGlowEnabled;
        _cursorGlowSize = Math.Clamp(cursorGlowSize, 40.0, 320.0);
        _cursorGlowOpacity = Math.Clamp(cursorGlowOpacity, 0.0, 1.0);

        foreach (var card in _mediaCards.Values)
        {
            card.SetVisualConfig(_visualMode, _contourReactivity, _cursorGlowEnabled, _cursorGlowSize, _cursorGlowOpacity);
        }
    }

    private void ApplyLayoutMode(bool resetToHomePage)
    {
        if (resetToHomePage)
        {
            _currentPage = 0;
        }
        _pageAnimTimer.Stop();
        _bodySlideTx.TranslateX = 0;
        _bodyGrid.Opacity = 1;

        if (_compactView)
        {
            // 紧凑视图：隐藏页面指示器与翻页按钮，同屏上下展示三列音频控制与媒体实例列表
            _pageDotsHost.Visibility = Visibility.Collapsed;
            _pageSwitchBtn.Visibility = Visibility.Collapsed;

            _bodyGrid.RowSpacing = 12;
            _audioRowDef.Height = GridLength.Auto;
            _mediaRowDef.Height = new GridLength(1, GridUnitType.Star);
            _columnsGrid.Height = 236;
            _columnsGrid.Visibility = Visibility.Visible;
            _mediaSection.Visibility = Visibility.Visible;
        }
        else
        {
            // 宽松视图：顶部居中页面指示器 + 翻页按钮，每页独立占满整个主体区域
            _pageDotsHost.Visibility = Visibility.Visible;
            _pageSwitchBtn.Visibility = Visibility.Visible;

            _bodyGrid.RowSpacing = 0;
            _columnsGrid.Height = double.NaN;
            ApplySpaciousPageVisibility();
        }
    }

    private void ApplySpaciousPageVisibility()
    {
        bool isHome = _currentPage == 0;
        _audioRowDef.Height = isHome ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _mediaRowDef.Height = isHome ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _columnsGrid.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        _mediaSection.Visibility = isHome ? Visibility.Collapsed : Visibility.Visible;

        _pageDot0.Width = isHome ? 18 : 6;
        _pageDot0.Background = isHome ? _accentBrush : _faintBrush;
        _pageDot0.Opacity = isHome ? 1 : 0.48;

        _pageDot1.Width = isHome ? 6 : 18;
        _pageDot1.Background = isHome ? _faintBrush : _accentBrush;
        _pageDot1.Opacity = isHome ? 0.48 : 1;

        _pageSwitchLeftGlyph.Visibility = isHome ? Visibility.Collapsed : Visibility.Visible;
        _pageSwitchRightGlyph.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        _pageSwitchLabel.Text = isHome ? "下一页" : "上一页";
    }

    private void SwitchPage(int targetPage, bool animate)
    {
        if (_compactView) return;
        int clamped = Math.Clamp(targetPage, 0, 1);
        if (clamped == _currentPage) return;

        int dir = clamped > _currentPage ? 1 : -1;
        _currentPage = clamped;
        HideHint();
        foreach (var card in _mediaCards.Values)
        {
            card.CloseAlarmPopup();
        }
        ApplySpaciousPageVisibility();

        if (animate)
        {
            _pageAnimFromX = dir > 0 ? 38 : -38;
            _pageAnimStart = DateTimeOffset.UtcNow;
            _bodySlideTx.TranslateX = _pageAnimFromX;
            _bodyGrid.Opacity = 0.25;
            _pageAnimTimer.Start();
        }
    }

    private void OnPageAnimTick()
    {
        const double durationMs = 200;
        double t = Math.Clamp((DateTimeOffset.UtcNow - _pageAnimStart).TotalMilliseconds / durationMs, 0, 1);
        double ease = 1 - Math.Pow(1 - t, 3);
        _bodySlideTx.TranslateX = _pageAnimFromX * (1 - ease);
        _bodyGrid.Opacity = 0.25 + 0.75 * ease;
        if (t >= 1)
        {
            _pageAnimTimer.Stop();
            _bodySlideTx.TranslateX = 0;
            _bodyGrid.Opacity = 1;
        }
    }

    private void OnHeaderPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_compactView) return;
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;

        e.Handled = true;
        int target = delta < 0 ? 1 : 0;
        SwitchPage(target, animate: true);
    }

    public void RefreshAllSections()
    {
        if (!_isLoaded) return;
        RenderChangedSectionsFromCache();
    }

    public void RefreshMediaInstances()
    {
        if (!_isLoaded) return;

        var snapshot = _mediaSnapshotProvider();
        _mediaCountBadgeText.Text = $"{snapshot.Count} 个实例";

        if (snapshot.Count == 0)
        {
            if (_mediaCards.Count > 0 || _mediaCardsPanel.Children.Count == 0)
            {
                _mediaCards.Clear();
                _mediaCardsPanel.Children.Clear();
                _mediaCardsPanel.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(14),
                    Background = _paneBgBrush,
                    BorderBrush = _paneBorderBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(20, 36, 20, 36),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            new FontIcon { Glyph = "\uE8D6", FontSize = 24, Foreground = _faintBrush, HorizontalAlignment = HorizontalAlignment.Center },
                            new TextBlock { Text = "当前没有正在运行的媒体播放实例", FontSize = 12.5, Foreground = _mutedBrush, HorizontalAlignment = HorizontalAlignment.Center },
                        },
                    },
                });
            }
            return;
        }

        if (_mediaCards.Count == 0 && _mediaCardsPanel.Children.Count > 0)
        {
            _mediaCardsPanel.Children.Clear();
        }

        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var desiredOrder = new List<SpotlightMediaCard>(snapshot.Count);

        foreach (var (info, lyrics) in snapshot)
        {
            activeIds.Add(info.Id);
            if (!_mediaCards.TryGetValue(info.Id, out var card))
            {
                card = new SpotlightMediaCard(_theme, info.Id, AttachCursorHint, HideHint);
                card.SetVisualConfig(_visualMode, _contourReactivity, _cursorGlowEnabled, _cursorGlowSize, _cursorGlowOpacity);
                card.ActivateSourceRequested += id => ActivateSourceRequested?.Invoke(id);
                card.MinimizeSourceRequested += id => MinimizeSourceRequested?.Invoke(id);
                card.PlayPauseRequested += id => PlayPauseRequested?.Invoke(id);
                card.PreviousRequested += id => PreviousRequested?.Invoke(id);
                card.NextRequested += id => NextRequested?.Invoke(id);
                card.SeekRatioRequested += (id, ratio) => SeekRatioRequested?.Invoke(id, ratio);
                card.SetSleepTimerRequested += (id, mins) => SetSleepTimerRequested?.Invoke(id, mins);
                _mediaCards[info.Id] = card;
            }

            bool hasTimer = _sleepTimer.IsActive(info.Id);
            TimeSpan remaining = _sleepTimer.GetRemaining(info.Id);
            card.UpdateData(info, lyrics, hasTimer, remaining);
            desiredOrder.Add(card);
        }

        var toRemove = _mediaCards.Keys.Where(id => !activeIds.Contains(id)).ToList();
        foreach (var id in toRemove)
        {
            if (_mediaCards.Remove(id, out var removed))
            {
                _mediaCardsPanel.Children.Remove(removed);
            }
        }

        bool orderMatches = _mediaCardsPanel.Children.Count == desiredOrder.Count;
        if (orderMatches)
        {
            for (int i = 0; i < desiredOrder.Count; i++)
            {
                if (!ReferenceEquals(_mediaCardsPanel.Children[i], desiredOrder[i]))
                {
                    orderMatches = false;
                    break;
                }
            }
        }

        if (!orderMatches)
        {
            _mediaCardsPanel.Children.Clear();
            foreach (var card in desiredOrder)
            {
                _mediaCardsPanel.Children.Add(card);
            }
        }
    }

    public void RefreshSleepTimersOnly()
    {
        if (!_isLoaded) return;
        foreach (var kv in _mediaCards)
        {
            kv.Value.UpdateTimerDisplay(_sleepTimer.IsActive(kv.Key), _sleepTimer.GetRemaining(kv.Key));
        }
    }

    private void OnCardVisualTick()
    {
        if (!_isLoaded || _mediaCards.Count == 0) return;
        if (!_compactView && _currentPage != 1) return; // 宽松视图停在首页时跳过第二页卡片动画，零额外开销
        var peak = _audioService.GetCurrentAudioPeak();
        foreach (var card in _mediaCards.Values)
        {
            card.TickVisual(peak);
        }
    }

    private void OnAudioCacheUpdated()
    {
        if (!_isLoaded || _isDraggingSlider) return;
        DispatcherQueue?.TryEnqueue(RenderChangedSectionsFromCache);
    }

    private void RenderChangedSectionsFromCache()
    {
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
        var iconPill = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(7),
            Background = _accentSoftBgBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 11.5,
                Foreground = _accentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var titleLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                iconPill,
                new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = _textBrush, VerticalAlignment = VerticalAlignment.Center },
            },
        };

        var settingsBtn = new Button
        {
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(7),
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
            try { Process.Start(new ProcessStartInfo(settingsUri) { UseShellExecute = true }); }
            catch { }
        };

        var header = new Grid { Padding = new Thickness(2, 0, 2, 7) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleLeft, 0);
        Grid.SetColumn(settingsBtn, 1);
        header.Children.Add(titleLeft);
        header.Children.Add(settingsBtn);

        var divider = new Border { Height = 1, Background = _dividerBrush, Margin = new Thickness(0, 0, 0, 7) };

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
            CornerRadius = new CornerRadius(14),
            Background = _paneBgBrush,
            BorderBrush = _paneBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 10, 11, 10),
            Child = inner,
        };
    }

    // ==================== 1. 左列：音频输出设备 ====================

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
                Glyph = dev.Glyph,
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

    // ==================== 2. 中列：音量合成器 ====================

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

            var appImg = new Image { Width = 20, Height = 20, Stretch = Stretch.UniformToFill };
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
                CornerRadius = new CornerRadius(9),
                Background = _itemBgBrush,
                Padding = new Thickness(6, 3, 8, 3),
                Child = row,
            });
        }

        OnWaveTick();
    }

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

        var host = new Border
        {
            Width = 14,
            Height = 14,
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -1, -1, 0),
            Child = new Viewbox { Width = 12, Height = 12, Stretch = Stretch.Uniform, Child = canvas },
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
                double phase = ((sec - BarPhaseOffsets[i]) % 1.0 + 1.0) % 1.0;
                double pos = phase * (WaveHeights.Length - 1);
                int idx = Math.Clamp((int)pos, 0, WaveHeights.Length - 2);
                double frac = pos - idx;

                ctrl.Bars[i].Height = WaveHeights[idx] + (WaveHeights[idx + 1] - WaveHeights[idx]) * frac;
                Canvas.SetTop(ctrl.Bars[i], WaveYs[idx] + (WaveYs[idx + 1] - WaveYs[idx]) * frac);
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

    // ==================== 延迟 1000ms 固定锚点悬停提示（离开按钮隐藏） ====================

    private void AttachCursorHint(UIElement target, Func<string> textProvider)
    {
        target.PointerEntered += (_, e) => ScheduleCursorHint(target, textProvider, e);
        target.PointerMoved += (_, e) =>
        {
            if (ReferenceEquals(_hintTarget, target) && !_hintVisible)
            {
                _hintPendingPt = e.GetCurrentPoint(_hintCanvas).Position;
            }
        };
        target.PointerExited += (_, _) =>
        {
            if (ReferenceEquals(_hintTarget, target))
            {
                _hintTarget = null;
                _hintTextProvider = null;
                _hintDelayTimer.Stop();
                HideHint();
            }
        };
    }

    private void ScheduleCursorHint(UIElement target, Func<string> textProvider, PointerRoutedEventArgs e)
    {
        _hintTarget = target;
        _hintTextProvider = textProvider;
        _hintPendingPt = e.GetCurrentPoint(_hintCanvas).Position;
        HideHint();
        _hintDelayTimer.Stop();
        _hintDelayTimer.Start();
    }

    private void OnHintDelayElapsed()
    {
        if (!_isLoaded || _hintTarget is null || _hintTextProvider is null)
        {
            HideHint();
            return;
        }

        _hintText.Text = _hintTextProvider();
        _hintPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double pillW = _hintPill.DesiredSize.Width > 0 ? _hintPill.DesiredSize.Width : 100;
        double pillH = _hintPill.DesiredSize.Height > 0 ? _hintPill.DesiredSize.Height : 24;
        double canvasW = _hintCanvas.ActualWidth > 10 ? _hintCanvas.ActualWidth : 880;
        double canvasH = _hintCanvas.ActualHeight > 10 ? _hintCanvas.ActualHeight : 500;

        double x = _hintPendingPt.X + 7;
        double y = _hintPendingPt.Y + 11;

        if (x + pillW > canvasW - 6)
        {
            x = Math.Max(6, canvasW - pillW - 6);
        }
        if (y + pillH > canvasH - 6)
        {
            y = Math.Max(6, _hintPendingPt.Y - pillH - 8);
        }

        Canvas.SetLeft(_hintPill, Math.Clamp(x, 6, Math.Max(6, canvasW - pillW - 6)));
        Canvas.SetTop(_hintPill, Math.Clamp(y, 6, Math.Max(6, canvasH - pillH - 6)));
        _hintPill.Opacity = 1;
        _hintVisible = true;
    }

    private void HideHint()
    {
        _hintDelayTimer.Stop();
        _hintPill.Opacity = 0;
        _hintVisible = false;
    }

    private Button CreateCompactRowButton(UIElement content, bool isSelected, Thickness padding)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = padding,
            CornerRadius = new CornerRadius(9),
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

    private void OnThemeChanged()
    {
        ApplyThemeColors();
        foreach (var card in _mediaCards.Values)
        {
            card.ApplyThemeColors();
        }
    }

    private void ApplyThemeColors()
    {
        bool light = _theme.IsLight;
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(175);
        _faintBrush.Color = Neutral(125);
        _paneBgBrush.Color = light ? Color.FromArgb(14, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255);
        _paneBorderBrush.Color = light ? Color.FromArgb(24, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255);
        _itemBgBrush.Color = light ? Color.FromArgb(15, 0, 0, 0) : Color.FromArgb(20, 255, 255, 255);
        _itemSelectedBgBrush.Color = light ? Color.FromArgb(30, 14, 165, 233) : Color.FromArgb(40, 56, 189, 248);
        _itemHoverBrush.Color = light ? Color.FromArgb(26, 0, 0, 0) : Color.FromArgb(36, 255, 255, 255);
        _dividerBrush.Color = Neutral(24);
        _hintBgBrush.Color = light ? Color.FromArgb(244, 255, 255, 255) : Color.FromArgb(240, 24, 28, 36);
        _accentSoftBgBrush.Color = light ? Color.FromArgb(26, 14, 165, 233) : Color.FromArgb(36, 56, 189, 248);
    }

    private Color Neutral(byte alpha) => _theme.IsLight
        ? Color.FromArgb(alpha, 0, 0, 0)
        : Color.FromArgb(alpha, 255, 255, 255);
}
