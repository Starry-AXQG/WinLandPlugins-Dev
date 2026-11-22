using System.Runtime.InteropServices.WindowsRuntime;
using MediaDeck.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using WinIsland.Core;

namespace MediaDeck.Views;

/// <summary>
/// 聚光卡内的单个媒体播放实例卡片：
///   - 外观与交互与灵动岛卡片形态 1:1 保持一致；
///   - 支持极光流光波浪 / 专辑封面底部向内轮廓光（含律动强度设置）+ 鼠标跟随圆形光效与避让反馈；
///   - 控制行左对齐闹钟图标支持为该实例独立设置定时暂停；
///   - 支持点击左上角激活该实例窗口、进度条拖拽跳转、上/下一曲、播放/暂停。
/// </summary>
public sealed class SpotlightMediaCard : UserControl
{
    private const int WavePointsCount = 60;
    private const double CardHeight = 192.0;

    private readonly IIslandTheme _theme;
    private readonly string _instanceId;
    private readonly Action<UIElement, Func<string>> _attachHint;
    private readonly Action _hideHint;

    // 主题画刷
    private readonly SolidColorBrush _cardBgBrush = new();
    private readonly SolidColorBrush _cardBorderBrush = new();
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
    private readonly SolidColorBrush _popupBgBrush = new();
    private readonly SolidColorBrush _accentCyanBrush = new(Color.FromArgb(255, 56, 189, 248));
    private readonly SolidColorBrush _playingPillBgBrush = new(Color.FromArgb(36, 16, 185, 129));
    private readonly SolidColorBrush _playingPillFgBrush = new(Color.FromArgb(255, 52, 211, 153));

    // 视觉特效 1：极光波浪
    private readonly Grid _auroraWaveLayer;
    private readonly Polygon _waveRibbon1;
    private readonly Polygon _waveRibbon2;
    private readonly Polygon _waveRibbon3;
    private readonly GradientStop _wave1TopStop;
    private readonly GradientStop _wave1MidStop;
    private readonly GradientStop _wave2TopStop;
    private readonly GradientStop _wave2MidStop;
    private readonly GradientStop _wave3TopStop;
    private readonly GradientStop _wave3MidStop;

    // 视觉特效 2：底部向内轮廓光
    private readonly Grid _contourGlowLayer;
    private readonly Border _inwardCenterBeamHost;
    private readonly Border _inwardAccentBeamHost;
    private readonly RadialGradientBrush _inwardCenterBeamBrush;
    private readonly RadialGradientBrush _inwardAccentBeamBrush;
    private readonly GradientStop _beamCoreStop;
    private readonly GradientStop _beamMidStop;
    private readonly GradientStop _beamEdgeStop;
    private readonly GradientStop _accentCoreStop;
    private readonly GradientStop _accentMidStop;
    private readonly GradientStop _accentEdgeStop;

    // 鼠标跟随圆形光效
    private readonly Canvas _cursorGlowCanvas;
    private readonly Ellipse _cursorGlowEllipse;
    private readonly GradientStop _cursorStop0;
    private readonly GradientStop _cursorStop1;
    private bool _isPointerInCard;
    private double _cursorRawX = 380;
    private double _cursorRawY = 96;
    private double _cursorSmoothX = 380;
    private double _cursorSmoothY = 96;
    private double _cursorSmoothOpacity;
    private bool _cursorGlowEnabled = true;
    private double _cursorGlowSize = 96.0;
    private double _cursorGlowOpacity = 0.30;

    // 特效状态
    private VisualEffectMode _visualMode = VisualEffectMode.AuroraWave;
    private double _contourReactivity = 0.50;
    private double _phase1;
    private double _phase2 = 1.8;
    private double _phase3 = 3.6;
    private double _smoothMaster;
    private double _slowMaster;
    private double _smoothLeft;
    private double _smoothRight;
    private Color _targetCoverLeft = Color.FromArgb(255, 56, 189, 248);
    private Color _targetCoverCenter = Color.FromArgb(255, 168, 85, 247);
    private Color _targetCoverRight = Color.FromArgb(255, 236, 72, 153);
    private Color _curCoverLeft = Color.FromArgb(255, 56, 189, 248);
    private Color _curCoverCenter = Color.FromArgb(255, 168, 85, 247);
    private Color _curCoverRight = Color.FromArgb(255, 236, 72, 153);

    // 控件引用
    private readonly Image _sourceIconImg;
    private readonly FontIcon _sourceIconFallback;
    private readonly Border _statusPill;
    private readonly TextBlock _statusPillText;
    private readonly Image _coverImg;
    private readonly FontIcon _coverFallback;
    private readonly TextBlock _titleText;
    private readonly TextBlock _artistText;
    private readonly TextBlock _sourceNameText;
    private readonly TextBlock _lyricPrev;
    private readonly TextBlock _lyricCurr;
    private readonly TextBlock _lyricNext;
    private readonly FontIcon _playPauseGlyph;

    // 左对齐闹钟 + 弹出菜单
    private readonly FontIcon _alarmGlyph;
    private readonly Border _alarmInlineBadge;
    private readonly TextBlock _alarmInlineText;
    private readonly Border _alarmPopup;
    private readonly StackPanel _alarmPopupList;
    private bool _alarmPopupOpen;
    private bool _hasTimer;
    private TimeSpan _timerRemaining;

    // 进度条
    private readonly Grid _progressHitArea;
    private readonly Border _progressFill;
    private readonly Border _progressThumb;
    private readonly TextBlock _elapsedText;
    private readonly TextBlock _durationText;
    private bool _isSeeking;
    private double _lastRatio;
    private TimeSpan _duration = TimeSpan.Zero;

    // 数据缓存
    private byte[]? _lastCoverBytes;
    private byte[]? _lastAppIconBytes;
    private string _sourceName = "媒体播放器";
    private bool _isPlaying;

    public string InstanceId => _instanceId;

    public event Action<string>? ActivateSourceRequested;
    public event Action<string>? PlayPauseRequested;
    public event Action<string>? PreviousRequested;
    public event Action<string>? NextRequested;
    public event Action<string, double>? SeekRatioRequested;
    public event Action<string, int>? SetSleepTimerRequested;

    public SpotlightMediaCard(
        IIslandTheme theme,
        string instanceId,
        Action<UIElement, Func<string>> attachHint,
        Action hideHint)
    {
        _theme = theme;
        _instanceId = instanceId;
        _attachHint = attachHint;
        _hideHint = hideHint;

        // 随机初相位，避免多张卡片波浪完全同步显得呆板
        int hash = Math.Abs(instanceId.GetHashCode());
        _phase1 = (hash % 628) / 100.0;
        _phase2 = _phase1 + 2.1;
        _phase3 = _phase1 + 4.2;

        // ==================== 0A. 极光流光波浪层 ====================
        _wave1TopStop = new GradientStop { Offset = 0.0 };
        _wave1MidStop = new GradientStop { Offset = 0.55 };
        var wave1Brush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        wave1Brush.GradientStops.Add(_wave1TopStop);
        wave1Brush.GradientStops.Add(_wave1MidStop);
        wave1Brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 56, 189, 248), Offset = 1.0 });

        _wave2TopStop = new GradientStop { Offset = 0.0 };
        _wave2MidStop = new GradientStop { Offset = 0.55 };
        var wave2Brush = new LinearGradientBrush { StartPoint = new Point(0.3, 0), EndPoint = new Point(0.7, 1) };
        wave2Brush.GradientStops.Add(_wave2TopStop);
        wave2Brush.GradientStops.Add(_wave2MidStop);
        wave2Brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 129, 140, 248), Offset = 1.0 });

        _wave3TopStop = new GradientStop { Offset = 0.0 };
        _wave3MidStop = new GradientStop { Offset = 0.50 };
        var wave3Brush = new LinearGradientBrush { StartPoint = new Point(0.7, 0), EndPoint = new Point(0.3, 1) };
        wave3Brush.GradientStops.Add(_wave3TopStop);
        wave3Brush.GradientStops.Add(_wave3MidStop);
        wave3Brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 232, 121, 249), Offset = 1.0 });

        _waveRibbon1 = new Polygon { Fill = wave1Brush, Stretch = Stretch.Fill };
        _waveRibbon2 = new Polygon { Fill = wave2Brush, Stretch = Stretch.Fill };
        _waveRibbon3 = new Polygon { Fill = wave3Brush, Stretch = Stretch.Fill };
        InitWavePoints(_waveRibbon1);
        InitWavePoints(_waveRibbon2);
        InitWavePoints(_waveRibbon3);

        _auroraWaveLayer = new Grid
        {
            IsHitTestVisible = false,
            Children = { _waveRibbon1, _waveRibbon2, _waveRibbon3 },
        };

        // ==================== 0B. 底部向内渐变轮廓光 ====================
        _beamCoreStop = new GradientStop { Offset = 0.0 };
        _beamMidStop = new GradientStop { Offset = 0.48 };
        _beamEdgeStop = new GradientStop { Offset = 1.0 };
        _inwardCenterBeamBrush = new RadialGradientBrush
        {
            Center = new Point(0.50, 1.06),
            GradientOrigin = new Point(0.50, 1.04),
            RadiusX = 0.64,
            RadiusY = 0.68,
        };
        _inwardCenterBeamBrush.GradientStops.Add(_beamCoreStop);
        _inwardCenterBeamBrush.GradientStops.Add(_beamMidStop);
        _inwardCenterBeamBrush.GradientStops.Add(_beamEdgeStop);

        _accentCoreStop = new GradientStop { Offset = 0.0 };
        _accentMidStop = new GradientStop { Offset = 0.45 };
        _accentEdgeStop = new GradientStop { Offset = 1.0 };
        _inwardAccentBeamBrush = new RadialGradientBrush
        {
            Center = new Point(0.46, 1.08),
            GradientOrigin = new Point(0.46, 1.05),
            RadiusX = 0.52,
            RadiusY = 0.48,
        };
        _inwardAccentBeamBrush.GradientStops.Add(_accentCoreStop);
        _inwardAccentBeamBrush.GradientStops.Add(_accentMidStop);
        _inwardAccentBeamBrush.GradientStops.Add(_accentEdgeStop);

        _inwardCenterBeamHost = new Border
        {
            IsHitTestVisible = false,
            Background = _inwardCenterBeamBrush,
        };
        _inwardAccentBeamHost = new Border
        {
            IsHitTestVisible = false,
            Background = _inwardAccentBeamBrush,
        };

        _contourGlowLayer = new Grid
        {
            IsHitTestVisible = false,
            Opacity = 0,
            Children = { _inwardCenterBeamHost, _inwardAccentBeamHost },
        };

        // ==================== 0C. 鼠标中心圆形光效 ====================
        _cursorStop0 = new GradientStop { Offset = 0.0 };
        _cursorStop1 = new GradientStop { Offset = 0.45 };
        var cursorBrush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        cursorBrush.GradientStops.Add(_cursorStop0);
        cursorBrush.GradientStops.Add(_cursorStop1);
        cursorBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 1.0 });

        _cursorGlowEllipse = new Ellipse
        {
            Width = _cursorGlowSize,
            Height = _cursorGlowSize,
            Fill = cursorBrush,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _cursorGlowCanvas = new Canvas
        {
            IsHitTestVisible = false,
            Children = { _cursorGlowEllipse },
        };

        // ==================== 1. 顶部栏：来源图标 + 播放状态胶囊 ====================
        _sourceIconImg = new Image { Width = 18, Height = 18, Stretch = Stretch.UniformToFill };
        _sourceIconFallback = new FontIcon
        {
            Glyph = "\uE8D6",
            FontSize = 13,
            Foreground = _accentCyanBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sourceBtn = CreateIconButton(new Grid { Children = { _sourceIconFallback, _sourceIconImg } }, 28, 26, 8, false);
        sourceBtn.HorizontalAlignment = HorizontalAlignment.Left;
        sourceBtn.Click += (_, _) => ActivateSourceRequested?.Invoke(_instanceId);
        _attachHint(sourceBtn, () => $"切换至 {_sourceName}");

        _statusPillText = new TextBlock
        {
            Text = "已暂停",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statusPill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Background = _subtleFillBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _statusPillText,
        };

        var topBar = new Grid
        {
            Height = 26,
            Children = { sourceBtn, _statusPill },
        };

        // ==================== 2. 中部：左专辑封面+元信息，右居中三行歌词 ====================
        _coverImg = new Image { Stretch = Stretch.UniformToFill };
        _coverFallback = new FontIcon
        {
            Glyph = "\uE8D6",
            FontSize = 24,
            Foreground = _accentCyanBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var coverBox = new Border
        {
            Width = 66,
            Height = 66,
            CornerRadius = new CornerRadius(12),
            Background = _subtleFillBrush,
            BorderBrush = _subtleBorderBrush,
            BorderThickness = new Thickness(1),
            Child = new Grid { Children = { _coverFallback, _coverImg } },
        };

        _titleText = new TextBlock
        {
            Text = "未知曲目",
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _artistText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = _mutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _sourceNameText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = _faintBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var metaStack = new StackPanel
        {
            Spacing = 2,
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

        _lyricPrev = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = _faintBrush,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _lyricCurr = new TextBlock
        {
            Text = "暂无同步歌词",
            FontSize = 13,
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
            FontSize = 11,
            Foreground = _faintBrush,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var middleRight = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 0, 4, 0),
            Children = { _lyricPrev, _lyricCurr, _lyricNext },
        };

        var middleGrid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        middleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        middleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        Grid.SetColumn(middleLeft, 0);
        Grid.SetColumn(middleRight, 1);
        middleGrid.Children.Add(middleLeft);
        middleGrid.Children.Add(middleRight);

        // ==================== 3. 播放控制行：左对齐闹钟 + 居中播放控制 ====================
        _alarmGlyph = new FontIcon
        {
            Glyph = "\uE916",
            FontSize = 14,
            Foreground = _textBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var alarmBtn = CreateIconButton(_alarmGlyph, 30, 26, 8, false);
        alarmBtn.HorizontalAlignment = HorizontalAlignment.Left;
        alarmBtn.VerticalAlignment = VerticalAlignment.Center;
        alarmBtn.Click += (_, _) => ToggleAlarmPopup();
        _attachHint(alarmBtn, () => _hasTimer
            ? $"定时暂停：剩余 {FormatTime(_timerRemaining)}（点击修改/取消）"
            : $"为 {_sourceName} 设置定时暂停");

        _alarmInlineText = new TextBlock
        {
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _accentCyanBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _alarmInlineBadge = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1, 6, 1),
            Background = _subtleFillBrush,
            Opacity = 0,
            Height = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(34, 0, 0, 0),
            IsHitTestVisible = false,
            Child = _alarmInlineText,
        };

        var prevBtn = CreateIconButton(new FontIcon { Glyph = "\uE892", FontSize = 14, Foreground = _textBrush }, 34, 34, 17, false);
        prevBtn.Click += (_, _) => PreviousRequested?.Invoke(_instanceId);
        _attachHint(prevBtn, () => "上一曲");

        _playPauseGlyph = new FontIcon { Glyph = "\uE768", FontSize = 16, Foreground = _textBrush };
        var playBtn = CreateIconButton(_playPauseGlyph, 38, 38, 19, true);
        playBtn.Click += (_, _) => PlayPauseRequested?.Invoke(_instanceId);
        _attachHint(playBtn, () => _isPlaying ? "暂停" : "播放");

        var nextBtn = CreateIconButton(new FontIcon { Glyph = "\uE893", FontSize = 14, Foreground = _textBrush }, 34, 34, 17, false);
        nextBtn.Click += (_, _) => NextRequested?.Invoke(_instanceId);
        _attachHint(nextBtn, () => "下一曲");

        var transportCenter = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { prevBtn, playBtn, nextBtn },
        };

        var transportRow = new Grid
        {
            Children = { alarmBtn, _alarmInlineBadge, transportCenter },
        };

        // ==================== 4. 进度条行 ====================
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
            Height = 18,
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { trackBg, _progressFill, _progressThumb },
        };
        _progressHitArea.PointerPressed += OnProgressPressed;
        _progressHitArea.PointerMoved += OnProgressMoved;
        _progressHitArea.PointerReleased += OnProgressReleased;
        _progressHitArea.PointerCaptureLost += OnProgressCaptureLost;
        _progressHitArea.SizeChanged += (_, _) => UpdateProgressVisual(_lastRatio);

        _elapsedText = new TextBlock { Text = "00:00", FontSize = 10.5, Foreground = _faintBrush, VerticalAlignment = VerticalAlignment.Center };
        _durationText = new TextBlock { Text = "00:00", FontSize = 10.5, Foreground = _faintBrush, VerticalAlignment = VerticalAlignment.Center };

        var progressRow = new Grid { ColumnSpacing = 10, Height = 18 };
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_elapsedText, 0);
        Grid.SetColumn(_progressHitArea, 1);
        Grid.SetColumn(_durationText, 2);
        progressRow.Children.Add(_elapsedText);
        progressRow.Children.Add(_progressHitArea);
        progressRow.Children.Add(_durationText);

        // ==================== 5. 卡片内分实例定时暂停弹出菜单（带滚动适配） ====================
        _alarmPopupList = new StackPanel { Spacing = 2, Padding = new Thickness(5) };
        var alarmScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 128,
            Content = _alarmPopupList,
        };
        _alarmPopup = new Border
        {
            Width = 156,
            MaxHeight = 132,
            Height = 0,
            Opacity = 0,
            IsHitTestVisible = false,
            CornerRadius = new CornerRadius(10),
            Background = _popupBgBrush,
            BorderBrush = _subtleBorderBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(14, 0, 0, 52),
            Child = alarmScroll,
        };
        _alarmPopup.Tapped += (_, e) => e.Handled = true;

        var contentGrid = new Grid
        {
            Padding = new Thickness(16, 10, 16, 10),
            RowSpacing = 2,
        };
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(topBar, 0);
        Grid.SetRow(middleGrid, 1);
        Grid.SetRow(transportRow, 2);
        Grid.SetRow(progressRow, 3);
        contentGrid.Children.Add(topBar);
        contentGrid.Children.Add(middleGrid);
        contentGrid.Children.Add(transportRow);
        contentGrid.Children.Add(progressRow);

        var innerHost = new Grid
        {
            Height = CardHeight,
            Children = { _auroraWaveLayer, _contourGlowLayer, _cursorGlowCanvas, contentGrid, _alarmPopup },
        };

        var outerCard = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = _cardBgBrush,
            BorderBrush = _cardBorderBrush,
            BorderThickness = new Thickness(1),
            Child = innerHost,
        };

        outerCard.PointerEntered += (_, e) =>
        {
            var pt = e.GetCurrentPoint(innerHost).Position;
            _cursorRawX = pt.X;
            _cursorRawY = pt.Y;
            if (!_isPointerInCard)
            {
                _cursorSmoothX = pt.X;
                _cursorSmoothY = pt.Y;
            }
            _isPointerInCard = true;
        };
        outerCard.PointerMoved += (_, e) =>
        {
            var pt = e.GetCurrentPoint(innerHost).Position;
            _cursorRawX = pt.X;
            _cursorRawY = pt.Y;
            _isPointerInCard = true;
        };
        outerCard.PointerExited += (_, _) => _isPointerInCard = false;
        outerCard.Tapped += (_, _) => CloseAlarmPopup();

        Content = outerCard;
        ApplyThemeColors();
    }

    public void ApplyThemeColors()
    {
        bool light = _theme.IsLight;
        _cardBgBrush.Color = light ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(24, 255, 255, 255);
        _cardBorderBrush.Color = light ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(36, 255, 255, 255);

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
        _popupBgBrush.Color = light ? Color.FromArgb(248, 250, 252, 255) : Color.FromArgb(246, 24, 28, 36);

        _wave1TopStop.Color = light ? Color.FromArgb(185, 56, 189, 248) : Color.FromArgb(175, 34, 211, 238);
        _wave1MidStop.Color = light ? Color.FromArgb(100, 45, 212, 191) : Color.FromArgb(92, 45, 212, 191);
        _wave2TopStop.Color = light ? Color.FromArgb(180, 129, 140, 248) : Color.FromArgb(175, 129, 140, 248);
        _wave2MidStop.Color = light ? Color.FromArgb(95, 168, 85, 247) : Color.FromArgb(88, 168, 85, 247);
        _wave3TopStop.Color = light ? Color.FromArgb(160, 232, 121, 249) : Color.FromArgb(155, 217, 70, 239);
        _wave3MidStop.Color = light ? Color.FromArgb(80, 56, 189, 248) : Color.FromArgb(75, 56, 189, 248);

        _cursorStop0.Color = light ? Color.FromArgb(135, 125, 211, 252) : Color.FromArgb(120, 224, 242, 254);
        _cursorStop1.Color = light ? Color.FromArgb(55, 165, 180, 252) : Color.FromArgb(48, 125, 211, 252);
        UpdateContourGradientColors();
    }

    public void SetVisualConfig(VisualEffectMode mode, double contourReactivity, bool cursorGlowEnabled, double cursorGlowSize, double cursorGlowOpacity)
    {
        _visualMode = mode;
        _contourReactivity = Math.Clamp(contourReactivity, 0.0, 5.0);
        _auroraWaveLayer.Opacity = mode == VisualEffectMode.AuroraWave ? 1 : 0;
        _contourGlowLayer.Opacity = mode == VisualEffectMode.AlbumContour ? 1 : 0;

        _cursorGlowEnabled = cursorGlowEnabled;
        _cursorGlowSize = Math.Clamp(cursorGlowSize, 40.0, 320.0);
        _cursorGlowOpacity = Math.Clamp(cursorGlowOpacity, 0.0, 1.0);
        _cursorGlowEllipse.Width = _cursorGlowSize;
        _cursorGlowEllipse.Height = _cursorGlowSize;
        if (!_cursorGlowEnabled || _cursorGlowOpacity <= 0.005)
        {
            _cursorGlowEllipse.Opacity = 0;
        }
    }

    public void UpdateData(MediaInstanceInfo info, LyricTriplet lyrics, bool hasTimer, TimeSpan timerRemaining)
    {
        _isPlaying = info.IsPlaying;
        _sourceName = info.SourceDisplayName;
        _duration = info.Duration;

        _titleText.Text = string.IsNullOrWhiteSpace(info.Title) ? "未知曲目" : info.Title;
        _artistText.Text = string.IsNullOrWhiteSpace(info.Artist) ? info.SourceDisplayName : info.Artist;
        _sourceNameText.Text = info.SourceDisplayName;
        _playPauseGlyph.Glyph = info.IsPlaying ? "\uE769" : "\uE768";

        if (info.IsPlaying)
        {
            _statusPill.Background = _playingPillBgBrush;
            _statusPillText.Foreground = _playingPillFgBrush;
            _statusPillText.Text = "播放中";
        }
        else
        {
            _statusPill.Background = _subtleFillBrush;
            _statusPillText.Foreground = _mutedBrush;
            _statusPillText.Text = "已暂停";
        }

        _lyricPrev.Text = lyrics.Previous;
        _lyricCurr.Text = string.IsNullOrWhiteSpace(lyrics.Current) ? "暂无同步歌词" : lyrics.Current;
        _lyricNext.Text = lyrics.Next;

        if (!_isSeeking)
        {
            var pos = info.EstimatedPosition;
            _elapsedText.Text = FormatTime(pos);
            _durationText.Text = info.Duration > TimeSpan.Zero ? FormatTime(info.Duration) : "--:--";
            double ratio = info.Duration > TimeSpan.Zero ? Math.Clamp(pos.TotalSeconds / info.Duration.TotalSeconds, 0, 1) : 0;
            UpdateProgressVisual(ratio);
        }

        UpdateTimerDisplay(hasTimer, timerRemaining);
        UpdateCoverImage(info.ThumbnailBytes);
        UpdateAppIconImage(info.AppIconBytes);
    }

    public void UpdateTimerDisplay(bool hasTimer, TimeSpan remaining)
    {
        _hasTimer = hasTimer && remaining > TimeSpan.Zero;
        _timerRemaining = _hasTimer ? remaining : TimeSpan.Zero;

        if (_hasTimer)
        {
            _alarmGlyph.Foreground = _accentCyanBrush;
            _alarmInlineText.Text = FormatTime(remaining);
            _alarmInlineBadge.Opacity = 1;
            _alarmInlineBadge.Height = 18;
        }
        else
        {
            _alarmGlyph.Foreground = _textBrush;
            _alarmInlineBadge.Opacity = 0;
            _alarmInlineBadge.Height = 0;
        }
    }

    public void TickVisual((float Master, float Left, float Right) peak)
    {
        double targetMaster = _isPlaying ? Math.Clamp(peak.Master * 2.2, 0.08, 1.0) : 0.03;
        double targetLeft = _isPlaying ? Math.Clamp(peak.Left * 2.2, 0.05, 1.0) : 0.02;
        double targetRight = _isPlaying ? Math.Clamp(peak.Right * 2.2, 0.05, 1.0) : 0.02;

        _smoothMaster += (targetMaster - _smoothMaster) * (targetMaster > _smoothMaster ? 0.35 : 0.12);
        _slowMaster += (_smoothMaster - _slowMaster) * 0.05;
        _smoothLeft += (targetLeft - _smoothLeft) * (targetLeft > _smoothLeft ? 0.35 : 0.12);
        _smoothRight += (targetRight - _smoothRight) * (targetRight > _smoothRight ? 0.35 : 0.12);

        // 鼠标跟随圆形光效平滑插值
        double targetCursorOp = _isPointerInCard ? 1.0 : 0.0;
        _cursorSmoothOpacity += (targetCursorOp - _cursorSmoothOpacity) * 0.22;
        _cursorSmoothX += (_cursorRawX - _cursorSmoothX) * 0.30;
        _cursorSmoothY += (_cursorRawY - _cursorSmoothY) * 0.30;

        if (_cursorGlowEnabled && _cursorSmoothOpacity > 0.01 && _cursorGlowOpacity > 0.005)
        {
            double half = _cursorGlowSize * 0.5;
            Canvas.SetLeft(_cursorGlowEllipse, _cursorSmoothX - half);
            Canvas.SetTop(_cursorGlowEllipse, _cursorSmoothY - half);
            _cursorGlowEllipse.Opacity = _cursorSmoothOpacity * _cursorGlowOpacity;
        }
        else
        {
            _cursorGlowEllipse.Opacity = 0;
        }

        if (_visualMode == VisualEffectMode.AuroraWave)
        {
            UpdateAuroraWaves();
        }
        else if (_visualMode == VisualEffectMode.AlbumContour)
        {
            UpdateAlbumContourGlow();
        }
    }

    private void UpdateAuroraWaves()
    {
        double speedBoost = 1.0 + _smoothMaster * 2.4;
        _phase1 += 0.042 * speedBoost;
        _phase2 -= 0.031 * speedBoost;
        _phase3 += 0.055 * (1.0 + (_smoothLeft + _smoothRight) * 1.4);

        double ampBase = 16.0 + _smoothMaster * 34.0;
        double ampLeft = 10.0 + _smoothLeft * 24.0;
        double ampRight = 10.0 + _smoothRight * 24.0;

        UpdateSingleWave(_waveRibbon1, _phase1, 108.0 - _smoothMaster * 22.0, ampBase, ampLeft * 0.5, 2.0, 4.0, 26.0);
        UpdateSingleWave(_waveRibbon2, _phase2, 126.0 - _smoothLeft * 20.0, ampLeft * 0.9, ampBase * 0.45, 2.5, 3.5, 22.0);
        UpdateSingleWave(_waveRibbon3, _phase3, 144.0 - _smoothRight * 18.0, ampRight * 0.85, ampLeft * 0.4, 3.0, 5.0, 18.0);
    }

    private void UpdateSingleWave(Polygon poly, double phase, double baseLine, double amp1, double amp2, double freq1, double freq2, double maxPush)
    {
        var pts = poly.Points;
        if (pts.Count < WavePointsCount + 2) return;

        double viewW = ActualWidth > 10 ? ActualWidth : 780.0;
        double viewH = ActualHeight > 10 ? ActualHeight : CardHeight;
        double mx = (_cursorSmoothX / Math.Max(1, viewW)) * 560.0;
        double my = (_cursorSmoothY / Math.Max(1, viewH)) * 208.0;
        double evadeStrength = _cursorSmoothOpacity;

        for (int i = 0; i < WavePointsCount; i++)
        {
            double normX = (double)i / (WavePointsCount - 1);
            double x = normX * 560.0;
            double y = baseLine
                     + Math.Sin(normX * Math.PI * freq1 + phase) * amp1
                     + Math.Cos(normX * Math.PI * freq2 - phase * 1.25) * amp2;

            if (evadeStrength > 0.01)
            {
                double dx = x - mx;
                double dy = y - my;
                double gaussX = Math.Exp(-(dx * dx) / (2.0 * 92.0 * 92.0));
                double gaussY = Math.Exp(-(dy * dy) / (2.0 * 84.0 * 84.0));
                double smoothDirY = Math.Tanh((dy + 18.0) / 44.0);
                y += smoothDirY * gaussX * gaussY * maxPush * evadeStrength;
            }

            if (y < 14.0) y = 14.0 - 10.0 * Math.Tanh((14.0 - y) / 18.0);
            else if (y > 202.0) y = 202.0 + 8.0 * Math.Tanh((y - 202.0) / 18.0);

            pts[i] = new Point(x, y);
        }
    }

    private void UpdateAlbumContourGlow()
    {
        _curCoverLeft = LerpColor(_curCoverLeft, _targetCoverLeft, 0.14);
        _curCoverCenter = LerpColor(_curCoverCenter, _targetCoverCenter, 0.14);
        _curCoverRight = LerpColor(_curCoverRight, _targetCoverRight, 0.14);
        UpdateContourGradientColors();

        double react = _contourReactivity;
        double beat = Math.Max(0, _smoothMaster - _slowMaster * 0.78);
        double energy = _smoothMaster * 0.65 + beat * 1.65;

        if (react > 0.001)
        {
            _phase1 += 0.022 + energy * 0.065 * Math.Min(react, 3.5);
        }

        double viewW = ActualWidth > 10 ? ActualWidth : 780.0;
        double viewH = ActualHeight > 10 ? ActualHeight : CardHeight;
        double normMx = Math.Clamp(_cursorSmoothX / Math.Max(1, viewW), 0, 1);
        double normMy = Math.Clamp(_cursorSmoothY / Math.Max(1, viewH), 0, 1);

        double evadeX = (0.5 - normMx) * 0.24 * _cursorSmoothOpacity;
        double bottomProximity = Math.Max(0, (normMy - 0.35) / 0.65) * (1.0 - Math.Min(1.0, Math.Abs(normMx - 0.5) * 1.8));
        double evadeYCompress = bottomProximity * 0.16 * _cursorSmoothOpacity;

        double driftX = react > 0.001 ? Math.Sin(_phase1) * Math.Min(0.11, 0.035 * react) : 0.0;
        double centerX = Math.Clamp(0.50 + driftX + evadeX, 0.18, 0.82);

        double baseY = react > 0.001 ? Math.Max(0.42, 0.56 - Math.Min(0.14, react * 0.03)) : 0.58;
        double radiusY = Math.Clamp(baseY + energy * 0.24 * react - evadeYCompress, 0.34, 1.18);
        double radiusX = Math.Clamp(0.58 + (_smoothLeft + _smoothRight) * 0.10 * react, 0.48, 1.08);

        _inwardCenterBeamBrush.Center = new Point(centerX, 1.06);
        _inwardCenterBeamBrush.GradientOrigin = new Point(centerX, 1.04);
        _inwardCenterBeamBrush.RadiusX = radiusX;
        _inwardCenterBeamBrush.RadiusY = radiusY;
        _inwardCenterBeamHost.Opacity = react > 0.001
            ? Math.Clamp(0.56 + energy * 0.28 * react, 0.45, 1.0)
            : 0.72;

        double accentX = Math.Clamp(0.48 - driftX * 1.25 + evadeX * 1.15, 0.16, 0.84);
        _inwardAccentBeamBrush.Center = new Point(accentX, 1.08);
        _inwardAccentBeamBrush.GradientOrigin = new Point(accentX, 1.05);
        _inwardAccentBeamBrush.RadiusX = Math.Clamp(0.46 + _smoothLeft * 0.15 * react, 0.40, 0.96);
        _inwardAccentBeamBrush.RadiusY = Math.Clamp(0.40 + (_smoothRight * 0.18 + beat * 0.32) * react - evadeYCompress * 0.8, 0.26, 1.05);
        _inwardAccentBeamHost.Opacity = react > 0.001
            ? Math.Clamp(0.44 + (_smoothLeft + _smoothRight + beat) * 0.18 * react, 0.36, 0.98)
            : 0.58;
    }

    private void UpdateContourGradientColors()
    {
        bool light = _theme.IsLight;
        byte coreAlpha = light ? (byte)160 : (byte)150;
        byte midAlpha = light ? (byte)75 : (byte)70;

        _beamCoreStop.Color = WithAlpha(_curCoverCenter, coreAlpha);
        _beamMidStop.Color = WithAlpha(_curCoverLeft, midAlpha);
        _beamEdgeStop.Color = WithAlpha(_curCoverLeft, 0);

        _accentCoreStop.Color = WithAlpha(_curCoverRight, (byte)(coreAlpha - 20));
        _accentMidStop.Color = WithAlpha(_curCoverCenter, (byte)(midAlpha - 15));
        _accentEdgeStop.Color = WithAlpha(_curCoverCenter, 0);
    }

    private void ToggleAlarmPopup()
    {
        if (_alarmPopupOpen)
        {
            CloseAlarmPopup();
            return;
        }
        _hideHint();
        RebuildAlarmPopup();
        _alarmPopupOpen = true;
        _alarmPopup.Height = double.NaN;
        _alarmPopup.Opacity = 1;
        _alarmPopup.IsHitTestVisible = true;
    }

    public void CloseAlarmPopup()
    {
        if (!_alarmPopupOpen) return;
        _alarmPopupOpen = false;
        _alarmPopup.Opacity = 0;
        _alarmPopup.Height = 0;
        _alarmPopup.IsHitTestVisible = false;
    }

    private void RebuildAlarmPopup()
    {
        _alarmPopupList.Children.Clear();
        _alarmPopupList.Children.Add(new TextBlock
        {
            Text = "定时暂停此实例",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _mutedBrush,
            Margin = new Thickness(6, 2, 6, 4),
        });

        foreach (var (label, mins) in new (string, int)[]
        {
            ("关闭定时", 0),
            ("15 分钟后", 15),
            ("30 分钟后", 30),
            ("45 分钟后", 45),
            ("60 分钟后", 60),
        })
        {
            int m = mins;
            bool isCancel = m == 0;
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 5, 8, 5),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Content = new TextBlock
                {
                    Text = isCancel && _hasTimer ? $"关闭定时（剩 {FormatTime(_timerRemaining)}）" : label,
                    FontSize = 11.5,
                    Foreground = isCancel && _hasTimer ? _accentCyanBrush : _textBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            btn.PointerEntered += (_, _) => { btn.Background = _hoverFillBrush; btn.BorderBrush = _hoverBorderBrush; };
            btn.PointerExited += (_, _) => { btn.Background = new SolidColorBrush(Colors.Transparent); btn.BorderBrush = new SolidColorBrush(Colors.Transparent); };
            btn.Tapped += (_, e) => e.Handled = true;
            btn.Click += (_, _) =>
            {
                SetSleepTimerRequested?.Invoke(_instanceId, m);
                CloseAlarmPopup();
            };
            _alarmPopupList.Children.Add(btn);
        }
    }

    private void UpdateProgressVisual(double ratio)
    {
        _lastRatio = Math.Clamp(ratio, 0, 1);
        double w = _progressHitArea.ActualWidth;
        if (w <= 0) return;

        double fillW = w * _lastRatio;
        _progressFill.Width = fillW;
        double thumbLeft = Math.Clamp(fillW - _progressThumb.Width / 2.0, 0, Math.Max(0, w - _progressThumb.Width));
        _progressThumb.Margin = new Thickness(thumbLeft, 0, 0, 0);
    }

    private double ComputeSeekRatio(PointerRoutedEventArgs e)
    {
        double w = _progressHitArea.ActualWidth;
        if (w <= 0) return 0;
        var pt = e.GetCurrentPoint(_progressHitArea).Position;
        return Math.Clamp(pt.X / w, 0, 1);
    }

    private void OnProgressPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_progressHitArea).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (_duration <= TimeSpan.Zero) return;
        _isSeeking = true;
        _progressHitArea.CapturePointer(e.Pointer);
        double ratio = ComputeSeekRatio(e);
        UpdateProgressVisual(ratio);
        _elapsedText.Text = FormatTime(TimeSpan.FromSeconds(_duration.TotalSeconds * ratio));
    }

    private void OnProgressMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking) return;
        e.Handled = true;
        double ratio = ComputeSeekRatio(e);
        UpdateProgressVisual(ratio);
        if (_duration > TimeSpan.Zero)
        {
            _elapsedText.Text = FormatTime(TimeSpan.FromSeconds(_duration.TotalSeconds * ratio));
        }
    }

    private void OnProgressReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking) return;
        e.Handled = true;
        double ratio = ComputeSeekRatio(e);
        _isSeeking = false;
        _progressHitArea.ReleasePointerCapture(e.Pointer);
        UpdateProgressVisual(ratio);
        SeekRatioRequested?.Invoke(_instanceId, ratio);
    }

    private void OnProgressCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isSeeking = false;
        UpdateProgressVisual(_lastRatio);
    }

    private async void UpdateCoverImage(byte[]? bytes)
    {
        if (ReferenceEquals(bytes, _lastCoverBytes)) return;
        _lastCoverBytes = bytes;
        if (bytes is null || bytes.Length == 0)
        {
            _coverImg.Source = null;
            _coverFallback.Opacity = 1;
            _targetCoverLeft = Color.FromArgb(255, 56, 189, 248);
            _targetCoverCenter = Color.FromArgb(255, 168, 85, 247);
            _targetCoverRight = Color.FromArgb(255, 236, 72, 153);
            return;
        }
        try
        {
            var bmp = await BytesToBitmapAsync(bytes);
            if (!ReferenceEquals(bytes, _lastCoverBytes)) return;
            _coverImg.Source = bmp;
            _coverFallback.Opacity = 0;
            var palette = await ExtractCoverPaletteAsync(bytes);
            if (ReferenceEquals(bytes, _lastCoverBytes))
            {
                _targetCoverLeft = palette.Left;
                _targetCoverCenter = palette.Center;
                _targetCoverRight = palette.Right;
            }
        }
        catch { _coverFallback.Opacity = 1; }
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
        catch { _sourceIconFallback.Opacity = 1; }
    }

    private Button CreateIconButton(UIElement content, double width, double height, double cornerRadius, bool isPrimary)
    {
        var btn = new Button
        {
            Width = width,
            Height = height,
            Padding = new Thickness(0),
            Background = isPrimary ? _subtleFillBrush : new SolidColorBrush(Colors.Transparent),
            BorderBrush = isPrimary ? _subtleBorderBrush : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(cornerRadius),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = content,
        };
        btn.Tapped += (_, e) => e.Handled = true;
        btn.PointerEntered += (_, _) => { btn.Background = _hoverFillBrush; btn.BorderBrush = _hoverBorderBrush; };
        btn.PointerExited += (_, _) =>
        {
            btn.Background = isPrimary ? _subtleFillBrush : new SolidColorBrush(Colors.Transparent);
            btn.BorderBrush = isPrimary ? _subtleBorderBrush : new SolidColorBrush(Colors.Transparent);
        };
        btn.PointerPressed += (_, _) => btn.Background = _pressedFillBrush;
        btn.PointerReleased += (_, _) => btn.Background = _hoverFillBrush;
        return btn;
    }

    private static void InitWavePoints(Polygon poly)
    {
        poly.Points.Clear();
        for (int i = 0; i < WavePointsCount; i++)
        {
            poly.Points.Add(new Point(i * (560.0 / (WavePointsCount - 1)), 100));
        }
        poly.Points.Add(new Point(560, 220));
        poly.Points.Add(new Point(0, 220));
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

    private static async Task<(Color Left, Color Center, Color Right)> ExtractCoverPaletteAsync(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform { ScaledWidth = 24, ScaledHeight = 24 };
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var raw = pixelData.DetachPixelData();
            long rL = 0, gL = 0, bL = 0, cL = 0;
            long rC = 0, gC = 0, bC = 0, cC = 0;
            long rR = 0, gR = 0, bR = 0, cR = 0;

            for (int y = 2; y < 22; y++)
            {
                for (int x = 0; x < 24; x++)
                {
                    int idx = (y * 24 + x) * 4;
                    if (idx + 3 >= raw.Length) continue;
                    byte b = raw[idx], g = raw[idx + 1], r = raw[idx + 2], a = raw[idx + 3];
                    if (a < 100) continue;
                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    int weight = (max > 35 && max < 245) ? (1 + (max - min) / 16) : 1;

                    if (x < 8) { rL += r * weight; gL += g * weight; bL += b * weight; cL += weight; }
                    else if (x < 16) { rC += r * weight; gC += g * weight; bC += b * weight; cC += weight; }
                    else { rR += r * weight; gR += g * weight; bR += b * weight; cR += weight; }
                }
            }

            Color colL = cL > 0 ? BoostVibrant((byte)(rL / cL), (byte)(gL / cL), (byte)(bL / cL)) : Color.FromArgb(255, 56, 189, 248);
            Color colC = cC > 0 ? BoostVibrant((byte)(rC / cC), (byte)(gC / cC), (byte)(bC / cC)) : Color.FromArgb(255, 168, 85, 247);
            Color colR = cR > 0 ? BoostVibrant((byte)(rR / cR), (byte)(gR / cR), (byte)(bR / cR)) : Color.FromArgb(255, 236, 72, 153);
            return (colL, colC, colR);
        }
        catch
        {
            return (Color.FromArgb(255, 56, 189, 248), Color.FromArgb(255, 168, 85, 247), Color.FromArgb(255, 236, 72, 153));
        }
    }

    private static Color BoostVibrant(byte r, byte g, byte b)
    {
        double avg = (r + g + b) / 3.0;
        double nr = avg + (r - avg) * 1.45;
        double ng = avg + (g - avg) * 1.45;
        double nb = avg + (b - avg) * 1.45;
        double max = Math.Max(nr, Math.Max(ng, nb));
        if (max < 130 && max > 1)
        {
            double lift = 155.0 / max;
            nr *= lift; ng *= lift; nb *= lift;
        }
        return Color.FromArgb(255, (byte)Math.Clamp(nr, 30, 255), (byte)Math.Clamp(ng, 30, 255), (byte)Math.Clamp(nb, 30, 255));
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color LerpColor(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)Math.Clamp(a.R + (b.R - a.R) * t, 0, 255),
        (byte)Math.Clamp(a.G + (b.G - a.G) * t, 0, 255),
        (byte)Math.Clamp(a.B + (b.B - a.B) * t, 0, 255));

    private static string FormatTime(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }

    private Color Neutral(byte alpha) => _theme.IsLight
        ? Color.FromArgb(alpha, 0, 0, 0)
        : Color.FromArgb(alpha, 255, 255, 255);
}
