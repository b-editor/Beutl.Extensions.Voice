using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Beutl.Extensions.Voice.Models;

namespace Beutl.Extensions.Voice.Views;

/// <summary>
/// アクセント句のピッチとアクセント位置を視覚的に編集するカスタムコントロール。
/// 上半分: ピッチカーブ（ドラッグで調整可能）
/// 下半分: アクセントバー（クリックでアクセント位置を変更）
/// </summary>
public class AccentPhrasePanel : Control
{
    public static readonly StyledProperty<List<AccentPhraseModel>?> AccentPhrasesProperty =
        AvaloniaProperty.Register<AccentPhrasePanel, List<AccentPhraseModel>?>(nameof(AccentPhrases));

    public static readonly StyledProperty<double> MinPitchProperty =
        AvaloniaProperty.Register<AccentPhrasePanel, double>(nameof(MinPitch), 3.0);

    public static readonly StyledProperty<double> MaxPitchProperty =
        AvaloniaProperty.Register<AccentPhrasePanel, double>(nameof(MaxPitch), 6.5);

    private const double MoraWidth = 36;
    private const double PhraseGap = 16;
    private const double PitchAreaRatio = 0.65;
    private const double AccentBarHeight = 28;
    private const double LabelHeight = 20;
    private const double TopPadding = 8;
    private const double BottomPadding = 4;

    private int _draggingMoraGlobalIndex = -1;
    private bool _isDragging;

    static AccentPhrasePanel()
    {
        AffectsRender<AccentPhrasePanel>(AccentPhrasesProperty, MinPitchProperty, MaxPitchProperty);
    }

    public List<AccentPhraseModel>? AccentPhrases
    {
        get => GetValue(AccentPhrasesProperty);
        set => SetValue(AccentPhrasesProperty, value);
    }

    public double MinPitch
    {
        get => GetValue(MinPitchProperty);
        set => SetValue(MinPitchProperty, value);
    }

    public double MaxPitch
    {
        get => GetValue(MaxPitchProperty);
        set => SetValue(MaxPitchProperty, value);
    }

    public event EventHandler? AccentPhrasesChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        var phrases = AccentPhrases;
        if (phrases == null || phrases.Count == 0)
            return new Size(0, 0);

        double totalWidth = 0;
        for (int i = 0; i < phrases.Count; i++)
        {
            totalWidth += phrases[i].Moras.Count * MoraWidth;
            if (i < phrases.Count - 1)
                totalWidth += PhraseGap;
        }

        double height = TopPadding + 160 + AccentBarHeight + LabelHeight + BottomPadding;
        return new Size(totalWidth, height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var phrases = AccentPhrases;
        if (phrases == null || phrases.Count == 0) return;

        double pitchAreaHeight = (Bounds.Height - TopPadding - AccentBarHeight - LabelHeight - BottomPadding);
        double pitchAreaTop = TopPadding;
        double accentBarTop = pitchAreaTop + pitchAreaHeight;
        double labelTop = accentBarTop + AccentBarHeight;

        var bgBrush = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128));
        context.DrawRectangle(bgBrush, null, new Rect(0, 0, Bounds.Width, Bounds.Height), 4, 4);

        // ピッチ範囲のガイドライン
        var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)), 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = pitchAreaTop + pitchAreaHeight * i / 4.0;
            context.DrawLine(guidePen, new Point(0, y), new Point(Bounds.Width, y));
        }

        double xOffset = 0;
        int globalMoraIndex = 0;

        var phraseColors = new[]
        {
            Color.FromRgb(66, 133, 244),   // Blue
            Color.FromRgb(52, 168, 83),    // Green
            Color.FromRgb(251, 188, 4),    // Yellow
            Color.FromRgb(234, 67, 53),    // Red
            Color.FromRgb(163, 73, 164),   // Purple
            Color.FromRgb(255, 109, 0),    // Orange
        };

        for (int pi = 0; pi < phrases.Count; pi++)
        {
            var phrase = phrases[pi];
            var color = phraseColors[pi % phraseColors.Length];
            var phraseBrush = new SolidColorBrush(color);
            var accentBrush = new SolidColorBrush(Color.FromArgb(180, color.R, color.G, color.B));
            var dimBrush = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B));
            var linePen = new Pen(phraseBrush, 2);
            var dotBrush = phraseBrush;

            double phraseStartX = xOffset;
            double phraseWidth = phrase.Moras.Count * MoraWidth;

            // アクセントバー背景
            var accentBgBrush = new SolidColorBrush(Color.FromArgb(15, color.R, color.G, color.B));
            context.DrawRectangle(accentBgBrush, null,
                new Rect(phraseStartX + 1, accentBarTop, phraseWidth - 2, AccentBarHeight), 2, 2);

            // ピッチカーブ描画
            var pitchPoints = new List<Point>();
            for (int mi = 0; mi < phrase.Moras.Count; mi++)
            {
                var mora = phrase.Moras[mi];
                double cx = xOffset + mi * MoraWidth + MoraWidth / 2;
                double cy = PitchToY(mora.Pitch, pitchAreaTop, pitchAreaHeight);
                pitchPoints.Add(new Point(cx, cy));
            }

            // ピッチ折れ線
            for (int i = 1; i < pitchPoints.Count; i++)
            {
                context.DrawLine(linePen, pitchPoints[i - 1], pitchPoints[i]);
            }

            // ピッチドット
            for (int mi = 0; mi < phrase.Moras.Count; mi++)
            {
                var mora = phrase.Moras[mi];
                var pt = pitchPoints[mi];
                double radius = (globalMoraIndex + mi == _draggingMoraGlobalIndex) ? 6 : 4;
                if (mora.Pitch > 0)
                {
                    context.DrawEllipse(dotBrush, null, pt, radius, radius);
                }
                else
                {
                    // pitch=0 (無声音) は中空ドット
                    var hollowPen = new Pen(phraseBrush, 1.5);
                    context.DrawEllipse(null, hollowPen, pt, radius, radius);
                }
            }

            // アクセントバー描画
            for (int mi = 0; mi < phrase.Moras.Count; mi++)
            {
                double mx = xOffset + mi * MoraWidth;
                bool isHigh = IsHighPitch(phrase.Accent, mi, phrase.Moras.Count);
                var barBrush = isHigh ? accentBrush : dimBrush;
                double barH = isHigh ? AccentBarHeight - 4 : (AccentBarHeight - 4) * 0.4;
                double barY = accentBarTop + (AccentBarHeight - barH) / 2;
                context.DrawRectangle(barBrush, null,
                    new Rect(mx + 2, barY, MoraWidth - 4, barH), 2, 2);
            }

            // モーラテキストラベル
            var labelBrush = new SolidColorBrush(Color.FromArgb(200, 220, 220, 220));
            var typeface = new Typeface("Noto Sans JP, Yu Gothic UI, Meiryo, sans-serif", FontStyle.Normal, FontWeight.Normal);
            for (int mi = 0; mi < phrase.Moras.Count; mi++)
            {
                double mx = xOffset + mi * MoraWidth;
                var ft = new FormattedText(
                    phrase.Moras[mi].Text,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface, 11, labelBrush);
                double textX = mx + (MoraWidth - ft.Width) / 2;
                context.DrawText(ft, new Point(textX, labelTop + 2));
            }

            globalMoraIndex += phrase.Moras.Count;
            xOffset += phraseWidth;
            if (pi < phrases.Count - 1)
                xOffset += PhraseGap;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        var phrases = AccentPhrases;
        if (phrases == null) return;

        double pitchAreaHeight = Bounds.Height - TopPadding - AccentBarHeight - LabelHeight - BottomPadding;
        double accentBarTop = TopPadding + pitchAreaHeight;

        // アクセントバー領域のクリック判定
        if (pos.Y >= accentBarTop && pos.Y <= accentBarTop + AccentBarHeight)
        {
            var (phraseIndex, moraIndex) = HitTestMora(pos.X);
            if (phraseIndex >= 0 && moraIndex >= 0)
            {
                // アクセント位置を変更（moraIndex+1がアクセント位置）
                phrases[phraseIndex].Accent = moraIndex + 1;
                AccentPhrasesChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // ピッチ領域のドラッグ開始判定
        if (pos.Y >= TopPadding && pos.Y < accentBarTop)
        {
            var (phraseIndex, moraIndex) = HitTestMora(pos.X);
            if (phraseIndex >= 0 && moraIndex >= 0)
            {
                _draggingMoraGlobalIndex = GetGlobalMoraIndex(phraseIndex, moraIndex);
                _isDragging = true;
                UpdatePitchFromPointer(pos);
                e.Handled = true;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDragging)
        {
            UpdatePitchFromPointer(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            _isDragging = false;
            _draggingMoraGlobalIndex = -1;
            AccentPhrasesChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private void UpdatePitchFromPointer(Point pos)
    {
        var phrases = AccentPhrases;
        if (phrases == null || _draggingMoraGlobalIndex < 0) return;

        var (phraseIndex, moraIndex) = GlobalIndexToLocal(_draggingMoraGlobalIndex);
        if (phraseIndex < 0) return;

        double pitchAreaHeight = Bounds.Height - TopPadding - AccentBarHeight - LabelHeight - BottomPadding;
        double pitch = YToPitch(pos.Y, TopPadding, pitchAreaHeight);
        pitch = Math.Clamp(pitch, MinPitch, MaxPitch);

        phrases[phraseIndex].Moras[moraIndex].Pitch = Math.Round(pitch, 2);
        InvalidateVisual();
    }

    private double PitchToY(double pitch, double areaTop, double areaHeight)
    {
        if (pitch <= 0) return areaTop + areaHeight;
        double ratio = (pitch - MinPitch) / (MaxPitch - MinPitch);
        ratio = Math.Clamp(ratio, 0, 1);
        return areaTop + areaHeight * (1 - ratio);
    }

    private double YToPitch(double y, double areaTop, double areaHeight)
    {
        double ratio = 1 - (y - areaTop) / areaHeight;
        return MinPitch + ratio * (MaxPitch - MinPitch);
    }

    private static bool IsHighPitch(int accent, int moraIndex, int moraCount)
    {
        // 日本語アクセント規則: accent=1→先頭のみ高い、accent=0→平板(1番目以降全て高い)
        if (accent == 0)
            return moraIndex > 0;
        if (accent == 1)
            return moraIndex == 0;
        // accent=N (N>=2) → 1〜N-1が高い
        return moraIndex >= 1 && moraIndex < accent;
    }

    private (int phraseIndex, int moraIndex) HitTestMora(double x)
    {
        var phrases = AccentPhrases;
        if (phrases == null) return (-1, -1);

        double xOffset = 0;
        for (int pi = 0; pi < phrases.Count; pi++)
        {
            double phraseWidth = phrases[pi].Moras.Count * MoraWidth;
            if (x >= xOffset && x < xOffset + phraseWidth)
            {
                int moraIndex = (int)((x - xOffset) / MoraWidth);
                moraIndex = Math.Clamp(moraIndex, 0, phrases[pi].Moras.Count - 1);
                return (pi, moraIndex);
            }

            xOffset += phraseWidth;
            if (pi < phrases.Count - 1)
                xOffset += PhraseGap;
        }

        return (-1, -1);
    }

    private int GetGlobalMoraIndex(int phraseIndex, int moraIndex)
    {
        var phrases = AccentPhrases;
        if (phrases == null) return -1;

        int global = 0;
        for (int i = 0; i < phraseIndex; i++)
            global += phrases[i].Moras.Count;
        return global + moraIndex;
    }

    private (int phraseIndex, int moraIndex) GlobalIndexToLocal(int globalIndex)
    {
        var phrases = AccentPhrases;
        if (phrases == null) return (-1, -1);

        int remaining = globalIndex;
        for (int i = 0; i < phrases.Count; i++)
        {
            if (remaining < phrases[i].Moras.Count)
                return (i, remaining);
            remaining -= phrases[i].Moras.Count;
        }

        return (-1, -1);
    }
}
