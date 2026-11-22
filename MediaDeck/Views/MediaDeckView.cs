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
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using WinIsland.Core;

namespace MediaDeck.Views;

public enum VisualEffectMode
{
    AuroraWave = 0,
    AlbumContour = 1,
    Off = 2,
}

/// <summary>
/// 声枢（MediaDeck）岛体视图：
///   1. 双视觉特效模式（可在设置中切换）：
///      · 极光波浪（AuroraWave）：三层正弦谐波极光リボン随左右声道与主音频频率起伏流动，无矩形裁切边；
///      · 专辑轮廓光（AlbumContour）：提取专辑封面多主色合成渐变轮廓光，遵循物理光学“底部最亮最厚、自下而上依次衰减”，随频谱律动；
///   2. 胶囊形态：有同步歌词且超长时自动平滑滚动，无歌词显示歌名时保持“……”省略；右侧使用 Quietify 5柱动态波形（播放）/ 5圆点（暂停）；
///   3. 卡片形态：播放控制行最右侧对齐喇叭图标，悬停自下而上弹出音量条（移开平滑收回），悬停期间滚轮重置为步进 2 调节音量，支持拖拽调节。
/// </summary>
public sealed class MediaDeckView : UserControl, IMorphView
{
    private const double BackAmplitude = 0.45;
    private const double CompactHeight = 40;
    private const double ExpandedHeight = 208;
    private const int WavePointsCount = 72;

    // Quietify SVG 关键帧（viewBox 0 0 135 140）
    private static readonly double[] QuietifyHeights = [120, 110, 100, 90, 80, 70, 60, 50, 40, 140, 120];
    private static readonly double[] QuietifyYs = [10, 15, 20, 25, 30, 35, 40, 45, 50, 0, 10];
    private static readonly double[] QuietifyPhases = [0.50, 0.25, 0.00, 0.25, 0.50];

    private readonly IIslandTheme _theme;
    private readonly Func<(float Master, float Left, float Right)> _audioPeakProvider;
    private readonly Func<(int Volume, bool Muted)> _masterVolumeProvider;

    // 共享主题画刷
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

    // ==================== 视觉特效层 1：极光流光波浪（Polygon 正弦波浪带） ====================
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

    // ==================== 视觉特效层 2：图3同款底部向容器中心投射的渐变光线（支持切歌重新染色动画 + 鼠标避让） ====================
    private enum ContourDyeOrigin { BottomUp, FromPrevButton, FromNextButton }

    private readonly Grid _contourGlowLayer;
    private readonly Border _inwardCenterBeamHost;
    private readonly Border _inwardAccentBeamHost;
    private readonly Border _dyeOverlayHost;
    private RadialGradientBrush _inwardCenterBeamBrush;
    private RadialGradientBrush _inwardAccentBeamBrush;
    private Color _lastRenderedLeft;
    private Color _lastRenderedCenter;
    private Color _lastRenderedRight;
    private bool _lastRenderedLight;

    // 轮廓光重新染色过渡动画状态（时长 < 1s）
    private bool _dyeActive;
    private DateTimeOffset _dyeStart;
    private ContourDyeOrigin _dyeOrigin = ContourDyeOrigin.BottomUp;
    private Point _dyeOriginNorm = new(0.50, 1.06);
    private Color _dyeFromLeft = Color.FromArgb(255, 56, 189, 248);
    private Color _dyeFromCenter = Color.FromArgb(255, 168, 85, 247);
    private Color _dyeFromRight = Color.FromArgb(255, 236, 72, 153);
    private ContourDyeOrigin? _pendingDyeOrigin;
    private DateTimeOffset _pendingDyeSetAt = DateTimeOffset.MinValue;
    private string _lastTrackKey = "";
    private string _lastCompactTrackKey = "";

    // ==================== 全局鼠标中心圆形光效 + 避让物理场 ====================
    private readonly Canvas _cursorGlowCanvas;
    private readonly Ellipse _cursorGlowEllipse;
    private readonly GradientStop _cursorStop0;
    private readonly GradientStop _cursorStop1;
    private bool _isPointerInRoot;
    private double _cursorRawX = 280;
    private double _cursorRawY = 100;
    private double _cursorSmoothX = 280;
    private double _cursorSmoothY = 100;
    private double _cursorSmoothOpacity;
    private bool _cursorGlowEnabled = true;
    private double _cursorGlowSize = 96.0;
    private double _cursorGlowOpacity = 0.30;
    private double _contourReactivity = 0.50;

    // 封面提取的目标主色与当前平滑主色
    private Color _targetCoverLeft = Color.FromArgb(255, 56, 189, 248);
    private Color _targetCoverCenter = Color.FromArgb(255, 168, 85, 247);
    private Color _targetCoverRight = Color.FromArgb(255, 236, 72, 153);
    private Color _curCoverLeft = Color.FromArgb(255, 56, 189, 248);
    private Color _curCoverCenter = Color.FromArgb(255, 168, 85, 247);
    private Color _curCoverRight = Color.FromArgb(255, 236, 72, 153);

    private readonly DispatcherQueueTimer? _auroraTimer;
    private double _auroraPhase1;
    private double _auroraPhase2;
    private double _auroraPhase3;
    private double _smoothMaster;
    private double _slowMaster;
    private double _smoothLeft;
    private double _smoothRight;
    private VisualEffectMode _visualMode = VisualEffectMode.AuroraWave;
    private bool _isPlaying;
    private bool _compactIsPlaying;

    // 形态变形动画状态
    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progress;

    // 翻页切换动画状态
    private readonly DispatcherQueueTimer? _pageAnimTimer;
    private readonly CompositeTransform _pageTx = new();
    private DateTimeOffset _pageAnimStart;
    private double _pageAnimFromX;
    private Grid _middleGrid = null!;

    // 两层容器
    private readonly Grid _compactLayer;
    private readonly Grid _expandedLayer;

    // ==================== 紧凑态（胶囊形态）控件 ====================
    private readonly Image _compactCoverImg;
    private readonly FontIcon _compactCoverFallback;
    private readonly Grid _compactTextViewport;
    private readonly RectangleGeometry _compactClipGeo = new();
    private readonly TranslateTransform _compactMarqueeTx = new();
    private readonly TextBlock _compactTitle;
    private readonly Rectangle[] _compactQuietifyBars = new Rectangle[5];
    private bool _compactIsLyricMode;
    private string _lastCompactText = "";
    private double _compactTextFullWidth;
    private double _compactMarqueeOffset;
    private double _compactMarqueePauseSec;
    private bool _compactMarqueeResetting;

    // ==================== 展开态顶部栏 ====================
    private readonly Button _sourceBtn;
    private readonly Image _sourceIconImg;
    private readonly FontIcon _sourceIconFallback;
    private readonly StackPanel _dotsPanel;
    private readonly Button _outputBtn;
    private readonly FontIcon _outputGlyph;
    private readonly Border _btBadge;

    // ==================== 展开态中部 ====================
    private readonly Image _coverImg;
    private readonly FontIcon _coverFallback;
    private readonly TextBlock _titleText;
    private readonly TextBlock _artistText;
    private readonly TextBlock _sourceNameText;
    private readonly TextBlock _lyricPrev;
    private readonly TextBlock _lyricCurr;
    private readonly TextBlock _lyricNext;

    // ==================== 展开态底部：左侧闹钟定时 + 居中播放控制 + 右侧喇叭音量 ====================
    private readonly Button _alarmBtn;
    private readonly FontIcon _alarmBtnGlyph;
    private readonly Border _alarmInlineBadge;
    private readonly TextBlock _alarmInlineText;
    private readonly Border _alarmPopup;
    private readonly StackPanel _alarmPopupList;
    private bool _alarmPopupOpen;
    private string? _currentInstanceId;
    private bool _currentHasTimer;
    private TimeSpan _currentTimerRemaining;

    private readonly Button _prevBtn;
    private readonly Button _nextBtn;
    private readonly FontIcon _playPauseGlyph;
    private readonly Button _volumeBtn;
    private readonly FontIcon _volumeBtnGlyph;
    private readonly Border _volumePopupHost;
    private readonly TextBlock _volumePctText;
    private readonly Grid _volumeTrackHitArea;
    private readonly Border _volumeFillBar;
    private readonly Border _volumeThumb;
    private bool _isHoveringVolBtn;
    private bool _isHoveringVolPopup;
    private bool _isDraggingVolBar;
    private DateTimeOffset _volHoverLeaveTime = DateTimeOffset.MinValue;
    private double _volPopupProgress; // 0 = 收起, 1 = 完全展开
    private int _currentVolumePct = 50;
    private bool _currentMasterMuted;

    // ==================== 展开态最底行进度条 ====================
    private readonly Grid _progressHitArea;
    private readonly Border _progressFill;
    private readonly Border _progressThumb;
    private readonly TextBlock _elapsedText;
    private readonly TextBlock _durationText;

    // 卡片内音频输出设备下拉菜单
    private readonly Border _deviceDropdown;
    private readonly StackPanel _deviceDropdownList;
    private bool _dropdownOpen;

    // 延迟 1000ms 显示的固定锚点悬停提示文本（离开按钮隐藏）
    private readonly Canvas _hintCanvas;
    private readonly Border _hintPill;
    private readonly TextBlock _hintText;
    private readonly DispatcherQueueTimer? _hintDelayTimer;
    private UIElement? _hintTarget;
    private Func<string>? _hintTextProvider;
    private Point _hintPendingPt;
    private bool _hintVisible;

    // 手势与进度条拖动状态
    private bool _isPointerDown;
    private Point _pointerDownPos;
    private bool _dragSwitched;
    private bool _isSeeking;
    private int _currentSelectedIndex = -1;

    // 缓存状态
    private byte[]? _lastCoverBytes;
    private byte[]? _lastCompactCoverBytes;
    private byte[]? _lastAppIconBytes;
    private int _coverLoadVer;
    private int _compactCoverLoadVer;
    private int _appIconLoadVer;
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
    public event Action<int>? MasterVolumeChangedRequested;
    public event Action<int>? MasterVolumeStepRequested;
    public event Action? MasterMuteToggleRequested;
    public event Action<string, int>? SetSleepTimerRequested;

    public MediaDeckView(
        IIslandTheme theme,
        Func<(float Master, float Left, float Right)> audioPeakProvider,
        Func<(int Volume, bool Muted)> masterVolumeProvider)
    {
        _theme = theme;
        _audioPeakProvider = audioPeakProvider;
        _masterVolumeProvider = masterVolumeProvider;

        // ==================== 0A. 极光流光波浪层（三层正弦波浪带，无矩形裁切） ====================
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

        InitWavePolygonPoints(_waveRibbon1);
        InitWavePolygonPoints(_waveRibbon2);
        InitWavePolygonPoints(_waveRibbon3);

        _auroraWaveLayer = new Grid
        {
            IsHitTestVisible = false,
            Children = { _waveRibbon1, _waveRibbon2, _waveRibbon3 },
        };

        // ==================== 0B. 图3同款：底部向容器中心打入的柔和渐变光线（支持切歌重新染色动画） ====================
        _inwardCenterBeamBrush = new RadialGradientBrush();
        _inwardAccentBeamBrush = new RadialGradientBrush();

        _inwardCenterBeamHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0.85,
        };
        _inwardAccentBeamHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0.72,
        };
        _dyeOverlayHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0,
            IsHitTestVisible = false,
        };

        _contourGlowLayer = new Grid
        {
            IsHitTestVisible = false,
            Opacity = 0,
            Children =
            {
                _inwardCenterBeamHost,
                _inwardAccentBeamHost,
                _dyeOverlayHost,
            },
        };

        // ==================== 0C. 鼠标中心小半径圆形光效层（中心强、向外弱） ====================
        _cursorStop0 = new GradientStop { Offset = 0.0 };
        _cursorStop1 = new GradientStop { Offset = 0.45 };
        var cursorGrad = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        cursorGrad.GradientStops.Add(_cursorStop0);
        cursorGrad.GradientStops.Add(_cursorStop1);
        cursorGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 125, 211, 252), Offset = 1.0 });

        _cursorGlowEllipse = new Ellipse
        {
            Width = 128,
            Height = 128,
            Fill = cursorGrad,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _cursorGlowCanvas = new Canvas
        {
            IsHitTestVisible = false,
            Children = { _cursorGlowEllipse },
        };

        ApplyThemeColors();
        UpdateContourGradientColors();
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
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _compactMarqueeTx,
        };

        // 使用横向 StackPanel 承载文本，避免 Grid 将 TextBlock 布局槽硬裁切至视口宽度导致滚动时后半段歌词缺失或字被截半
        var compactMarqueeHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _compactTitle },
        };

        _compactTextViewport = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Clip = _compactClipGeo,
            Children = { compactMarqueeHost },
        };
        _compactTextViewport.SizeChanged += (_, e) =>
        {
            _compactClipGeo.Rect = new Rect(0, -4, Math.Max(0, e.NewSize.Width), CompactHeight + 8);
            RecalculateCompactMarquee(resetOffset: false);
        };

        // 胶囊右侧：Quietify 5柱动态波形（播放）/ 5点（暂停）SVG 图标
        var compactQuietifyCanvas = new Canvas { Width = 135, Height = 140 };
        double[] barXs = [0, 30, 60, 90, 120];
        for (int i = 0; i < 5; i++)
        {
            var r = new Rectangle
            {
                Width = 15,
                Height = 15,
                RadiusX = 6,
                RadiusY = 6,
                Fill = _mutedBrush,
            };
            Canvas.SetLeft(r, barXs[i]);
            Canvas.SetTop(r, 70);
            compactQuietifyCanvas.Children.Add(r);
            _compactQuietifyBars[i] = r;
        }
        var compactQuietifyViewbox = new Viewbox
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Child = compactQuietifyCanvas,
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
        Grid.SetColumn(_compactTextViewport, 1);
        Grid.SetColumn(compactQuietifyViewbox, 2);
        _compactLayer.Children.Add(compactCoverBox);
        _compactLayer.Children.Add(_compactTextViewport);
        _compactLayer.Children.Add(compactQuietifyViewbox);

        // ==================== 2. 展开大岛层 ====================
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
        _sourceBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _sourceBtn.Click += (_, _) => ActivateSourceRequested?.Invoke();
        AttachCursorHint(_sourceBtn, () => $"切换至 {_currentSourceName}");

        // 2.2 顶部绝对居中：多实例指示点
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

        // 2.3 右上角：音频输出类型图标 + 蓝牙角标
        _outputGlyph = new FontIcon
        {
            Glyph = "\uE7F5",
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

        var topBar = new Grid
        {
            Height = 28,
            Children = { _sourceBtn, dotsHoverHost, _outputBtn },
        };

        // 2.4 中部左侧：专辑封面 + 歌名/歌手/来源名
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

        // 2.6 播放控制行：居中播放控制 + 最右侧对齐喇叭图标（与右上角图标同宽同右边距）
        _prevBtn = CreateInteractiveIconButton(
            new FontIcon { Glyph = "\uE892", FontSize = 15, Foreground = _textBrush },
            36, 36, cornerRadius: 18, isPrimaryPill: false);
        _prevBtn.Click += (_, _) =>
        {
            _pendingDyeOrigin = ContourDyeOrigin.FromPrevButton;
            _pendingDyeSetAt = DateTimeOffset.UtcNow;
            PreviousRequested?.Invoke();
        };
        AttachCursorHint(_prevBtn, () => "上一曲");

        _playPauseGlyph = new FontIcon { Glyph = "\uE768", FontSize = 17, Foreground = _textBrush };
        var playBtn = CreateInteractiveIconButton(
            _playPauseGlyph,
            40, 40, cornerRadius: 20, isPrimaryPill: true);
        playBtn.Click += (_, _) => PlayPauseRequested?.Invoke();
        AttachCursorHint(playBtn, () => _isPlaying ? "暂停" : "播放");

        _nextBtn = CreateInteractiveIconButton(
            new FontIcon { Glyph = "\uE893", FontSize = 15, Foreground = _textBrush },
            36, 36, cornerRadius: 18, isPrimaryPill: false);
        _nextBtn.Click += (_, _) =>
        {
            _pendingDyeOrigin = ContourDyeOrigin.FromNextButton;
            _pendingDyeSetAt = DateTimeOffset.UtcNow;
            NextRequested?.Invoke();
        };
        AttachCursorHint(_nextBtn, () => "下一曲");

        var transportCenterStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _prevBtn, playBtn, _nextBtn },
        };

        // 左对齐闹钟定时暂停按钮（与左上角来源图标同左边距、与右侧喇叭图标对称）
        _alarmBtnGlyph = new FontIcon
        {
            Glyph = "\uE916",
            FontSize = 14.5,
            Foreground = _textBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _alarmBtn = CreateInteractiveIconButton(_alarmBtnGlyph, 32, 28, cornerRadius: 8, isPrimaryPill: false);
        _alarmBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _alarmBtn.VerticalAlignment = VerticalAlignment.Center;
        _alarmBtn.Click += (_, _) => ToggleAlarmPopup();
        AttachCursorHint(_alarmBtn, () => _currentHasTimer
            ? $"定时暂停：剩余 {FormatTime(_currentTimerRemaining)}（点击修改/取消）"
            : "为当前实例设置定时暂停");

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
            Margin = new Thickness(36, 0, 0, 0),
            IsHitTestVisible = false,
            Child = _alarmInlineText,
        };

        // 右对齐喇叭按钮（Width = 32, HorizontalAlignment = Right，与右上角 _outputBtn 严格同右边距）
        _volumeBtnGlyph = new FontIcon
        {
            Glyph = "\uE767",
            FontSize = 15,
            Foreground = _textBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _volumeBtn = CreateInteractiveIconButton(_volumeBtnGlyph, 32, 28, cornerRadius: 8, isPrimaryPill: false);
        _volumeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        _volumeBtn.VerticalAlignment = VerticalAlignment.Center;
        _volumeBtn.PointerEntered += (_, _) =>
        {
            _isHoveringVolBtn = true;
            SyncMasterVolumeFromProvider();
        };
        _volumeBtn.PointerExited += (_, _) =>
        {
            _isHoveringVolBtn = false;
            _volHoverLeaveTime = DateTimeOffset.UtcNow;
        };
        _volumeBtn.PointerWheelChanged += OnVolumePointerWheelChanged;
        _volumeBtn.Click += (_, _) =>
        {
            _currentMasterMuted = !_currentMasterMuted;
            UpdateVolumeBarVisual(_currentMasterMuted ? 0 : Math.Max(1, _currentVolumePct), _currentMasterMuted);
            MasterMuteToggleRequested?.Invoke();
        };
        AttachCursorHint(_volumeBtn, () => _currentMasterMuted ? "取消静音" : "静音音量");

        var transportBarGrid = new Grid
        {
            Margin = new Thickness(0, 2, 0, 0),
            Children = { _alarmBtn, _alarmInlineBadge, transportCenterStack, _volumeBtn },
        };

        // 2.7 最底行：全宽进度条 + 左右时间戳
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

        // 2.8 自下而上弹出的垂直音量条（对齐右下角喇叭图标正上方）
        _volumePctText = new TextBlock
        {
            Text = "50",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var volTrackBg = new Border
        {
            Width = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = _trackBgBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _volumeFillBar = new Border
        {
            Width = 5,
            Height = 0,
            CornerRadius = new CornerRadius(2.5),
            Background = _accentCyanBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _volumeThumb = new Border
        {
            Width = 11,
            Height = 11,
            CornerRadius = new CornerRadius(5.5),
            Background = _thumbBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _volumeTrackHitArea = new Grid
        {
            Width = 26,
            Height = 76,
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { volTrackBg, _volumeFillBar, _volumeThumb },
        };
        _volumeTrackHitArea.PointerPressed += OnVolumeTrackPointerPressed;
        _volumeTrackHitArea.PointerMoved += OnVolumeTrackPointerMoved;
        _volumeTrackHitArea.PointerReleased += OnVolumeTrackPointerReleased;
        _volumeTrackHitArea.PointerCaptureLost += OnVolumeTrackPointerCaptureLost;
        _volumeTrackHitArea.Tapped += (_, e) => e.Handled = true;

        var volPopupInner = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { _volumePctText, _volumeTrackHitArea },
        };

        _volumePopupHost = new Border
        {
            Width = 34,
            Height = 0,
            Opacity = 0,
            IsHitTestVisible = false,
            CornerRadius = new CornerRadius(17),
            Background = _dropdownBgBrush,
            BorderBrush = _subtleBorderBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -1, 56),
            Child = volPopupInner,
        };
        _volumePopupHost.PointerEntered += (_, _) => _isHoveringVolPopup = true;
        _volumePopupHost.PointerExited += (_, _) =>
        {
            _isHoveringVolPopup = false;
            _volHoverLeaveTime = DateTimeOffset.UtcNow;
        };
        _volumePopupHost.PointerWheelChanged += OnVolumePointerWheelChanged;
        _volumePopupHost.Tapped += (_, e) => e.Handled = true;

        // 2.9 卡片内音频输出设备下拉菜单
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
            Width = 264,
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

        // 2.95 卡片内分实例定时暂停弹出菜单（带内部滚动适配，对齐左下角闹钟图标正上方）
        _alarmPopupList = new StackPanel { Spacing = 2, Padding = new Thickness(5) };
        var alarmScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 132,
            Content = _alarmPopupList,
        };
        _alarmPopup = new Border
        {
            Width = 156,
            MaxHeight = 136,
            Height = 0,
            Opacity = 0,
            IsHitTestVisible = false,
            CornerRadius = new CornerRadius(10),
            Background = _dropdownBgBrush,
            BorderBrush = _subtleBorderBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 52),
            Child = alarmScroll,
        };
        _alarmPopup.Tapped += (_, e) => e.Handled = true;

        // 2.10 固定锚点式悬停提示文本层
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
        Grid.SetRow(transportBarGrid, 2);
        Grid.SetRow(bottomProgressRow, 3);
        expandedContent.Children.Add(topBar);
        expandedContent.Children.Add(_middleGrid);
        expandedContent.Children.Add(transportBarGrid);
        expandedContent.Children.Add(bottomProgressRow);

        _expandedLayer = new Grid
        {
            Height = 0,
            Opacity = 0,
            IsHitTestVisible = false,
            Padding = new Thickness(16, 10, 16, 10),
            Children = { expandedContent, _volumePopupHost, _alarmPopup, _deviceDropdown, _hintCanvas },
        };

        // 根容器
        var root = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Children = { _auroraWaveLayer, _contourGlowLayer, _cursorGlowCanvas, _compactLayer, _expandedLayer },
        };

        root.PointerEntered += OnRootPointerEntered;
        root.PointerExited += OnRootPointerExited;
        root.PointerWheelChanged += OnCardPointerWheelChanged;
        root.PointerPressed += OnCardPointerPressed;
        root.PointerMoved += OnCardPointerMoved;
        root.PointerReleased += OnCardPointerReleased;
        root.Tapped += OnCardTapped;

        Content = root;

        // 逐帧变形定时器
        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        // 翻页切换动画定时器
        _pageAnimTimer = DispatcherQueue?.CreateTimer();
        if (_pageAnimTimer is not null)
        {
            _pageAnimTimer.Interval = TimeSpan.FromMilliseconds(16);
            _pageAnimTimer.IsRepeating = true;
            _pageAnimTimer.Tick += (_, _) => OnPageAnimTick();
        }

        // 悬停提示 1000ms 延迟触发定时器
        _hintDelayTimer = DispatcherQueue?.CreateTimer();
        if (_hintDelayTimer is not null)
        {
            _hintDelayTimer.Interval = TimeSpan.FromMilliseconds(1000);
            _hintDelayTimer.IsRepeating = false;
            _hintDelayTimer.Tick += (_, _) => OnHintDelayElapsed();
        }

        // 30fps 主视觉定时器（驱动极光波浪 / 封面轮廓光 / 胶囊歌词滚动 / 胶囊 Quietify 波形 / 音量条弹出收回动画）
        _auroraTimer = DispatcherQueue?.CreateTimer();
        if (_auroraTimer is not null)
        {
            _auroraTimer.Interval = TimeSpan.FromMilliseconds(33);
            _auroraTimer.IsRepeating = true;
            _auroraTimer.Tick += (_, _) => OnVisualTick();
            _auroraTimer.Start();
        }

        Loaded += (_, _) => _auroraTimer?.Start();
        Unloaded += (_, _) =>
        {
            _hintDelayTimer?.Stop();
            HideHint();
            // 仅在真正离开视觉树时停止动画，避免宿主短暂重挂视图导致主视觉循环永久停摆
            if (!IsLoaded)
            {
                _morphTimer?.Stop();
                _pageAnimTimer?.Stop();
            }
        };
    }

    public void StopAllTimers()
    {
        _morphTimer?.Stop();
        _pageAnimTimer?.Stop();
        _auroraTimer?.Stop();
        _hintDelayTimer?.Stop();
    }

    public UIElement View => this;

    public void SetVisualEffectMode(VisualEffectMode mode)
    {
        _visualMode = mode;
        _auroraWaveLayer.Opacity = mode == VisualEffectMode.AuroraWave ? 1 : 0;
        _contourGlowLayer.Opacity = mode == VisualEffectMode.AlbumContour ? 1 : 0;
        if (mode != VisualEffectMode.Off)
        {
            EnsureAuroraTimerRunning();
        }
    }

    public void SetContourReactivity(double reactivityRatio)
    {
        _contourReactivity = Math.Clamp(reactivityRatio, 0.0, 5.0);
    }

    private void EnsureAuroraTimerRunning()
    {
        if (_auroraTimer is not null && !_auroraTimer.IsRunning)
        {
            _auroraTimer.Start();
        }
    }

    public void SetCursorGlowSettings(bool enabled, double size, double opacity)
    {
        _cursorGlowEnabled = enabled;
        _cursorGlowSize = Math.Clamp(size, 40.0, 320.0);
        _cursorGlowOpacity = Math.Clamp(opacity, 0.0, 1.0);
        _cursorGlowEllipse.Width = _cursorGlowSize;
        _cursorGlowEllipse.Height = _cursorGlowSize;
        if (!_cursorGlowEnabled || _cursorGlowOpacity <= 0.005)
        {
            _cursorGlowEllipse.Opacity = 0;
        }
    }

    public void AnimateToExpanded(TimeSpan duration)
    {
        EnsureAuroraTimerRunning();
        StartMorph(expanded: true, duration);
    }

    public void AnimateToCompact(TimeSpan duration)
    {
        CloseDeviceDropdown();
        CloseAlarmPopup();
        _isHoveringVolBtn = false;
        _isHoveringVolPopup = false;
        _isDraggingVolBar = false;
        HideHint();
        StartMorph(expanded: false, duration);
    }

    public void UpdateMedia(
        MediaInstanceInfo? current,
        int totalCount,
        int selectedIndex,
        LyricTriplet lyrics,
        MediaInstanceInfo? compact,
        LyricTriplet compactLyrics)
    {
        // 1) 胶囊形态独立刷新：遵循「新旧优先 + 优先展示正在播放的实例」规则
        if (compact is null)
        {
            _compactIsPlaying = false;
            _lastCompactTrackKey = "";
            SetCompactText("声枢 · 暂无媒体", isLyric: false);
            UpdateCompactQuietifyStatic();
            UpdateCompactCoverImage(null);
        }
        else
        {
            _compactIsPlaying = compact.IsPlaying;
            bool compactHasLyric = compactLyrics.HasSyncedLyrics && !string.IsNullOrWhiteSpace(compactLyrics.Current);
            SetCompactText(compactHasLyric ? compactLyrics.Current : compact.Title, isLyric: compactHasLyric);
            UpdateCompactQuietifyStatic();
            string compactTrackKey = $"{compact.Id}||{compact.Title}||{compact.Artist}";
            bool compactTrackChanged = !string.Equals(_lastCompactTrackKey, compactTrackKey, StringComparison.OrdinalIgnoreCase);
            _lastCompactTrackKey = compactTrackKey;
            UpdateCompactCoverImage(compact.ThumbnailBytes, triggerPaletteIfCompact: compactTrackChanged && _progress < 0.5);
        }

        // 2) 展开卡片形态刷新：遵循卡片当前选中的实例
        if (current is null)
        {
            _currentInstanceId = null;
            _isPlaying = false;
            _currentSourceName = "媒体播放器";
            _currentDuration = TimeSpan.Zero;
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
            CloseAlarmPopup();
            return;
        }

        EnsureAuroraTimerRunning();
        bool instanceChanged = !string.Equals(_currentInstanceId, current.Id, StringComparison.OrdinalIgnoreCase);
        string trackKey = $"{current.Id}||{current.Title}||{current.Artist}";
        bool trackChanged = !string.Equals(_lastTrackKey, trackKey, StringComparison.OrdinalIgnoreCase);
        _lastTrackKey = trackKey;
        if (instanceChanged)
        {
            _currentInstanceId = current.Id;
            CloseAlarmPopup();
        }
        _isPlaying = current.IsPlaying;
        _currentSourceName = current.SourceDisplayName;
        _currentDuration = current.Duration;

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

        UpdateCoverImage(current.ThumbnailBytes, force: instanceChanged || trackChanged);
        UpdateAppIconImage(current.AppIconBytes, force: instanceChanged);
        RebuildInstanceDots(totalCount, selectedIndex);
    }

    public void UpdateOutputDevices(IReadOnlyList<AudioDeviceInfo> devices)
    {
        _cachedDevices = devices.ToList();
        var def = devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();

        if (def is not null)
        {
            _outputGlyph.Glyph = def.Glyph;
            _btBadge.Opacity = def.IsBluetooth ? 1 : 0;
        }
        else
        {
            _outputGlyph.Glyph = "\uE7F5";
            _btBadge.Opacity = 0;
        }

        if (_dropdownOpen)
        {
            RebuildDeviceDropdownItems();
        }
    }

    public void UpdateSleepTimer(bool isActive, TimeSpan remaining)
    {
        _currentHasTimer = isActive && remaining > TimeSpan.Zero;
        _currentTimerRemaining = _currentHasTimer ? remaining : TimeSpan.Zero;

        if (_currentHasTimer)
        {
            _alarmBtnGlyph.Foreground = _accentCyanBrush;
            _alarmInlineText.Text = FormatTime(remaining);
            _alarmInlineBadge.Opacity = 1;
            _alarmInlineBadge.Height = 18;
        }
        else
        {
            _alarmBtnGlyph.Foreground = _textBrush;
            _alarmInlineBadge.Opacity = 0;
            _alarmInlineBadge.Height = 0;
        }
    }

    // ==================== 胶囊歌词滚动 & Quietify 5柱/5点图标 ====================

    private void SetCompactText(string text, bool isLyric)
    {
        bool changed = !string.Equals(_lastCompactText, text, StringComparison.Ordinal) || (_compactIsLyricMode != isLyric);
        _lastCompactText = text;
        _compactIsLyricMode = isLyric;
        _compactTitle.Text = text;

        if (changed)
        {
            RecalculateCompactMarquee(resetOffset: true);
        }
    }

    private void RecalculateCompactMarquee(bool resetOffset)
    {
        double vw = _compactTextViewport.ActualWidth;
        if (resetOffset)
        {
            _compactMarqueeOffset = 0;
            _compactMarqueePauseSec = 0.85;
            _compactMarqueeResetting = false;
            _compactMarqueeTx.X = 0;
        }

        if (!_compactIsLyricMode)
        {
            // 无歌词显示歌曲名称时：保持旧行为（不滚动，超出显示“……”）
            _compactTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            _compactTitle.Width = vw > 0 ? vw : double.NaN;
            _compactTitle.MaxWidth = vw > 0 ? vw : double.PositiveInfinity;
            _compactTextFullWidth = 0;
            _compactMarqueeTx.X = 0;
            return;
        }

        // 有同步歌词时：解除宽度限制，让横向 StackPanel 按完整文本宽度排版并平滑滚动
        _compactTitle.TextTrimming = TextTrimming.None;
        _compactTitle.Width = double.NaN;
        _compactTitle.MaxWidth = double.PositiveInfinity;
        _compactTitle.Measure(new Size(double.PositiveInfinity, CompactHeight));
        _compactTextFullWidth = Math.Ceiling(_compactTitle.DesiredSize.Width);

        if (vw > 0 && _compactTextFullWidth <= vw + 2)
        {
            _compactMarqueeTx.X = 0;
        }
    }

    private void UpdateCompactQuietifyStatic()
    {
        if (!_compactIsPlaying)
        {
            // 暂停状态：显示第 2 个图标（5 个水平圆点 y=70, height=15）
            for (int i = 0; i < 5; i++)
            {
                _compactQuietifyBars[i].Fill = _mutedBrush;
                _compactQuietifyBars[i].Height = 15;
                Canvas.SetTop(_compactQuietifyBars[i], 70);
            }
        }
        else
        {
            for (int i = 0; i < 5; i++)
            {
                _compactQuietifyBars[i].Fill = _accentCyanBrush;
            }
        }
    }

    // ==================== 30fps 主视觉循环（极光波浪 / 封面物理轮廓光 / 胶囊滚动 / 音量条动画） ====================

    private static void InitWavePolygonPoints(Polygon poly)
    {
        poly.Points.Clear();
        for (int i = 0; i < WavePointsCount; i++)
        {
            poly.Points.Add(new Point(i * (560.0 / (WavePointsCount - 1)), 100));
        }
        poly.Points.Add(new Point(560, 220));
        poly.Points.Add(new Point(0, 220));
    }

    private void OnVisualTick()
    {
        const double dt = 0.033;

        // 1) 胶囊形态：超长歌词平滑自动滚动 & 右侧 Quietify 5柱播放动画
        if (_progress < 0.45)
        {
            if (_compactIsLyricMode)
            {
                double vw = _compactTextViewport.ActualWidth;
                double overflow = _compactTextFullWidth - vw;
                if (vw > 10 && overflow > 3)
                {
                    if (_compactMarqueePauseSec > 0)
                    {
                        _compactMarqueePauseSec -= dt;
                    }
                    else if (!_compactMarqueeResetting)
                    {
                        _compactMarqueeOffset += 30.0 * dt;
                        if (_compactMarqueeOffset >= overflow + 10)
                        {
                            _compactMarqueeOffset = overflow + 10;
                            _compactMarqueeResetting = true;
                            _compactMarqueePauseSec = 0.75;
                        }
                        _compactMarqueeTx.X = -_compactMarqueeOffset;
                    }
                    else
                    {
                        _compactMarqueeOffset = 0;
                        _compactMarqueeResetting = false;
                        _compactMarqueePauseSec = 0.85;
                        _compactMarqueeTx.X = 0;
                    }
                }
            }

            if (_compactIsPlaying)
            {
                double sec = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                for (int i = 0; i < 5; i++)
                {
                    double phase = ((sec - QuietifyPhases[i]) % 1.0 + 1.0) % 1.0;
                    double pos = phase * (QuietifyHeights.Length - 1);
                    int idx = Math.Clamp((int)pos, 0, QuietifyHeights.Length - 2);
                    double frac = pos - idx;
                    double h = QuietifyHeights[idx] + (QuietifyHeights[idx + 1] - QuietifyHeights[idx]) * frac;
                    double y = QuietifyYs[idx] + (QuietifyYs[idx + 1] - QuietifyYs[idx]) * frac;
                    _compactQuietifyBars[i].Height = h;
                    Canvas.SetTop(_compactQuietifyBars[i], y);
                }
            }
        }

        // 2) 展开卡片：喇叭音量条自下而上弹出 / 收回动画
        bool wantVolOpen = _progress > 0.7 &&
            (_isHoveringVolBtn || _isHoveringVolPopup || _isDraggingVolBar ||
             (DateTimeOffset.UtcNow - _volHoverLeaveTime).TotalMilliseconds < 110);

        double volTarget = wantVolOpen ? 1.0 : 0.0;
        if (Math.Abs(_volPopupProgress - volTarget) > 0.005)
        {
            double step = dt / 0.16; // 约 160ms 完成弹出/收回
            _volPopupProgress = _volPopupProgress < volTarget
                ? Math.Min(volTarget, _volPopupProgress + step)
                : Math.Max(volTarget, _volPopupProgress - step);

            double ease = 1 - Math.Pow(1 - _volPopupProgress, 3);
            _volumePopupHost.Height = 114 * ease;
            _volumePopupHost.Opacity = Math.Clamp(ease * 1.15, 0, 1);
            _volumePopupHost.IsHitTestVisible = _volPopupProgress > 0.15;
        }
        else if (!_isDraggingVolBar && _progress > 0.7)
        {
            SyncMasterVolumeFromProvider();
        }

        // 3) 鼠标坐标平滑插值（同时驱动波浪/轮廓光物理避让场与可选圆形光效）
        double targetCursorOp = _isPointerInRoot ? 1.0 : 0.0;
        _cursorSmoothOpacity += (targetCursorOp - _cursorSmoothOpacity) * 0.22;
        if (_cursorSmoothOpacity > 0.01)
        {
            _cursorSmoothX += (_cursorRawX - _cursorSmoothX) * 0.32;
            _cursorSmoothY += (_cursorRawY - _cursorSmoothY) * 0.32;
            if (_cursorGlowEnabled && _cursorGlowOpacity > 0.005)
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
        }
        else
        {
            _cursorGlowEllipse.Opacity = 0;
        }

        // 4) 音频频谱驱动视觉特效（含对鼠标移动的避让反馈）
        if (_visualMode == VisualEffectMode.Off) return;

        try
        {
            var (master, left, right) = _audioPeakProvider();
            double mCurve = Math.Pow(Math.Clamp(master, 0, 1), 0.65);
            double lCurve = Math.Pow(Math.Clamp(left, 0, 1), 0.65);
            double rCurve = Math.Pow(Math.Clamp(right, 0, 1), 0.65);

            bool anyPlaying = _isPlaying || _compactIsPlaying;
            double targetM = anyPlaying ? Math.Max(mCurve, 0.12) : mCurve * 0.4;
            double targetL = anyPlaying ? Math.Max(lCurve, 0.10) : lCurve * 0.4;
            double targetR = anyPlaying ? Math.Max(rCurve, 0.10) : rCurve * 0.4;

            _smoothMaster += (targetM - _smoothMaster) * (targetM > _smoothMaster ? 0.35 : 0.12);
            _slowMaster += (_smoothMaster - _slowMaster) * 0.05;
            _smoothLeft += (targetL - _smoothLeft) * (targetL > _smoothLeft ? 0.35 : 0.12);
            _smoothRight += (targetR - _smoothRight) * (targetR > _smoothRight ? 0.35 : 0.12);

            if (_visualMode == VisualEffectMode.AuroraWave)
            {
                UpdateAuroraWaves();
            }
            else if (_visualMode == VisualEffectMode.AlbumContour)
            {
                UpdateAlbumContourGlow();
            }
        }
        catch { }
    }

    private void UpdateAuroraWaves()
    {
        // 随音频频率加速流动与波峰起伏
        _auroraPhase1 += 0.045 + _smoothLeft * 0.11;
        _auroraPhase2 += 0.036 + _smoothRight * 0.10;
        _auroraPhase3 += 0.052 + _smoothMaster * 0.12;

        double baseOp = _theme.IsLight ? 0.48 : 0.42;
        double boost = _theme.IsLight ? 0.48 : 0.52;

        _waveRibbon1.Opacity = Math.Clamp(baseOp + _smoothLeft * boost, 0.30, 0.96);
        _waveRibbon2.Opacity = Math.Clamp(baseOp + _smoothRight * boost, 0.30, 0.96);
        _waveRibbon3.Opacity = Math.Clamp(baseOp * 0.85 + _smoothMaster * boost, 0.25, 0.92);

        UpdateSingleWavePolygon(_waveRibbon1, _auroraPhase1, baseLine: 72 - _smoothLeft * 36, amp1: 14 + _smoothLeft * 32, amp2: 9 + _smoothMaster * 18, freq1: 1.9, freq2: 3.4, maxPush: 24.0);
        UpdateSingleWavePolygon(_waveRibbon2, -_auroraPhase2, baseLine: 86 - _smoothRight * 38, amp1: 16 + _smoothRight * 34, amp2: 10 + _smoothLeft * 16, freq1: 2.2, freq2: 3.1, maxPush: 30.0);
        UpdateSingleWavePolygon(_waveRibbon3, _auroraPhase3 * 0.85, baseLine: 102 - _smoothMaster * 42, amp1: 12 + _smoothMaster * 36, amp2: 11 + _smoothRight * 20, freq1: 2.6, freq2: 4.0, maxPush: 36.0);
    }

    private void UpdateSingleWavePolygon(Polygon poly, double phase, double baseLine, double amp1, double amp2, double freq1, double freq2, double maxPush)
    {
        var pts = poly.Points;
        if (pts.Count < WavePointsCount + 2) return;

        double viewW = ActualWidth > 10 ? ActualWidth : 560.0;
        double viewH = ActualHeight > 10 ? ActualHeight : (_progress > 0.5 ? ExpandedHeight : CompactHeight);
        // 将鼠标实际坐标映射到 560x208 的波浪坐标系中
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

            // 平滑流体物理避让场（C∞ 连续高斯衰减 + 双曲正切方向过渡，消除生硬折线与符号跳变）
            if (evadeStrength > 0.01)
            {
                double dx = x - mx;
                double dy = y - my;
                const double sigmaX = 92.0;
                const double sigmaY = 84.0;
                double gaussX = Math.Exp(-(dx * dx) / (2.0 * sigmaX * sigmaX));
                double gaussY = Math.Exp(-(dy * dy) / (2.0 * sigmaY * sigmaY));
                double smoothDirY = Math.Tanh((dy + 18.0) / 44.0);
                y += smoothDirY * gaussX * gaussY * maxPush * evadeStrength;
            }

            // 软边界渐近压缩，避免波峰贴近顶底边界时被硬截断成平直线
            if (y < 14.0)
            {
                y = 14.0 - 10.0 * Math.Tanh((14.0 - y) / 18.0);
            }
            else if (y > 202.0)
            {
                y = 202.0 + 8.0 * Math.Tanh((y - 202.0) / 18.0);
            }

            pts[i] = new Point(x, y);
        }
    }

    private void BeginContourPaletteTransition(Color left, Color center, Color right, bool allowButtonOrigin)
    {
        bool colorActuallyChanged = left != _targetCoverLeft || center != _targetCoverCenter || right != _targetCoverRight;
        _targetCoverLeft = left;
        _targetCoverCenter = center;
        _targetCoverRight = right;

        // 决定重新染色扩散起点：用户点击上/下一曲 -> 以对应按钮为中心扩散；否则 -> 自下而上柔和染色
        var origin = ContourDyeOrigin.BottomUp;
        if (allowButtonOrigin && _pendingDyeOrigin.HasValue && (DateTimeOffset.UtcNow - _pendingDyeSetAt).TotalSeconds < 4.5)
        {
            origin = _pendingDyeOrigin.Value;
            _pendingDyeOrigin = null;
        }
        else if (!colorActuallyChanged && !_dyeActive)
        {
            return;
        }

        _dyeFromLeft = _curCoverLeft;
        _dyeFromCenter = _curCoverCenter;
        _dyeFromRight = _curCoverRight;
        _dyeOrigin = origin;
        _dyeOriginNorm = ResolveDyeOriginNormalized(origin);
        _dyeStart = DateTimeOffset.UtcNow;
        _dyeActive = true;
    }

    private Point ResolveDyeOriginNormalized(ContourDyeOrigin origin)
    {
        if (origin == ContourDyeOrigin.BottomUp || _progress < 0.5)
        {
            return new Point(0.50, 1.06);
        }

        var btn = origin == ContourDyeOrigin.FromPrevButton ? _prevBtn : _nextBtn;
        try
        {
            double w = _contourGlowLayer.ActualWidth > 10 ? _contourGlowLayer.ActualWidth : 560.0;
            double h = _contourGlowLayer.ActualHeight > 10 ? _contourGlowLayer.ActualHeight : ExpandedHeight;
            var pt = btn.TransformToVisual(_contourGlowLayer).TransformPoint(new Point(btn.ActualWidth * 0.5, btn.ActualHeight * 0.5));
            return new Point(Math.Clamp(pt.X / w, 0.10, 0.90), Math.Clamp(pt.Y / h, 0.15, 0.95));
        }
        catch
        {
            return origin == ContourDyeOrigin.FromPrevButton ? new Point(0.39, 0.72) : new Point(0.61, 0.72);
        }
    }

    private void UpdateAlbumContourGlow()
    {
        // 1) 切歌重新染色动画（780ms，< 1s，丝滑三次缓出）：
        //    - 胶囊 / 自动切歌：自下而上波浪染色
        //    - 卡片点击上/下一曲：以上/下一曲按钮为中心向外扩散染色
        if (_dyeActive)
        {
            const double dyeDurationMs = 780.0;
            double t = Math.Clamp((DateTimeOffset.UtcNow - _dyeStart).TotalMilliseconds / dyeDurationMs, 0, 1);
            double ease = 1.0 - Math.Pow(1.0 - t, 3);

            _curCoverLeft = LerpColor(_dyeFromLeft, _targetCoverLeft, ease);
            _curCoverCenter = LerpColor(_dyeFromCenter, _targetCoverCenter, ease);
            _curCoverRight = LerpColor(_dyeFromRight, _targetCoverRight, ease);

            // 染色波前覆盖层：新建 RadialGradientBrush 绕过 WinUI 3 GradientStop 不刷新 Bug
            bool light = _theme.IsLight;
            double waveAlpha = Math.Sin(t * Math.PI); // 0 -> 1 -> 0 柔和淡入再融入基底光
            double rx, ry;
            if (_dyeOrigin == ContourDyeOrigin.BottomUp)
            {
                rx = 0.42 + ease * 0.62;
                ry = 0.06 + ease * 1.10;
            }
            else
            {
                rx = 0.04 + ease * 1.12;
                ry = 0.04 + ease * 1.32;
            }

            var dyeBrush = new RadialGradientBrush
            {
                Center = _dyeOriginNorm,
                GradientOrigin = _dyeOriginNorm,
                RadiusX = rx,
                RadiusY = ry,
            };
            byte peakA = (byte)Math.Clamp((light ? 175 : 165) * waveAlpha, 0, 220);
            byte midA = (byte)Math.Clamp((light ? 95 : 88) * waveAlpha, 0, 140);
            dyeBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_targetCoverCenter, peakA), Offset = 0.0 });
            dyeBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_targetCoverLeft, midA), Offset = 0.52 });
            dyeBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_targetCoverRight, 0), Offset = 1.0 });

            _dyeOverlayHost.Background = dyeBrush;
            _dyeOverlayHost.Opacity = Math.Clamp(waveAlpha * 0.92, 0, 0.95);

            if (t >= 1.0)
            {
                _dyeActive = false;
                _curCoverLeft = _targetCoverLeft;
                _curCoverCenter = _targetCoverCenter;
                _curCoverRight = _targetCoverRight;
                _dyeOverlayHost.Opacity = 0;
            }
        }
        else
        {
            _curCoverLeft = LerpColor(_curCoverLeft, _targetCoverLeft, 0.16);
            _curCoverCenter = LerpColor(_curCoverCenter, _targetCoverCenter, 0.16);
            _curCoverRight = LerpColor(_curCoverRight, _targetCoverRight, 0.16);
        }

        // 仅当颜色或主题变化时重建 RadialGradientBrush（彻底解决 WinUI 3 修改 GradientStop.Color 不生效导致一直停留在旧颜色的问题）
        if (_curCoverLeft != _lastRenderedLeft ||
            _curCoverCenter != _lastRenderedCenter ||
            _curCoverRight != _lastRenderedRight ||
            _theme.IsLight != _lastRenderedLight)
        {
            UpdateContourGradientColors();
        }

        // 2) 律动强度：0 => 完全静态光；1.0 (100%) => 清晰可见的呼吸与节拍律动；5.0 (500%) => 强烈动感冲击
        double react = _contourReactivity;
        double beat = Math.Max(0, _smoothMaster - _slowMaster * 0.78);
        double energy = _smoothMaster * 0.65 + beat * 1.65;

        if (react > 0.001)
        {
            _auroraPhase1 += 0.022 + energy * 0.065 * Math.Min(react, 3.5);
        }

        // 鼠标避让反馈：底部向内打入的光束遇到鼠标时向反方向轻柔偏转并收缩投射深度
        double viewW = ActualWidth > 10 ? ActualWidth : 560.0;
        double viewH = ActualHeight > 10 ? ActualHeight : (_progress > 0.5 ? ExpandedHeight : CompactHeight);
        double normMx = Math.Clamp(_cursorSmoothX / Math.Max(1, viewW), 0, 1);
        double normMy = Math.Clamp(_cursorSmoothY / Math.Max(1, viewH), 0, 1);

        double evadeX = (0.5 - normMx) * 0.24 * _cursorSmoothOpacity;
        double bottomProximity = Math.Max(0, (normMy - 0.35) / 0.65) * (1.0 - Math.Min(1.0, Math.Abs(normMx - 0.5) * 1.8));
        double evadeYCompress = bottomProximity * 0.16 * _cursorSmoothOpacity;

        double driftX = react > 0.001 ? Math.Sin(_auroraPhase1) * Math.Min(0.11, 0.035 * react) : 0.0;
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
        byte coreAlpha = light ? (byte)165 : (byte)155;
        byte midAlpha = light ? (byte)78 : (byte)72;

        // 构造全新 RadialGradientBrush 实例，强制 WinUI 3 Composition 立即应用最新颜色
        var centerBrush = new RadialGradientBrush
        {
            Center = _inwardCenterBeamBrush.Center,
            GradientOrigin = _inwardCenterBeamBrush.GradientOrigin,
            RadiusX = _inwardCenterBeamBrush.RadiusX > 0.05 ? _inwardCenterBeamBrush.RadiusX : 0.64,
            RadiusY = _inwardCenterBeamBrush.RadiusY > 0.05 ? _inwardCenterBeamBrush.RadiusY : 0.68,
        };
        centerBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_curCoverCenter, coreAlpha), Offset = 0.0 });
        centerBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_curCoverLeft, midAlpha), Offset = 0.48 });
        centerBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_curCoverLeft, 0), Offset = 1.0 });

        var accentBrush = new RadialGradientBrush
        {
            Center = _inwardAccentBeamBrush.Center,
            GradientOrigin = _inwardAccentBeamBrush.GradientOrigin,
            RadiusX = _inwardAccentBeamBrush.RadiusX > 0.05 ? _inwardAccentBeamBrush.RadiusX : 0.52,
            RadiusY = _inwardAccentBeamBrush.RadiusY > 0.05 ? _inwardAccentBeamBrush.RadiusY : 0.48,
        };
        accentBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_curCoverRight, (byte)(coreAlpha - 20)), Offset = 0.0 });
        accentBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_curCoverCenter, (byte)(midAlpha - 15)), Offset = 0.50 });
        accentBrush.GradientStops.Add(new GradientStop { Color = WithAlpha(_curCoverCenter, 0), Offset = 1.0 });

        _inwardCenterBeamBrush = centerBrush;
        _inwardAccentBeamBrush = accentBrush;
        _inwardCenterBeamHost.Background = centerBrush;
        _inwardAccentBeamHost.Background = accentBrush;

        _lastRenderedLeft = _curCoverLeft;
        _lastRenderedCenter = _curCoverCenter;
        _lastRenderedRight = _curCoverRight;
        _lastRenderedLight = light;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color LerpColor(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)Math.Clamp(a.R + (b.R - a.R) * t, 0, 255),
        (byte)Math.Clamp(a.G + (b.G - a.G) * t, 0, 255),
        (byte)Math.Clamp(a.B + (b.B - a.B) * t, 0, 255));

    // ==================== 喇叭弹出音量条交互（滚轮步进 2 + 拖拽调整） ====================

    private void SyncMasterVolumeFromProvider()
    {
        var (vol, muted) = _masterVolumeProvider();
        _currentMasterMuted = muted;
        int displayVol = muted ? 0 : Math.Clamp(vol, 0, 100);
        if (!muted) _currentVolumePct = displayVol;
        UpdateVolumeBarVisual(displayVol, muted);
    }

    private void UpdateVolumeBarVisual(int percent, bool muted)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        _volumePctText.Text = $"{clamped}";

        double trackH = 76.0;
        double fillH = trackH * (clamped / 100.0);
        _volumeFillBar.Height = fillH;
        double thumbBottom = Math.Clamp(fillH - 5.5, 0, trackH - 11.0);
        _volumeThumb.Margin = new Thickness(0, 0, 0, thumbBottom);

        if (muted || clamped == 0) _volumeBtnGlyph.Glyph = "\uE74F";
        else if (clamped < 34) _volumeBtnGlyph.Glyph = "\uE993";
        else if (clamped < 67) _volumeBtnGlyph.Glyph = "\uE994";
        else _volumeBtnGlyph.Glyph = "\uE767";
    }

    private int ComputeVolumeFromPointer(PointerRoutedEventArgs e)
    {
        double h = _volumeTrackHitArea.ActualHeight > 0 ? _volumeTrackHitArea.ActualHeight : 76.0;
        var pt = e.GetCurrentPoint(_volumeTrackHitArea).Position;
        double ratioFromBottom = 1.0 - Math.Clamp(pt.Y / h, 0, 1);
        return (int)Math.Round(ratioFromBottom * 100.0);
    }

    private void OnVolumeTrackPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_volumeTrackHitArea);
        if (!pt.Properties.IsLeftButtonPressed) return;

        e.Handled = true;
        _isPointerDown = false;
        _dragSwitched = false;
        _isDraggingVolBar = true;
        _volumeTrackHitArea.CapturePointer(e.Pointer);

        int pct = ComputeVolumeFromPointer(e);
        _currentVolumePct = pct;
        UpdateVolumeBarVisual(pct, muted: pct == 0);
        MasterVolumeChangedRequested?.Invoke(pct);
    }

    private void OnVolumeTrackPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingVolBar) return;
        e.Handled = true;
        int pct = ComputeVolumeFromPointer(e);
        if (pct != _currentVolumePct)
        {
            _currentVolumePct = pct;
            UpdateVolumeBarVisual(pct, muted: pct == 0);
            MasterVolumeChangedRequested?.Invoke(pct);
        }
    }

    private void OnVolumeTrackPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingVolBar) return;
        e.Handled = true;
        _isDraggingVolBar = false;
        _volumeTrackHitArea.ReleasePointerCapture(e.Pointer);
        _volHoverLeaveTime = DateTimeOffset.UtcNow;
    }

    private void OnVolumeTrackPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingVolBar = false;
        _volHoverLeaveTime = DateTimeOffset.UtcNow;
    }

    private void OnVolumePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;

        e.Handled = true;
        int step = delta > 0 ? 2 : -2;
        int next = Math.Clamp(_currentVolumePct + step, 0, 100);
        _currentVolumePct = next;
        UpdateVolumeBarVisual(next, muted: next == 0);
        MasterVolumeStepRequested?.Invoke(step);
    }

    // ==================== 卡片内音频输出下拉菜单 + 分实例定时暂停弹出菜单 ====================

    private void ToggleDeviceDropdown()
    {
        if (_dropdownOpen)
        {
            CloseDeviceDropdown();
            return;
        }

        CloseAlarmPopup();
        HideHint();
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

    private void ToggleAlarmPopup()
    {
        if (_alarmPopupOpen)
        {
            CloseAlarmPopup();
            return;
        }
        if (string.IsNullOrWhiteSpace(_currentInstanceId)) return;

        CloseDeviceDropdown();
        HideHint();
        RebuildAlarmPopupItems();
        _alarmPopupOpen = true;
        _alarmPopup.Height = double.NaN;
        _alarmPopup.Opacity = 1;
        _alarmPopup.IsHitTestVisible = true;
    }

    private void CloseAlarmPopup()
    {
        if (!_alarmPopupOpen) return;
        _alarmPopupOpen = false;
        _alarmPopup.Opacity = 0;
        _alarmPopup.Height = 0;
        _alarmPopup.IsHitTestVisible = false;
    }

    private void RebuildAlarmPopupItems()
    {
        _alarmPopupList.Children.Clear();
        _alarmPopupList.Children.Add(new TextBlock
        {
            Text = "定时暂停当前实例",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _mutedBrush,
            Margin = new Thickness(6, 2, 6, 4),
        });

        var options = new (string Label, int Minutes)[]
        {
            ("关闭定时", 0),
            ("15 分钟后", 15),
            ("30 分钟后", 30),
            ("45 分钟后", 45),
            ("60 分钟后", 60),
        };

        foreach (var (label, mins) in options)
        {
            int m = mins;
            bool isCancelRow = m == 0;
            var rowText = new TextBlock
            {
                Text = isCancelRow && _currentHasTimer ? $"关闭定时（剩 {FormatTime(_currentTimerRemaining)}）" : label,
                FontSize = 11.5,
                Foreground = isCancelRow && _currentHasTimer ? _accentCyanBrush : _textBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var itemBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Content = rowText,
            };
            itemBtn.PointerEntered += (_, _) =>
            {
                itemBtn.Background = _hoverFillBrush;
                itemBtn.BorderBrush = _hoverBorderBrush;
            };
            itemBtn.PointerExited += (_, _) =>
            {
                itemBtn.Background = new SolidColorBrush(Colors.Transparent);
                itemBtn.BorderBrush = new SolidColorBrush(Colors.Transparent);
            };
            itemBtn.Tapped += (_, e) => e.Handled = true;
            itemBtn.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(_currentInstanceId))
                {
                    SetSleepTimerRequested?.Invoke(_currentInstanceId, m);
                }
                CloseAlarmPopup();
            };
            _alarmPopupList.Children.Add(itemBtn);
        }
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
                Glyph = dev.Glyph,
                FontSize = 13,
                Foreground = dev.IsDefault ? _accentCyanBrush : _mutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var nameText = new TextBlock
            {
                Text = dev.Name,
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
        btn.PointerPressed += (_, _) => btn.Background = _pressedFillBrush;
        btn.PointerReleased += (_, _) => btn.Background = _hoverFillBrush;

        return btn;
    }

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
                _hintDelayTimer?.Stop();
                HideHint();
            }
        };
    }

    private void ScheduleCursorHint(UIElement target, Func<string> textProvider, PointerRoutedEventArgs e)
    {
        if (_progress < 0.7 || _dropdownOpen || _alarmPopupOpen)
        {
            HideHint();
            return;
        }

        _hintTarget = target;
        _hintTextProvider = textProvider;
        _hintPendingPt = e.GetCurrentPoint(_hintCanvas).Position;
        HideHint();

        if (_hintDelayTimer is not null)
        {
            _hintDelayTimer.Stop();
            _hintDelayTimer.Start();
        }
    }

    private void OnHintDelayElapsed()
    {
        if (_hintTarget is null || _hintTextProvider is null || _progress < 0.7 || _dropdownOpen || _alarmPopupOpen)
        {
            HideHint();
            return;
        }

        _hintText.Text = _hintTextProvider();
        _hintPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double pillW = _hintPill.DesiredSize.Width > 0 ? _hintPill.DesiredSize.Width : 100;
        double pillH = _hintPill.DesiredSize.Height > 0 ? _hintPill.DesiredSize.Height : 24;
        double canvasW = _hintCanvas.ActualWidth > 10 ? _hintCanvas.ActualWidth : 528;
        double canvasH = _hintCanvas.ActualHeight > 10 ? _hintCanvas.ActualHeight : 188;

        // 统一轻量偏移：(+7, +11)，延迟 1000ms 后固定显示，鼠标离开按钮才隐藏
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
        _hintDelayTimer?.Stop();
        _hintPill.Opacity = 0;
        _hintVisible = false;
    }

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
        double ease = 1 - Math.Pow(1 - t, 3);

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
        if (_progress < 0.5 || _dropdownOpen || _alarmPopupOpen || _isSeeking) return;

        // 若鼠标当前悬停在喇叭图标或音量条弹出状态，滚轮行为切换为步进 2 修改音量
        if (_isHoveringVolBtn || _isHoveringVolPopup || _volPopupProgress > 0.15)
        {
            OnVolumePointerWheelChanged(sender, e);
            return;
        }

        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;

        e.Handled = true;
        int dir = delta > 0 ? -1 : 1;
        StartPageTransition(dir);
        SwitchInstanceRequested?.Invoke(dir);
    }

    private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_progress < 0.5 || _dropdownOpen || _alarmPopupOpen || _isSeeking || _isDraggingVolBar) return;
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;

        _isPointerDown = true;
        _dragSwitched = false;
        _pointerDownPos = pt.Position;
    }

    private void OnRootPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        EnsureAuroraTimerRunning();
        var pos = e.GetCurrentPoint(this).Position;
        _cursorRawX = pos.X;
        _cursorRawY = pos.Y;
        if (!_isPointerInRoot)
        {
            _cursorSmoothX = pos.X;
            _cursorSmoothY = pos.Y;
        }
        _isPointerInRoot = true;
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInRoot = false;
    }

    private void OnCardPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        _cursorRawX = pt.Position.X;
        _cursorRawY = pt.Position.Y;
        _isPointerInRoot = true;

        if (!_isPointerDown || _dragSwitched || _isSeeking || _isDraggingVolBar) return;
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
        if (_dropdownOpen || _alarmPopupOpen)
        {
            CloseDeviceDropdown();
            CloseAlarmPopup();
            e.Handled = true;
            return;
        }
        if (_dragSwitched)
        {
            _dragSwitched = false;
            e.Handled = true;
        }
    }

    // ==================== 图像加载与专辑封面色彩提取 ====================

    private async void UpdateCompactCoverImage(byte[]? bytes, bool triggerPaletteIfCompact = false)
    {
        if (!triggerPaletteIfCompact && ReferenceEquals(bytes, _lastCompactCoverBytes)) return;
        _lastCompactCoverBytes = bytes;
        int ver = ++_compactCoverLoadVer;

        if (bytes is null || bytes.Length == 0)
        {
            _compactCoverImg.Source = null;
            _compactCoverFallback.Opacity = 1;
            if (triggerPaletteIfCompact)
            {
                BeginContourPaletteTransition(
                    Color.FromArgb(255, 56, 189, 248),
                    Color.FromArgb(255, 168, 85, 247),
                    Color.FromArgb(255, 236, 72, 153),
                    allowButtonOrigin: false);
            }
            return;
        }

        try
        {
            var bmp = await BytesToBitmapAsync(bytes);
            if (ver != _compactCoverLoadVer) return;
            _compactCoverImg.Source = bmp;
            _compactCoverFallback.Opacity = 0;

            if (triggerPaletteIfCompact)
            {
                var palette = await ExtractCoverPaletteAsync(bytes);
                if (ver == _compactCoverLoadVer && _progress < 0.5)
                {
                    BeginContourPaletteTransition(palette.Left, palette.Center, palette.Right, allowButtonOrigin: false);
                }
            }
        }
        catch
        {
            if (ver != _compactCoverLoadVer) return;
            _compactCoverImg.Source = null;
            _compactCoverFallback.Opacity = 1;
        }
    }

    private async void UpdateCoverImage(byte[]? bytes, bool force = false)
    {
        if (!force && ReferenceEquals(bytes, _lastCoverBytes)) return;
        _lastCoverBytes = bytes;
        int ver = ++_coverLoadVer;

        if (bytes is null || bytes.Length == 0)
        {
            _coverImg.Source = null;
            _coverFallback.Opacity = 1;
            BeginContourPaletteTransition(
                Color.FromArgb(255, 56, 189, 248),
                Color.FromArgb(255, 168, 85, 247),
                Color.FromArgb(255, 236, 72, 153),
                allowButtonOrigin: _progress >= 0.5);
            return;
        }

        try
        {
            var bmp = await BytesToBitmapAsync(bytes);
            if (ver != _coverLoadVer) return;
            _coverImg.Source = bmp;
            _coverFallback.Opacity = 0;

            var palette = await ExtractCoverPaletteAsync(bytes);
            if (ver == _coverLoadVer)
            {
                BeginContourPaletteTransition(palette.Left, palette.Center, palette.Right, allowButtonOrigin: _progress >= 0.5);
            }
        }
        catch
        {
            if (ver != _coverLoadVer) return;
            _coverImg.Source = null;
            _coverFallback.Opacity = 1;
        }
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
                    byte b = raw[idx];
                    byte g = raw[idx + 1];
                    byte r = raw[idx + 2];
                    byte a = raw[idx + 3];
                    if (a < 100) continue;

                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    // 优先采有一定饱和度与亮度的像素
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
            return (
                Color.FromArgb(255, 56, 189, 248),
                Color.FromArgb(255, 168, 85, 247),
                Color.FromArgb(255, 236, 72, 153));
        }
    }

    private static Color BoostVibrant(byte r, byte g, byte b)
    {
        double avg = (r + g + b) / 3.0;
        // 增强饱和度并保底亮度，确保黑白灰封面也能打出高级轮廓光
        double nr = avg + (r - avg) * 1.45;
        double ng = avg + (g - avg) * 1.45;
        double nb = avg + (b - avg) * 1.45;

        double max = Math.Max(nr, Math.Max(ng, nb));
        if (max < 130 && max > 1)
        {
            double lift = 155.0 / max;
            nr *= lift;
            ng *= lift;
            nb *= lift;
        }
        return Color.FromArgb(
            255,
            (byte)Math.Clamp(nr, 30, 255),
            (byte)Math.Clamp(ng, 30, 255),
            (byte)Math.Clamp(nb, 30, 255));
    }

    private async void UpdateAppIconImage(byte[]? bytes, bool force = false)
    {
        if (!force && ReferenceEquals(bytes, _lastAppIconBytes)) return;
        _lastAppIconBytes = bytes;
        int ver = ++_appIconLoadVer;

        if (bytes is null || bytes.Length == 0)
        {
            _sourceIconImg.Source = null;
            _sourceIconFallback.Opacity = 1;
            return;
        }

        try
        {
            var bmp = await BytesToBitmapAsync(bytes);
            if (ver != _appIconLoadVer) return;
            _sourceIconImg.Source = bmp;
            _sourceIconFallback.Opacity = 0;
        }
        catch
        {
            if (ver != _appIconLoadVer) return;
            _sourceIconImg.Source = null;
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

        // 极光波浪带配色
        _wave1TopStop.Color = light ? Color.FromArgb(195, 56, 189, 248) : Color.FromArgb(185, 34, 211, 238);
        _wave1MidStop.Color = light ? Color.FromArgb(110, 45, 212, 191) : Color.FromArgb(100, 45, 212, 191);

        _wave2TopStop.Color = light ? Color.FromArgb(190, 129, 140, 248) : Color.FromArgb(185, 129, 140, 248);
        _wave2MidStop.Color = light ? Color.FromArgb(105, 168, 85, 247) : Color.FromArgb(95, 168, 85, 247);

        _wave3TopStop.Color = light ? Color.FromArgb(170, 232, 121, 249) : Color.FromArgb(165, 217, 70, 239);
        _wave3MidStop.Color = light ? Color.FromArgb(85, 56, 189, 248) : Color.FromArgb(80, 56, 189, 248);

        // 鼠标中心小半径圆形光效（中心强、远离中心弱）
        _cursorStop0.Color = light ? Color.FromArgb(135, 125, 211, 252) : Color.FromArgb(120, 224, 242, 254);
        _cursorStop1.Color = light ? Color.FromArgb(55, 165, 180, 252) : Color.FromArgb(48, 125, 211, 252);
    }

    private Color Neutral(byte alpha) => _theme.IsLight
        ? Color.FromArgb(alpha, 0, 0, 0)
        : Color.FromArgb(alpha, 255, 255, 255);

    // ==================== 逐帧变形动画 ====================

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
