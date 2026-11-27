using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Glowline.Models;

public enum DesignLayout { Adaptive, Lateral, Fusion, Minimal, Karaoke }
public enum DesignAlignment { Left, Center, Right }
public enum BackgroundKind { Solid, Gradient, CoverBlur, CoverSolid }
public enum ParticleKind { None, Dust, Stars, Sparks, Bubbles }
public enum LyricAnimation { Fade, Slide, Zoom, Bounce, Rise, Blur, Elastic, Pulse, Wave }
public enum VerseTransition { Fade, Slide, Roll, Scale, None }
public enum CoverAnimation { None, Float, Breathe, Tilt, Parallax, Beat }
public enum EasingKind { EaseOut, EaseInOut, Linear, Spring, Bounce }
public enum ChromeStyle { Rounded, Minimal, Hidden }

public abstract class DesignModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void RaisePropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DesignProfile : DesignModel
{
    private int _schemaVersion = 1;
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Mi diseño";
    private string _author = "";
    private string _description = "";
    private DesignLayout _layout = DesignLayout.Adaptive;
    private DesignPalette _palette = new();
    private DesignTypography _typography = new();
    private DesignLyrics _lyrics = new();
    private DesignArtwork _artwork = new();
    private DesignBackground _background = new();
    private DesignAnimations _animations = new();
    private DesignControls _controls = new();
    private bool _reduceMotion;

    public int SchemaVersion { get => _schemaVersion; set => Set(ref _schemaVersion, value); }
    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Author { get => _author; set => Set(ref _author, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public DesignLayout Layout { get => _layout; set => Set(ref _layout, value); }
    public DesignPalette Palette { get => _palette; set => Set(ref _palette, value); }
    public DesignTypography Typography { get => _typography; set => Set(ref _typography, value); }
    public DesignLyrics Lyrics { get => _lyrics; set => Set(ref _lyrics, value); }
    public DesignArtwork Artwork { get => _artwork; set => Set(ref _artwork, value); }
    public DesignBackground Background { get => _background; set => Set(ref _background, value); }
    public DesignAnimations Animations { get => _animations; set => Set(ref _animations, value); }
    public DesignControls Controls { get => _controls; set => Set(ref _controls, value); }
    public bool ReduceMotion { get => _reduceMotion; set => Set(ref _reduceMotion, value); }
}

public sealed class DesignPalette : DesignModel
{
    private string _background = "#0A0A0C";
    private string _backgroundSecondary = "#17171B";
    private double _gradientAngle = 45;
    private string _text = "#F5F5F7";
    private string _secondaryText = "#A8A8B0";
    private string _accent = "#FFFFFF";
    private string _lyricActive = "#FFFFFF";
    private string _lyricInactive = "#66FFFFFF";
    private bool _autoContrast = true;
    private bool _coverFusion = true;

    public string BackgroundColor { get => _background; set => Set(ref _background, value); }
    public string BackgroundSecondaryColor { get => _backgroundSecondary; set => Set(ref _backgroundSecondary, value); }
    public double GradientAngle { get => _gradientAngle; set => Set(ref _gradientAngle, value); }
    public string TextColor { get => _text; set => Set(ref _text, value); }
    public string SecondaryTextColor { get => _secondaryText; set => Set(ref _secondaryText, value); }
    public string AccentColor { get => _accent; set => Set(ref _accent, value); }
    public string LyricActiveColor { get => _lyricActive; set => Set(ref _lyricActive, value); }
    public string LyricInactiveColor { get => _lyricInactive; set => Set(ref _lyricInactive, value); }
    public bool AutoContrast { get => _autoContrast; set => Set(ref _autoContrast, value); }
    public bool CoverFusion { get => _coverFusion; set => Set(ref _coverFusion, value); }
}

public sealed class DesignTypography : DesignModel
{
    private string _fontFamily = "Segoe UI Variable Display";
    private string _fontWeight = "SemiBold";
    private double _titleSize = 26;
    private double _artistSize = 15;
    private double _lyricSize = 29;
    private double _lineHeight = 1.2;

    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }
    public string FontWeight { get => _fontWeight; set => Set(ref _fontWeight, value); }
    public double TitleSize { get => _titleSize; set => Set(ref _titleSize, value); }
    public double ArtistSize { get => _artistSize; set => Set(ref _artistSize, value); }
    public double LyricSize { get => _lyricSize; set => Set(ref _lyricSize, value); }
    public double LineHeight { get => _lineHeight; set => Set(ref _lineHeight, value); }
}

public sealed class DesignLyrics : DesignModel
{
    private DesignAlignment _alignment = DesignAlignment.Center;
    private double _widthPercent = 62;
    private double _spacing = 24;
    private double _verticalOffset;
    private double _inactiveOpacity = 0.42;
    private bool _showBackingVocals = true;
    private bool _introDots = true;
    private bool _interludeSeparation = true;

    public DesignAlignment Alignment { get => _alignment; set => Set(ref _alignment, value); }
    public double WidthPercent { get => _widthPercent; set => Set(ref _widthPercent, value); }
    public double Spacing { get => _spacing; set => Set(ref _spacing, value); }
    public double VerticalOffset { get => _verticalOffset; set => Set(ref _verticalOffset, value); }
    public double InactiveOpacity { get => _inactiveOpacity; set => Set(ref _inactiveOpacity, value); }
    public bool ShowBackingVocals { get => _showBackingVocals; set => Set(ref _showBackingVocals, value); }
    public bool IntroDots { get => _introDots; set => Set(ref _introDots, value); }
    public bool InterludeSeparation { get => _interludeSeparation; set => Set(ref _interludeSeparation, value); }
}

public sealed class DesignArtwork : DesignModel
{
    private bool _visible = true;
    private double _sizePercent = 26;
    private double _animatedSizePercent;
    private double _cornerRadius = 18;
    private double _borderThickness;
    private double _shadowOpacity = 0.55;
    private bool _fusion = false;

    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    public double SizePercent
    {
        get => _sizePercent;
        set
        {
            if (!Set(ref _sizePercent, value)) return;
            if (_animatedSizePercent <= 0) RaisePropertyChanged(nameof(AnimatedSizePercent));
        }
    }

    // Sin valor propio (0) la carátula animada hereda el tamaño estático.
    public double AnimatedSizePercent
    {
        get => _animatedSizePercent > 0
            ? Math.Clamp(_animatedSizePercent, 10, 60)
            : Math.Clamp(_sizePercent, 10, 60);
        set
        {
            if (!double.IsFinite(value)) return;
            Set(ref _animatedSizePercent, Math.Clamp(value, 10, 60));
        }
    }

    public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, value); }
    public double BorderThickness { get => _borderThickness; set => Set(ref _borderThickness, value); }
    public double ShadowOpacity { get => _shadowOpacity; set => Set(ref _shadowOpacity, value); }
    public bool Fusion { get => _fusion; set => Set(ref _fusion, value); }
}

public sealed class DesignBackground : DesignModel
{
    private BackgroundKind _kind = BackgroundKind.CoverBlur;
    private double _blur = 55;
    private double _dim = 0.55;
    private double _coverScale = 1.0;
    private ParticleKind _particles = ParticleKind.None;
    private double _particleIntensity = 0.5;
    private bool _beatReactive = true;

    public BackgroundKind Kind { get => _kind; set => Set(ref _kind, value); }
    public double Blur { get => _blur; set => Set(ref _blur, value); }
    public double Dim { get => _dim; set => Set(ref _dim, value); }
    public double CoverScale { get => _coverScale; set => Set(ref _coverScale, value); }
    public ParticleKind Particles { get => _particles; set => Set(ref _particles, value); }
    public double ParticleIntensity { get => _particleIntensity; set => Set(ref _particleIntensity, value); }
    public bool BeatReactive { get => _beatReactive; set => Set(ref _beatReactive, value); }
}

public sealed class DesignAnimations : DesignModel
{
    private LyricAnimation _wordAnimation = LyricAnimation.Fade;
    private int _wordDurationMs = 110;
    private double _wordIntensity = 0.65;
    private VerseTransition _verseTransition = VerseTransition.Fade;
    private int _transitionDurationMs = 300;
    private CoverAnimation _coverAnimation = CoverAnimation.None;
    private double _coverIntensity = 0.5;
    private EasingKind _easing = EasingKind.EaseOut;

    public LyricAnimation WordAnimation { get => _wordAnimation; set => Set(ref _wordAnimation, value); }
    public int WordDurationMs { get => _wordDurationMs; set => Set(ref _wordDurationMs, value); }
    public double WordIntensity { get => _wordIntensity; set => Set(ref _wordIntensity, value); }
    public VerseTransition VerseTransition { get => _verseTransition; set => Set(ref _verseTransition, value); }
    public int TransitionDurationMs { get => _transitionDurationMs; set => Set(ref _transitionDurationMs, value); }
    public CoverAnimation CoverAnimation { get => _coverAnimation; set => Set(ref _coverAnimation, value); }
    public double CoverIntensity { get => _coverIntensity; set => Set(ref _coverIntensity, value); }
    public EasingKind Easing { get => _easing; set => Set(ref _easing, value); }
}

public sealed class DesignControls : DesignModel
{
    private bool _showTransport = true;
    private bool _showProgress = true;
    private bool _showMetadata = true;
    private bool _showTitle = true;
    private double _cornerRadius = 999;
    private ChromeStyle _chromeStyle = ChromeStyle.Rounded;

    public bool ShowTransport { get => _showTransport; set => Set(ref _showTransport, value); }
    public bool ShowProgress { get => _showProgress; set => Set(ref _showProgress, value); }
    public bool ShowMetadata { get => _showMetadata; set => Set(ref _showMetadata, value); }
    public bool ShowTitle { get => _showTitle; set => Set(ref _showTitle, value); }
    public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, value); }
    public ChromeStyle ChromeStyle { get => _chromeStyle; set => Set(ref _chromeStyle, value); }
}
