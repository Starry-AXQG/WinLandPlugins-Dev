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
/// 声枢（MediaDeck）岛体视图：
///   - 胶囊形态 & 卡片形态均内置音频响应背景流光（三层径向渐变光晕随实时音频电平呼吸与漂移）
///   - 展开卡片布局：
///     · 顶部：左上媒体来源图标（点击激活原窗口） / 中间多实例圆点与定时徽标 / 右上音频设备图标+蓝牙角标（点击展开卡片内可滚动下拉菜单）
///     · 中部：左侧圆角封面+歌名/歌手/来源名，右侧居中三行同步歌词
///     · 底部居中：上一曲 / 播放暂停 / 下一曲（带悬停聚光与按压反馈）
///     · 最底行：左侧已播时间 + 全宽交互进度条 + 右侧总时长
///   - 悬停提示文本以鼠标坐标为左顶点显示在卡片内无拦截覆盖层（Canvas）
///   - 严格遵守 SDK 铁律：逐帧赋值动画（无 Storyboard）、元素常驻可视树、浅色/深色主题自适应。
/// </summary>
public sealed class MediaDeckView : UserControl, IMorphView
{
    private const double BackAmplitude = 0.45;
    private const double CompactHeight = 40;
    private const double ExpandedHeight = 208;

    private readonly IIslandTheme _theme;
    private readonly Func<(float Master, float Left, float Right)> _audioPeakProvider;

    // 共享主题画刷（主题切换时只改 Color）
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _subtleFillBrush = new();
    private readonly SolidColorBrush _subtleBorderBrush = new();
    private readonly SolidColorBrush _hoverFillBrush = new();
    private readonly SolidColorBrush _hoverBorderBrush = new();
    private readonly SolidColorBrush _pressedFillBrush = new();
    private readonly SolidColorBrush _trackBgBrush = new();
    private readonly SolidColorBrush _progressFillBrush = new();
    private readonly SolidColorBrush _thumbBrush = new();
    private readonly SolidColorBrush _dropdownBgBrush = new();
    private readonly SolidColorBrush _dropdownSelectedBrush = new();
    private readonly SolidColorBrush _hintBgBrush = new();

    private readonly SolidColorBrush _accentCyanBrush = new(Color.FromArgb(255, 56, 189, 248));
    private readonly SolidColorBrush _bluetoothBlueBrush = new(Color.FromArgb(255, 24, 132, 255));

    // 背景流光层（三层径向渐变光晕）
    private readonly Grid _auroraLayer;
    private readonly Ellipse _blobCyan;
    private readonly Ellipse _blobViolet;
    private readonly Ellipse _blobRose;
    private readonly GradientStop _cyanStop0;
    private readonly GradientStop _cyanStop1;
    private readonly GradientStop _violetStop0;
    private readonly GradientStop _violetStop1;
    private readonly GradientStop _roseStop0;
    private readonly GradientStop _roseStop1;
    private readonly CompositeTransform _cyanTx = new();
    private readonly CompositeTransform _violetTx = new();
    private readonly CompositeTransform _roseTx = new();
    private readonly DispatcherQueueTimer? _auroraTimer;
    private double _auroraPhase;
    private double _smoothMaster;
    private double _smoothLeft;
    private double _smoothRight;
    private bool _auroraEnabled = true;
    private bool _isPlaying;

    // 形态变形动画状态
    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progress;

    // 翻页切换动画状态（逐帧赋值，禁止 Storyboard）
    private readonly DispatcherQueueTimer? _pageAnimTimer;
    private readonly CompositeTransform _pageTx = new();
    private DateTimeOffset _pageAnimStart;
    private double _pageAnimFromX;
    private Grid _middleGrid = null!;

    // 两层容器
    private readonly Grid _compactLayer;
    private readonly Grid _expandedLayer;

    // 紧凑态控件
    private readonly Image _compactCoverImg;
    private readonly FontIcon _compactCoverFallback;
    private readonly TextBlock _compactTitle;
    private readonly FontIcon _compactPlayIcon;

    // 展开态顶部栏
    private readonly Button _sourceBtn;
    private readonly Image _sourceIconImg;
    private readonly FontIcon _sourceIconFallback;
    private readonly StackPanel _dotsPanel;
    private readonly Border _timerBadge;
    private readonly TextBlock _timerBadgeText;
    private readonly Button _outputBtn;
    private readonly FontIcon _outputGlyph;
    private readonly Border _btBadge;

    // 展开态中部左侧（封面 + 歌名/歌手/来源）
    private readonly Image _coverImg;
    private readonly FontIcon _coverFallback;
    private readonly TextBlock _titleText;
    private readonly TextBlock _artistText;
    private readonly TextBlock _sourceNameText;

    // 展开态中部右侧（三行同步歌词）
    private readonly TextBlock _lyricPrev;
    private readonly TextBlock _lyricCurr;
    private readonly TextBlock _lyricNext;

    // 展开态底部居中控制按钮
    private readonly FontIcon _playPauseGlyph;

    // 展开态最底行进度条
    private readonly Grid _progressHitArea;
    private readonly Border _progressFill;
    private readonly Border _progressThumb;
    private readonly TextBlock _elapsedText;
    private readonly TextBlock _durationText;

    // 卡片内音频输出设备下拉菜单（严格限制在卡片内，超长滚动）
    private readonly Border _deviceDropdown;
    private readonly StackPanel _deviceDropdownList;
    private bool _dropdownOpen;

    // 鼠标坐标跟随的提示文本（以鼠标坐标为左顶点，无命中拦截）
    private readonly Canvas _hintCanvas;
    private readonly Border _hintPill;
    private readonly TextBlock _hintText;

    // 手势与进度条拖动状态
    private bool _isPointerDown;
    private Point _pointerDownPos;
    private bool _dragSwitched;
    private bool _isSeeking;
    private int _currentSelectedIndex = -1;

    // 缓存状态
    private byte[]? _lastCoverBytes;
    private byte[]? _lastAppIconBytes;
    private string _currentSourceName = "媒体播放器";
    private TimeSpan _currentDuration = TimeSpan.Zero;
    private double _lastRatio;
    private List<AudioDeviceInfo> _cachedDevices = [];

    public event Action? ActivateSourceRequested;
    public event Action? PlayPauseRequested;
    public event Action? PreviousRequested;
    public event Action? NextRequested;
    public event Action<double>? SeekRatioRequested;
    public event Action<int>? SwitchInstanceRequested;
    public event Action<int>? SelectInstanceRequested;
    public event Action<string>? SwitchOutputDeviceRequested;

    public MediaDeckView(
        IIslandTheme theme,
        Func<(float Master, float Left, float Right)> audioPeakProvider)
    {
        _theme = theme;
        _audioPeakProvider = audioPeakProvider;

        // ==================== 0. 音频响应背景流光层 ====================
        _cyanStop0 = new GradientStop { Offset = 0 };
        _cyanStop1 = new GradientStop { Offset = 0.48 };
        var cyanGrad = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        cyanGrad.GradientStops.Add(_cyanStop0);
        cyanGrad.GradientStops.Add(_cyanStop1);
        cyanGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 56, 189, 248), Offset = 1 });

        _violetStop0 = new GradientStop { Offset = 0 };
        _violetStop1 = new GradientStop { Offset = 0.48 };
        var violetGrad = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        violetGrad.GradientStops.Add(_violetStop0);
        violetGrad.GradientStops.Add(_violetStop1);
        violetGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 129, 140, 248), Offset = 1 });

        _roseStop0 = new GradientStop { Offset = 0 };
        _roseStop1 = new GradientStop { Offset = 0.45 };
        var roseGrad = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        roseGrad.GradientStops.Add(_roseStop0);
        roseGrad.GradientStops.Add(_roseStop1);
        roseGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 232, 121, 249), Offset = 1 });

        _blobCyan = new Ellipse
        {
            Width = 360,
            Height = 240,
            Fill = cyanGrad,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _cyanTx,
            Opacity = 0.52,
        };
        _blobViolet = new Ellipse
        {
            Width = 380,
            Height = 240,
            Fill = violetGrad,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _violetTx,
            Opacity = 0.52,
        };
        _blobRose = new Ellipse
        {
            Width = 300,
            Height = 210,
            Fill = roseGrad,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _roseTx,
            Opacity = 0.40,
        };

        _auroraLayer = new Grid
        {
            IsHitTestVisible = false,
            Children = { _blobCyan, _blobViolet, _blobRose },
        };

        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        // ==================== 1. 紧凑小岛层（胶囊形态） ====================
        _compactCoverImg = new Image { Stretch = Stretch.UniformToFill };
        _compactCoverFallback = new FontIcon
        {
            Glyph = "\uE8D6",
            FontSize = 12,
            Foreground = _accentCyanBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var compactCoverBox = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = _subtleFillBrush,
            Child = new Grid { Children = { _compactCoverFallback, _compactCoverImg } },
        };

        _compactTitle = new TextBlock
        {
            Text = "声枢 · 暂无媒体",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = _textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _compactPlayIcon = new FontIcon
        {
            Glyph = "\uE768",
            FontSize = 11,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _compactLayer = new Grid
        {
            Height = CompactHeight,
            Padding = new Thickness(12, 0, 12, 0),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _compactLayer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compactLayer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _compactLayer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(compactCoverBox, 0);
        Grid.SetColumn(_compactTitle, 1);
        Grid.SetColumn(_compactPlayIcon, 2);
        _compactLayer.Children.Add(compactCoverBox);
        _compactLayer.Children.Add(_compactTitle);
        _compactLayer.Children.Add(_compactPlayIcon);

        // ==================== 2. 展开大岛层（按图3居中控制 + 顶部功能角标） ====================
        // 2.1 左上角：媒体来源图标按钮
        _sourceIconImg = new Image { Width = 16, Height = 16, Stretch = Stretch.UniformToFill };
        _sourceIconFallback = new FontIcon
        {
            Glyph = "\uE8D6",
            FontSize = 12,
            Foreground = _accentCyanBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sourceIconInner = new Grid { Children = { _sourceIconFallback, _sourceIconImg } };
        _sourceBtn = CreateInteractiveIconButton(sourceIconInner, 28, 28, cornerRadius: 8, isPrimaryPill: false);
        _sourceBtn.Click += (_, _) => ActivateSourceRequested?.Invoke();
        AttachCursorHint(_sourceBtn, () => $"切换至 {_currentSourceName}");

        // 2.2 顶部绝对居中：多实例指示点（单层 Grid 叠放，确保数学级完全居中）
        _dotsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var dotsHoverHost = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _dotsPanel,
        };
        AttachCursorHint(dotsHoverHost, () => "滚轮、左右滑动或点击圆点切换媒体实例");

        _timerBadgeText = new TextBlock
        {
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _accentCyanBrush,
        };
        _timerBadge = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 2),
            Background = _subtleFillBrush,
            Opacity = 0,
            Height = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 38, 0),
            Child = _timerBadgeText,
        };

        // 2.3 右上角：音频输出类型图标 + 蓝牙角标
        _outputGlyph = new FontIcon
        {
            Glyph = "\uE767",
            FontSize = 15,
            Foreground = _textBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _btBadge = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = _bluetoothBlueBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -1, -2, 0),
            Opacity = 0,
            Child = new FontIcon
            {
                Glyph = "\uE702",
                FontSize = 7.5,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var outputIconContainer = new Grid
        {
            Width = 24,
            Height = 22,
            Children = { _outputGlyph, _btBadge },
        };
        _outputBtn = CreateInteractiveIconButton(outputIconContainer, 32, 28, cornerRadius: 8, isPrimaryPill: false);
        _outputBtn.HorizontalAlignment = HorizontalAlignment.Right;
        _outputBtn.Click += (_, _) => ToggleDeviceDropdown();
        AttachCursorHint(_outputBtn, () => "切换音频输出设备");

        _sourceBtn.HorizontalAlignment = HorizontalAlignment.Left;

        // 单格叠放容器：指示器严格居中于整张卡片，不受左右按钮宽度或定时徽标影响
        var topBar = new Grid
        {
            Height = 28,
            Children = { _sourceBtn, dotsHoverHost, _timerBadge, _outputBtn },
        };

        // 2.4 中部左侧：专辑封面 + 歌名/歌手/来源名（参考图3）
        _coverImg = new Image { Stretch = Stretch.UniformToFill };
        _coverFallback = new FontIcon
        {
            Glyph = "\uE8D6",
            FontSize = 26,
            Foreground = _accentCyanBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var coverBox = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(12),
            Background = _subtleFillBrush,
            BorderBrush = _subtleBorderBrush,
            BorderThickness = new Thickness(1),
            Child = new Grid { Children = { _coverFallback, _coverImg } },
        };

        _titleText = new TextBlock
        {
            Text = "暂无媒体播放",
            FontSize = 15.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _artistText = new TextBlock
        {
            Text = "播放音乐或视频后自动显示",
            FontSize = 12.5,
            Foreground = _mutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _sourceNameText = new TextBlock
        {
            Text = "声枢",
            FontSize = 11,
            Foreground = _faintBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var metaStack = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _titleText, _artistText, _sourceNameText },
        };

        var middleLeft = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        middleLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middleLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(coverBox, 0);
        Grid.SetColumn(metaStack, 1);
        middleLeft.Children.Add(coverBox);
        middleLeft.Children.Add(metaStack);

        // 2.5 中部右侧：居中三行同步歌词
        _lyricPrev = new TextBlock
        {
            Text = "",
            FontSize = 11.5,
            Foreground = _faintBrush,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _lyricCurr = new TextBlock
        {
            Text = "声枢已就绪",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _lyricNext = new TextBlock
        {
            Text = "",
            FontSize = 11.5,
            Foreground = _faintBrush,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var middleRight = new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 0, 4, 0),
            Children = { _lyricPrev, _lyricCurr, _lyricNext },
        };

        _middleGrid = new Grid
        {
            Margin = new Thickness(0, 2, 0, 2),
            RenderTransform = _pageTx,
        };
        _middleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        _middleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        Grid.SetColumn(middleLeft, 0);
        Grid.SetColumn(middleRight, 1);
        _middleGrid.Children.Add(middleLeft);
        _middleGrid.Children.Add(middleRight);

        // 2.6 底部居中：媒体控制组件（参考图3）
        var prevBtn = CreateInteractiveIconButton(
            new FontIcon { Glyph = "\uE892", FontSize = 15, Foreground = _textBrush },
            36, 36, cornerRadius: 18, isPrimaryPill: false);
        prevBtn.Click += (_, _) => PreviousRequested?.Invoke();
        AttachCursorHint(prevBtn, () => "上一曲");

        _playPauseGlyph = new FontIcon { Glyph = "\uE768", FontSize = 17, Foreground = _textBrush };
        var playBtn = CreateInteractiveIconButton(
            _playPauseGlyph,
            40, 40, cornerRadius: 20, isPrimaryPill: true);
        playBtn.Click += (_, _) => PlayPauseRequested?.Invoke();
        AttachCursorHint(playBtn, () => _isPlaying ? "暂停" : "播放");

        var nextBtn = CreateInteractiveIconButton(
            new FontIcon { Glyph = "\uE893", FontSize = 15, Foreground = _textBrush },
            36, 36, cornerRadius: 18, isPrimaryPill: false);
        nextBtn.Click += (_, _) => NextRequested?.Invoke();
        AttachCursorHint(nextBtn, () => "下一曲");

        var transportRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            Children = { prevBtn, playBtn, nextBtn },
        };

        // 2.7 最底行：全宽进度条 + 左右时间戳（参考图3）
        var trackBg = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = _trackBgBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _progressFill = new Border
        {
            Height = 4,
            Width = 0,
            CornerRadius = new CornerRadius(2),
            Background = _progressFillBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _progressThumb = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = _thumbBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _progressHitArea = new Grid
        {
            Height = 20,
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { trackBg, _progressFill, _progressThumb },
        };
        _progressHitArea.PointerPressed += OnProgressPointerPressed;
        _progressHitArea.PointerMoved += OnProgressPointerMoved;
        _progressHitArea.PointerReleased += OnProgressPointerReleased;
        _progressHitArea.PointerCaptureLost += OnProgressPointerCaptureLost;
        _progressHitArea.PointerEntered += (_, _) =>
        {
            _progressThumb.Width = 13;
            _progressThumb.Height = 13;
            _progressThumb.CornerRadius = new CornerRadius(6.5);
            UpdateProgressVisual(_lastRatio);
        };
        _progressHitArea.PointerExited += (_, _) =>
        {
            if (_isSeeking) return;
            _progressThumb.Width = 10;
            _progressThumb.Height = 10;
            _progressThumb.CornerRadius = new CornerRadius(5);
            UpdateProgressVisual(_lastRatio);
        };
        _progressHitArea.Tapped += (_, e) => e.Handled = true;
        _progressHitArea.SizeChanged += (_, _) => UpdateProgressVisual(_lastRatio);

        _elapsedText = new TextBlock
        {
            Text = "00:00",
            FontSize = 11,
            Foreground = _faintBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _durationText = new TextBlock
        {
            Text = "00:00",
            FontSize = 11,
            Foreground = _faintBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var bottomProgressRow = new Grid { ColumnSpacing = 10, Height = 18 };
        bottomProgressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomProgressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomProgressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_elapsedText, 0);
        Grid.SetColumn(_progressHitArea, 1);
        Grid.SetColumn(_durationText, 2);
        bottomProgressRow.Children.Add(_elapsedText);
        bottomProgressRow.Children.Add(_progressHitArea);
        bottomProgressRow.Children.Add(_durationText);

        // 2.8 卡片内音频输出设备下拉菜单（严格限制在卡片内，超长滚动）
        _deviceDropdownList = new StackPanel { Spacing = 3, Padding = new Thickness(5) };
        var dropdownScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 142,
            Content = _deviceDropdownList,
        };
        _deviceDropdown = new Border
        {
            Width = 232,
            MaxHeight = 150,
            Height = 0,
            Opacity = 0,
            IsHitTestVisible = false,
            CornerRadius = new CornerRadius(10),
            Background = _dropdownBgBrush,
            BorderBrush = _subtleBorderBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 32, 0, 0),
            Child = dropdownScroll,
        };
        _deviceDropdown.Tapped += (_, e) => e.Handled = true;
        _deviceDropdown.PointerWheelChanged += (_, e) => e.Handled = true;

        // 2.9 以鼠标坐标为左顶点的文本提示层（IsHitTestVisible = false，不抢焦点）
        _hintText = new TextBlock { FontSize = 11, Foreground = _textBrush };
        _hintPill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Background = _hintBgBrush,
            BorderBrush = _subtleBorderBrush,
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

        var expandedContent = new Grid { RowSpacing = 2 };
        expandedContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        expandedContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        expandedContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        expandedContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(topBar, 0);
        Grid.SetRow(_middleGrid, 1);
        Grid.SetRow(transportRow, 2);
        Grid.SetRow(bottomProgressRow, 3);
        expandedContent.Children.Add(topBar);
        expandedContent.Children.Add(_middleGrid);
        expandedContent.Children.Add(transportRow);
        expandedContent.Children.Add(bottomProgressRow);

        _expandedLayer = new Grid
        {
            Height = 0,
            Opacity = 0,
            IsHitTestVisible = false,
            Padding = new Thickness(16, 10, 16, 10),
            Children = { expandedContent, _deviceDropdown, _hintCanvas },
        };

        // 根容器（透明底 + 音频响应流光 + 双层内容）
        var root = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Children = { _auroraLayer, _compactLayer, _expandedLayer },
        };

        root.PointerWheelChanged += OnCardPointerWheelChanged;
        root.PointerPressed += OnCardPointerPressed;
        root.PointerMoved += OnCardPointerMoved;
        root.PointerReleased += OnCardPointerReleased;
        root.Tapped += OnCardTapped;

        Content = root;

        // 逐帧变形定时器（禁止 Storyboard）
        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        // 翻页切换动画定时器（逐帧滑动 + 淡入）
        _pageAnimTimer = DispatcherQueue?.CreateTimer();
        if (_pageAnimTimer is not null)
        {
            _pageAnimTimer.Interval = TimeSpan.FromMilliseconds(16);
            _pageAnimTimer.IsRepeating = true;
            _pageAnimTimer.Tick += (_, _) => OnPageAnimTick();
        }

        // 音频响应背景流光定时器（约 30fps）
        _auroraTimer = DispatcherQueue?.CreateTimer();
        if (_auroraTimer is not null)
        {
            _auroraTimer.Interval = TimeSpan.FromMilliseconds(33);
            _auroraTimer.IsRepeating = true;
            _auroraTimer.Tick += (_, _) => OnAuroraTick();
            _auroraTimer.Start();
        }

        Loaded += (_, _) =>
        {
            _auroraTimer?.Start();
            if (Math.Abs(_progress - _morphTarget) > 1e-4)
                _morphTimer?.Start();
        };
        Unloaded += (_, _) =>
        {
            // 仅在真正脱离可视树时停止定时器，防止主岛与队列卡片之间重挂载时异步 Unloaded 中断形变动画
            if (IsLoaded) return;
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget);
            _pageAnimTimer?.Stop();
            _pageTx.TranslateX = 0;
            _middleGrid.Opacity = 1;
            _auroraTimer?.Stop();
        };
    }

    public UIElement View => this;

    public void SetAuroraEnabled(bool enabled)
    {
        _auroraEnabled = enabled;
        _auroraLayer.Opacity = enabled ? 1 : 0;
    }

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(expanded: true, duration);

    public void AnimateToCompact(TimeSpan duration)
    {
        CloseDeviceDropdown();
        HideHint();
        StartMorph(expanded: false, duration);
    }

    public void UpdateMedia(MediaInstanceInfo? current, int totalCount, int selectedIndex, LyricTriplet lyrics)
    {
        if (current is null)
        {
            _isPlaying = false;
            _currentSourceName = "媒体播放器";
            _currentDuration = TimeSpan.Zero;
            _compactTitle.Text = "声枢 · 暂无媒体";
            _compactPlayIcon.Glyph = "\uE768";
            _titleText.Text = "暂无媒体播放";
            _artistText.Text = "播放音乐或视频后自动显示";
            _sourceNameText.Text = "声枢";
            _playPauseGlyph.Glyph = "\uE768";
            _elapsedText.Text = "00:00";
            _durationText.Text = "00:00";
            UpdateProgressVisual(0);
            _lyricPrev.Text = "";
            _lyricCurr.Text = "声枢已就绪";
            _lyricNext.Text = "";
            UpdateCoverImage(null);
            UpdateAppIconImage(null);
            RebuildInstanceDots(0, -1);
            return;
        }

        _isPlaying = current.IsPlaying;
        _currentSourceName = current.SourceDisplayName;
        _currentDuration = current.Duration;

        _compactTitle.Text = lyrics.HasSyncedLyrics && !string.IsNullOrWhiteSpace(lyrics.Current)
            ? lyrics.Current
            : current.Title;
        _compactPlayIcon.Glyph = current.IsPlaying ? "\uE769" : "\uE768";

        _titleText.Text = current.Title;
        _artistText.Text = string.IsNullOrWhiteSpace(current.Artist)
            ? current.SourceDisplayName
            : current.Artist;
        _sourceNameText.Text = current.SourceDisplayName;
        _playPauseGlyph.Glyph = current.IsPlaying ? "\uE769" : "\uE768";

        var pos = current.EstimatedPosition;
        var dur = current.Duration;
        if (!_isSeeking)
        {
            _elapsedText.Text = FormatTime(pos);
            _durationText.Text = dur > TimeSpan.Zero ? FormatTime(dur) : "--:--";
            double ratio = dur > TimeSpan.Zero ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
            UpdateProgressVisual(ratio);
        }

        _currentSelectedIndex = selectedIndex;
        _lyricPrev.Text = lyrics.Previous;
        _lyricCurr.Text = lyrics.Current;
        _lyricNext.Text = lyrics.Next;

        UpdateCoverImage(current.ThumbnailBytes);
        UpdateAppIconImage(current.AppIconBytes);
        RebuildInstanceDots(totalCount, selectedIndex);
    }

    public void UpdateOutputDevices(IReadOnlyList<AudioDeviceInfo> devices)
    {
        _cachedDevices = devices.ToList();
        var def = devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();

        if (def is not null)
        {
            _outputGlyph.Glyph = def.Kind == AudioOutputKind.Headphone ? "\uE7F6" : "\uE767";
            _btBadge.Opacity = def.IsBluetooth ? 1 : 0;
        }
        else
        {
            _outputGlyph.Glyph = "\uE767";
            _btBadge.Opacity = 0;
        }

        if (_dropdownOpen)
        {
            RebuildDeviceDropdownItems();
        }
    }

    public void UpdateSleepTimer(bool isActive, TimeSpan remaining)
    {
        if (isActive && remaining > TimeSpan.Zero)
        {
            _timerBadgeText.Text = $"⏱ {FormatTime(remaining)}";
            _timerBadge.Opacity = 1;
            _timerBadge.Height = 18;
        }
        else
        {
            _timerBadge.Opacity = 0;
            _timerBadge.Height = 0;
        }
    }

    // ==================== 音频响应背景流光 ====================

    private void OnAuroraTick()
    {
        if (!_auroraEnabled) return;

        try
        {
            var (master, left, right) = _audioPeakProvider();
            double mCurve = Math.Pow(Math.Clamp(master, 0, 1), 0.65);
            double lCurve = Math.Pow(Math.Clamp(left, 0, 1), 0.65);
            double rCurve = Math.Pow(Math.Clamp(right, 0, 1), 0.65);

            double targetM = _isPlaying ? Math.Max(mCurve, 0.16) : mCurve * 0.6;
            double targetL = _isPlaying ? Math.Max(lCurve, 0.14) : lCurve * 0.6;
            double targetR = _isPlaying ? Math.Max(rCurve, 0.14) : rCurve * 0.6;

            // 快起慢落平滑
            _smoothMaster += (targetM - _smoothMaster) * (targetM > _smoothMaster ? 0.38 : 0.10);
            _smoothLeft += (targetL - _smoothLeft) * (targetL > _smoothLeft ? 0.38 : 0.10);
            _smoothRight += (targetR - _smoothRight) * (targetR > _smoothRight ? 0.38 : 0.10);

            _auroraPhase += 0.028 + _smoothMaster * 0.065;

            double baseOp = _theme.IsLight ? 0.46 : 0.38;
            double boost = _theme.IsLight ? 0.52 : 0.56;

            _blobCyan.Opacity = Math.Clamp(baseOp + _smoothLeft * boost, 0.28, 0.98);
            _blobViolet.Opacity = Math.Clamp(baseOp + _smoothRight * boost, 0.28, 0.98);
            _blobRose.Opacity = Math.Clamp(baseOp * 0.8 + _smoothMaster * boost * 0.9, 0.22, 0.92);

            double scaleM = 0.96 + _smoothMaster * 0.42;
            double scaleL = 0.96 + _smoothLeft * 0.40;
            double scaleR = 0.96 + _smoothRight * 0.40;

            _cyanTx.ScaleX = scaleL;
            _cyanTx.ScaleY = scaleL;
            _cyanTx.TranslateX = Math.Sin(_auroraPhase) * 30;
            _cyanTx.TranslateY = Math.Cos(_auroraPhase * 0.8) * 16;

            _violetTx.ScaleX = scaleR;
            _violetTx.ScaleY = scaleR;
            _violetTx.TranslateX = Math.Cos(_auroraPhase * 0.9) * 30;
            _violetTx.TranslateY = Math.Sin(_auroraPhase * 0.7) * 16;

            _roseTx.ScaleX = scaleM;
            _roseTx.ScaleY = scaleM;
            _roseTx.TranslateX = Math.Sin(_auroraPhase * 1.15) * 20;
            _roseTx.TranslateY = Math.Cos(_auroraPhase * 1.1) * 12;
        }
        catch { }
    }

    // ==================== 卡片内音频输出下拉菜单 ====================

    private void ToggleDeviceDropdown()
    {
        if (_dropdownOpen)
        {
            CloseDeviceDropdown();
            return;
        }

        HideHint();
        // 直接使用后台线程维护好的最新缓存，毫秒级秒开，不阻塞 UI
        RebuildDeviceDropdownItems();
        _dropdownOpen = true;
        _deviceDropdown.Height = double.NaN;
        _deviceDropdown.Opacity = 1;
        _deviceDropdown.IsHitTestVisible = true;
    }

    private void CloseDeviceDropdown()
    {
        if (!_dropdownOpen) return;
        _dropdownOpen = false;
        _deviceDropdown.Opacity = 0;
        _deviceDropdown.Height = 0;
        _deviceDropdown.IsHitTestVisible = false;
    }

    private void RebuildDeviceDropdownItems()
    {
        _deviceDropdownList.Children.Clear();
        if (_cachedDevices.Count == 0)
        {
            _deviceDropdownList.Children.Add(new TextBlock
            {
                Text = "未检测到音频输出设备",
                FontSize = 11.5,
                Foreground = _mutedBrush,
                Margin = new Thickness(8, 6, 8, 6),
            });
            return;
        }

        foreach (var dev in _cachedDevices)
        {
            var icon = new FontIcon
            {
                Glyph = dev.Kind == AudioOutputKind.Headphone ? "\uE7F6" : "\uE767",
                FontSize = 13,
                Foreground = dev.IsDefault ? _accentCyanBrush : _mutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var nameText = new TextBlock
            {
                Text = dev.IsBluetooth ? $"{dev.ShortName} · 蓝牙" : dev.ShortName,
                FontSize = 12,
                FontWeight = dev.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = dev.IsDefault ? _accentCyanBrush : _textBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 11,
                Foreground = _accentCyanBrush,
                Opacity = dev.IsDefault ? 1 : 0,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(nameText, 1);
            Grid.SetColumn(check, 2);
            row.Children.Add(icon);
            row.Children.Add(nameText);
            row.Children.Add(check);

            var baseBg = dev.IsDefault ? _dropdownSelectedBrush : new SolidColorBrush(Colors.Transparent);
            var itemBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 6, 8, 6),
                Background = baseBg,
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Content = row,
            };

            bool isDef = dev.IsDefault;
            itemBtn.PointerEntered += (_, _) =>
            {
                itemBtn.Background = _hoverFillBrush;
                itemBtn.BorderBrush = _hoverBorderBrush;
            };
            itemBtn.PointerExited += (_, _) =>
            {
                itemBtn.Background = isDef ? _dropdownSelectedBrush : new SolidColorBrush(Colors.Transparent);
                itemBtn.BorderBrush = new SolidColorBrush(Colors.Transparent);
            };
            itemBtn.Tapped += (_, e) => e.Handled = true;

            var deviceId = dev.Id;
            itemBtn.Click += (_, _) =>
            {
                SwitchOutputDeviceRequested?.Invoke(deviceId);
                CloseDeviceDropdown();
            };

            _deviceDropdownList.Children.Add(itemBtn);
        }
    }

    // ==================== 按钮悬停聚光反馈 + 鼠标坐标提示 ====================

    private Button CreateInteractiveIconButton(
        UIElement content,
        double width,
        double height,
        double cornerRadius,
        bool isPrimaryPill)
    {
        var btn = new Button
        {
            Width = width,
            Height = height,
            Padding = new Thickness(0),
            Background = isPrimaryPill ? _subtleFillBrush : new SolidColorBrush(Colors.Transparent),
            BorderBrush = isPrimaryPill ? _subtleBorderBrush : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(cornerRadius),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = content,
        };

        // 防止按钮点击的 Tapped 冒泡到卡片根容器（否则会导致刚打开的下拉菜单瞬间被关闭，或误触聚光卡）
        btn.Tapped += (_, e) => e.Handled = true;

        btn.PointerEntered += (_, _) =>
        {
            btn.Background = _hoverFillBrush;
            btn.BorderBrush = _hoverBorderBrush;
        };
        btn.PointerExited += (_, _) =>
        {
            btn.Background = isPrimaryPill ? _subtleFillBrush : new SolidColorBrush(Colors.Transparent);
            btn.BorderBrush = isPrimaryPill ? _subtleBorderBrush : new SolidColorBrush(Colors.Transparent);
        };
        btn.PointerPressed += (_, _) =>
        {
            btn.Background = _pressedFillBrush;
        };
        btn.PointerReleased += (_, _) =>
        {
            btn.Background = _hoverFillBrush;
        };

        return btn;
    }

    private void AttachCursorHint(UIElement target, Func<string> textProvider)
    {
        target.PointerEntered += (_, e) => UpdateCursorHint(textProvider(), e);
        target.PointerMoved += (_, e) => UpdateCursorHint(textProvider(), e);
        target.PointerExited += (_, _) => HideHint();
    }

    private void UpdateCursorHint(string text, PointerRoutedEventArgs e)
    {
        if (_progress < 0.7 || _dropdownOpen)
        {
            HideHint();
            return;
        }

        _hintText.Text = text;
        var pt = e.GetCurrentPoint(_hintCanvas).Position;

        // 以鼠标坐标为左顶点，并防止超出卡片右/下边界
        double maxX = Math.Max(4, _hintCanvas.ActualWidth - 130);
        double maxY = Math.Max(4, _hintCanvas.ActualHeight - 26);
        Canvas.SetLeft(_hintPill, Math.Clamp(pt.X, 4, maxX));
        Canvas.SetTop(_hintPill, Math.Clamp(pt.Y, 4, maxY));
        _hintPill.Opacity = 1;
    }

    private void HideHint() => _hintPill.Opacity = 0;

    // ==================== 多实例、翻页动画、进度条拖动与手势 ====================

    private void StartPageTransition(int direction)
    {
        if (direction == 0) return;
        _pageAnimFromX = direction > 0 ? 44 : -44;
        _pageAnimStart = DateTimeOffset.UtcNow;
        _pageTx.TranslateX = _pageAnimFromX;
        _middleGrid.Opacity = 0.18;
        _pageAnimTimer?.Start();
    }

    private void OnPageAnimTick()
    {
        const double durationMs = 240;
        var elapsed = (DateTimeOffset.UtcNow - _pageAnimStart).TotalMilliseconds;
        double t = Math.Clamp(elapsed / durationMs, 0, 1);
        double ease = 1 - Math.Pow(1 - t, 3); // EaseOutCubic

        _pageTx.TranslateX = _pageAnimFromX * (1 - ease);
        _middleGrid.Opacity = 0.18 + 0.82 * ease;

        if (t >= 1)
        {
            _pageAnimTimer?.Stop();
            _pageTx.TranslateX = 0;
            _middleGrid.Opacity = 1;
        }
    }

    private void RebuildInstanceDots(int totalCount, int selectedIndex)
    {
        _dotsPanel.Children.Clear();
        if (totalCount <= 1) return;

        for (int i = 0; i < totalCount; i++)
        {
            int idx = i;
            bool active = i == selectedIndex;
            var dot = new Border
            {
                Width = active ? 16 : 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = active ? _accentCyanBrush : _faintBrush,
                Opacity = active ? 1 : 0.48,
            };
            dot.PointerPressed += (_, e) => e.Handled = true;
            dot.Tapped += (_, e) =>
            {
                e.Handled = true;
                if (idx != _currentSelectedIndex)
                {
                    StartPageTransition(idx > _currentSelectedIndex ? 1 : -1);
                    SelectInstanceRequested?.Invoke(idx);
                }
            };
            _dotsPanel.Children.Add(dot);
        }
    }

    private void UpdateProgressVisual(double ratio)
    {
        _lastRatio = Math.Clamp(ratio, 0, 1);
        double w = _progressHitArea.ActualWidth;
        if (w <= 0) return;

        double thumbHalf = _progressThumb.Width / 2.0;
        double fillW = w * _lastRatio;
        _progressFill.Width = fillW;
        double thumbLeft = Math.Clamp(fillW - thumbHalf, 0, Math.Max(0, w - _progressThumb.Width));
        _progressThumb.Margin = new Thickness(thumbLeft, 0, 0, 0);
    }

    private double ComputeSeekRatio(PointerRoutedEventArgs e)
    {
        double w = _progressHitArea.ActualWidth;
        if (w <= 0) return 0;
        var pt = e.GetCurrentPoint(_progressHitArea).Position;
        return Math.Clamp(pt.X / w, 0, 1);
    }

    private void OnProgressPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_progressHitArea);
        if (!pt.Properties.IsLeftButtonPressed) return;

        e.Handled = true;
        _isPointerDown = false;
        _dragSwitched = false;

        if (_currentDuration <= TimeSpan.Zero) return;

        _isSeeking = true;
        _progressHitArea.CapturePointer(e.Pointer);
        double ratio = ComputeSeekRatio(e);
        UpdateProgressVisual(ratio);
        _elapsedText.Text = FormatTime(TimeSpan.FromSeconds(_currentDuration.TotalSeconds * ratio));
    }

    private void OnProgressPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking) return;
        e.Handled = true;
        double ratio = ComputeSeekRatio(e);
        UpdateProgressVisual(ratio);
        if (_currentDuration > TimeSpan.Zero)
        {
            _elapsedText.Text = FormatTime(TimeSpan.FromSeconds(_currentDuration.TotalSeconds * ratio));
        }
    }

    private void OnProgressPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking) return;
        e.Handled = true;
        double ratio = ComputeSeekRatio(e);
        _isSeeking = false;
        _progressHitArea.ReleasePointerCapture(e.Pointer);
        UpdateProgressVisual(ratio);
        SeekRatioRequested?.Invoke(ratio);
    }

    private void OnProgressPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking) return;
        _isSeeking = false;
        _progressThumb.Width = 10;
        _progressThumb.Height = 10;
        _progressThumb.CornerRadius = new CornerRadius(5);
        UpdateProgressVisual(_lastRatio);
    }

    private void OnCardPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_progress < 0.5 || _dropdownOpen || _isSeeking) return;
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;

        e.Handled = true;
        int dir = delta > 0 ? -1 : 1;
        StartPageTransition(dir);
        SwitchInstanceRequested?.Invoke(dir);
    }

    private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_progress < 0.5 || _dropdownOpen || _isSeeking) return;
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;

        _isPointerDown = true;
        _dragSwitched = false;
        _pointerDownPos = pt.Position;
    }

    private void OnCardPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerDown || _dragSwitched || _isSeeking) return;
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed)
        {
            _isPointerDown = false;
            return;
        }

        double dx = pt.Position.X - _pointerDownPos.X;
        double dy = pt.Position.Y - _pointerDownPos.Y;
        if (Math.Abs(dx) >= 36 && Math.Abs(dx) > Math.Abs(dy) * 1.4)
        {
            _dragSwitched = true;
            _isPointerDown = false;
            int dir = dx < 0 ? 1 : -1;
            StartPageTransition(dir);
            SwitchInstanceRequested?.Invoke(dir);
        }
    }

    private void OnCardPointerReleased(object sender, PointerRoutedEventArgs e) => _isPointerDown = false;

    private void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_dropdownOpen)
        {
            CloseDeviceDropdown();
            e.Handled = true;
            return;
        }
        if (_dragSwitched)
        {
            _dragSwitched = false;
            e.Handled = true;
        }
    }

    // ==================== 图像加载 ====================

    private async void UpdateCoverImage(byte[]? bytes)
    {
        if (ReferenceEquals(bytes, _lastCoverBytes)) return;
        _lastCoverBytes = bytes;

        if (bytes is null || bytes.Length == 0)
        {
            _coverImg.Source = null;
            _compactCoverImg.Source = null;
            _coverFallback.Opacity = 1;
            _compactCoverFallback.Opacity = 1;
            return;
        }

        try
        {
            var bmp = await BytesToBitmapAsync(bytes);
            if (!ReferenceEquals(bytes, _lastCoverBytes)) return;
            _coverImg.Source = bmp;
            _compactCoverImg.Source = bmp;
            _coverFallback.Opacity = 0;
            _compactCoverFallback.Opacity = 0;
        }
        catch
        {
            _coverFallback.Opacity = 1;
            _compactCoverFallback.Opacity = 1;
        }
    }

    private async void UpdateAppIconImage(byte[]? bytes)
    {
        if (ReferenceEquals(bytes, _lastAppIconBytes)) return;
        _lastAppIconBytes = bytes;

        if (bytes is null || bytes.Length == 0)
        {
            _sourceIconImg.Source = null;
            _sourceIconFallback.Opacity = 1;
            return;
        }

        try
        {
            var bmp = await BytesToBitmapAsync(bytes);
            if (!ReferenceEquals(bytes, _lastAppIconBytes)) return;
            _sourceIconImg.Source = bmp;
            _sourceIconFallback.Opacity = 0;
        }
        catch
        {
            _sourceIconFallback.Opacity = 1;
        }
    }

    private static async Task<BitmapImage> BytesToBitmapAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        return bmp;
    }

    private static string FormatTime(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }

    // ==================== 浅色 / 深色主题自适应 ====================

    private void ApplyThemeColors()
    {
        bool light = _theme.IsLight;
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(180);
        _faintBrush.Color = Neutral(128);

        _subtleFillBrush.Color = light ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255);
        _subtleBorderBrush.Color = light ? Color.FromArgb(34, 0, 0, 0) : Color.FromArgb(46, 255, 255, 255);
        _hoverFillBrush.Color = light ? Color.FromArgb(36, 0, 0, 0) : Color.FromArgb(62, 255, 255, 255);
        _hoverBorderBrush.Color = light ? Color.FromArgb(64, 0, 0, 0) : Color.FromArgb(95, 255, 255, 255);
        _pressedFillBrush.Color = light ? Color.FromArgb(52, 0, 0, 0) : Color.FromArgb(88, 255, 255, 255);

        _trackBgBrush.Color = light ? Color.FromArgb(38, 0, 0, 0) : Color.FromArgb(56, 255, 255, 255);
        _progressFillBrush.Color = light ? Color.FromArgb(200, 30, 41, 59) : Color.FromArgb(235, 103, 232, 249);
        _thumbBrush.Color = light ? Color.FromArgb(255, 15, 23, 42) : Color.FromArgb(255, 103, 232, 249);

        _dropdownBgBrush.Color = light ? Color.FromArgb(246, 250, 252, 255) : Color.FromArgb(244, 24, 28, 36);
        _dropdownSelectedBrush.Color = light ? Color.FromArgb(26, 14, 165, 233) : Color.FromArgb(38, 56, 189, 248);
        _hintBgBrush.Color = light ? Color.FromArgb(240, 255, 255, 255) : Color.FromArgb(235, 24, 28, 36);

        // 流光中心色与中段色：大幅提高不透明度与铺展范围，让浅色/深色主题下都清晰可见
        _cyanStop0.Color = light ? Color.FromArgb(220, 56, 189, 248) : Color.FromArgb(205, 34, 211, 238);
        _cyanStop1.Color = light ? Color.FromArgb(135, 56, 189, 248) : Color.FromArgb(115, 34, 211, 238);

        _violetStop0.Color = light ? Color.FromArgb(215, 129, 140, 248) : Color.FromArgb(205, 129, 140, 248);
        _violetStop1.Color = light ? Color.FromArgb(130, 129, 140, 248) : Color.FromArgb(115, 129, 140, 248);

        _roseStop0.Color = light ? Color.FromArgb(185, 232, 121, 249) : Color.FromArgb(175, 217, 70, 239);
        _roseStop1.Color = light ? Color.FromArgb(100, 232, 121, 249) : Color.FromArgb(90, 217, 70, 239);
    }

    private Color Neutral(byte alpha) => _theme.IsLight
        ? Color.FromArgb(alpha, 0, 0, 0)
        : Color.FromArgb(alpha, 255, 255, 255);

    // ==================== 逐帧变形动画（无 Storyboard） ====================

    private void StartMorph(bool expanded, TimeSpan duration)
    {
        _morphFrom = _progress;
        _morphTarget = expanded ? 1d : 0d;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

        if (_morphTimer is null)
        {
            ApplyMorph(_morphTarget);
            return;
        }

        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
        var durationMs = Math.Max(1, _morphDuration.TotalMilliseconds);
        var t = Math.Clamp(elapsed / durationMs, 0, 1);

        ApplyMorph(_morphFrom + (_morphTarget - _morphFrom) * BackEaseOut(t));

        if (t >= 1)
        {
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget);
        }
    }

    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    private void ApplyMorph(double progress)
    {
        _progress = progress;
        double clamped = Math.Clamp(progress, 0, 1);

        _compactLayer.Height = Math.Max(0, CompactHeight * (1 - clamped));
        _compactLayer.Opacity = Math.Clamp(1 - clamped * 2.2, 0, 1);
        _compactLayer.IsHitTestVisible = clamped < 0.35;

        _expandedLayer.Height = Math.Max(0, ExpandedHeight * progress);
        _expandedLayer.Opacity = Math.Clamp((clamped - 0.15) / 0.85, 0, 1);
        _expandedLayer.IsHitTestVisible = clamped > 0.65;
    }
}
