using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Views;

public class PitchCurveEditor : Control
{
    public static readonly StyledProperty<ObservableCollection<MoraViewModel>?> MorasProperty =
        AvaloniaProperty.Register<PitchCurveEditor, ObservableCollection<MoraViewModel>?>(nameof(Moras));

    public static readonly StyledProperty<double> MinPitchProperty =
        AvaloniaProperty.Register<PitchCurveEditor, double>(nameof(MinPitch), 0.0);

    public static readonly StyledProperty<double> MaxPitchProperty =
        AvaloniaProperty.Register<PitchCurveEditor, double>(nameof(MaxPitch), 200.0);

    private int? _draggedIndex;
    private Point _lastDragPosition;

    public ObservableCollection<MoraViewModel>? Moras
    {
        get => GetValue(MorasProperty);
        set => SetValue(MorasProperty, value);
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

    static PitchCurveEditor()
    {
        AffectsRender<PitchCurveEditor>(MorasProperty, MinPitchProperty, MaxPitchProperty);
    }

    public PitchCurveEditor()
    {
        MinHeight = 120;
        MinWidth = 200;
        Background = Brushes.Transparent;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Moras == null || Moras.Count == 0)
            return;

        var bounds = Bounds;
        var padding = 20.0;
        var width = bounds.Width - padding * 2;
        var height = bounds.Height - padding * 2;

        if (width <= 0 || height <= 0)
            return;

        // Draw background
        context.DrawRectangle(
            new SolidColorBrush(Color.Parse("#20FFFFFF")),
            new Pen(new SolidColorBrush(Color.Parse("#40FFFFFF")), 1),
            new Rect(padding, padding, width, height));

        // Draw horizontal grid lines
        var gridBrush = new SolidColorBrush(Color.Parse("#20FFFFFF"));
        var gridPen = new Pen(gridBrush, 1);
        for (int i = 0; i <= 4; i++)
        {
            var y = padding + (height * i / 4.0);
            context.DrawLine(gridPen, new Point(padding, y), new Point(padding + width, y));
        }

        if (Moras.Count < 2)
            return;

        // Calculate positions
        var stepX = width / (Moras.Count - 1);
        var points = new List<Point>();

        for (int i = 0; i < Moras.Count; i++)
        {
            var mora = Moras[i];
            var pitch = mora.Pitch.Value;
            var normalizedPitch = (pitch - MinPitch) / (MaxPitch - MinPitch);
            normalizedPitch = Math.Clamp(normalizedPitch, 0, 1);

            var x = padding + i * stepX;
            var y = padding + height - (normalizedPitch * height);

            points.Add(new Point(x, y));
        }

        // Draw line connecting points
        var lineBrush = new SolidColorBrush(Color.Parse("#4A9EFF"));
        var linePen = new Pen(lineBrush, 2);
        for (int i = 0; i < points.Count - 1; i++)
        {
            context.DrawLine(linePen, points[i], points[i + 1]);
        }

        // Draw points
        var pointBrush = new SolidColorBrush(Color.Parse("#4A9EFF"));
        var pointHoverBrush = new SolidColorBrush(Color.Parse("#6AB0FF"));
        var pointStroke = new Pen(Brushes.White, 2);
        var radius = 6.0;

        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var brush = (_draggedIndex == i) ? pointHoverBrush : pointBrush;
            
            context.DrawEllipse(brush, pointStroke, point, radius, radius);

            // Draw mora text below point
            var formattedText = new FormattedText(
                Moras[i].Text.Value,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Yu Gothic UI"),
                12,
                Brushes.White);

            var textPoint = new Point(
                point.X - formattedText.Width / 2,
                padding + height + 5);
            context.DrawText(formattedText, textPoint);

            // Draw pitch value
            var pitchText = new FormattedText(
                $"{Moras[i].Pitch.Value:F0}",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Yu Gothic UI"),
                10,
                Brushes.White);

            var pitchTextPoint = new Point(
                point.X - pitchText.Width / 2,
                point.Y - 15);
            context.DrawText(pitchText, pitchTextPoint);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Moras == null || Moras.Count == 0)
            return;

        var position = e.GetPosition(this);
        var bounds = Bounds;
        var padding = 20.0;
        var width = bounds.Width - padding * 2;
        var height = bounds.Height - padding * 2;

        if (width <= 0 || height <= 0)
            return;

        var stepX = width / (Moras.Count - 1);
        var clickRadius = 10.0;

        // Find if we clicked on a point
        for (int i = 0; i < Moras.Count; i++)
        {
            var mora = Moras[i];
            var pitch = mora.Pitch.Value;
            var normalizedPitch = (pitch - MinPitch) / (MaxPitch - MinPitch);
            normalizedPitch = Math.Clamp(normalizedPitch, 0, 1);

            var x = padding + i * stepX;
            var y = padding + height - (normalizedPitch * height);

            var distance = Math.Sqrt(Math.Pow(position.X - x, 2) + Math.Pow(position.Y - y, 2));

            if (distance <= clickRadius)
            {
                _draggedIndex = i;
                _lastDragPosition = position;
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_draggedIndex == null || Moras == null)
            return;

        var position = e.GetPosition(this);
        var bounds = Bounds;
        var padding = 20.0;
        var height = bounds.Height - padding * 2;

        if (height <= 0)
            return;

        // Calculate new pitch based on Y position
        var y = position.Y - padding;
        var normalizedPitch = 1.0 - (y / height);
        normalizedPitch = Math.Clamp(normalizedPitch, 0, 1);

        var newPitch = MinPitch + (normalizedPitch * (MaxPitch - MinPitch));
        newPitch = Math.Clamp(newPitch, MinPitch, MaxPitch);

        Moras[_draggedIndex.Value].Pitch.Value = (float)newPitch;

        _lastDragPosition = position;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_draggedIndex != null)
        {
            _draggedIndex = null;
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _draggedIndex = null;
        InvalidateVisual();
    }
}
