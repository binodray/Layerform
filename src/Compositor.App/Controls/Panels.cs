using System.Numerics;
using Compositor.Editing;
using Compositor.Model;
using Compositor.Rendering;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinColor = Windows.UI.Color;

namespace Compositor.App.Controls;

/// <summary>Content for a floating panel that keeps itself in step with the session.</summary>
public abstract class PanelContent : UserControl
{
    protected readonly EditorSession Session;
    protected readonly Binder Binder = new();
    protected PanelContent(EditorSession session) { Session = session; }
    public virtual void Refresh() => Binder.Update();

    protected static CanvasControl Drawing(double width, double height, Action<CanvasControl, CanvasDrawEventArgs> draw)
    {
        var control = new CanvasControl { Width = width, Height = height, ClearColor = Colors.Transparent };
        control.Draw += (s, e) => draw(s, e);
        return control;
    }

    protected static FrameworkElement Footer(Action cancel, Action ok, FrameworkElement? status = null)
    {
        var grid = new Grid();
        grid.Children.Add(Ui.Capsule("Cancel", cancel));
        var right = Ui.Row(10);
        if (status != null) right.Children.Add(status);
        right.Children.Add(Ui.Capsule("OK", ok, accent: true));
        right.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(right);
        return grid;
    }

    protected static StackPanel Column(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    /// <summary>A slider plus an exact field; logarithmic sliders give the small values most of the travel.</summary>
    protected FrameworkElement Control(string title, Func<double> get, Action<double> set, double min, double max, string unit, int decimals, bool logarithmic)
    {
        double step = Math.Pow(10, decimals);
        var slider = Ui.Slider(logarithmic ? Math.Log(min) : min, logarithmic ? Math.Log(max) : max,
            () => logarithmic ? Math.Log(Math.Max(min, get())) : get(),
            v => set(Math.Round((logarithmic ? Math.Exp(v) : v) * step) / step), Binder, double.NaN);
        slider.HorizontalAlignment = HorizontalAlignment.Stretch;
        var field = Ui.Number(get, set, Binder, 56, decimals);
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Ui.Label(title));
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        var right = Ui.WithUnit(field, unit);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }
}

// MARK: Levels (LevelsSheet.swift)

public sealed class LevelsPanel : PanelContent
{
    private readonly CanvasControl histogram, inputHandles, outputHandles;
    private readonly TextBlock loading = Ui.Label("Loading histogram…", 11, brush: Ui.Secondary);
    private readonly TextBlock sampleHint = Ui.Label("", 11, brush: Ui.Secondary);
    private int dragging = -1;

    private LevelsEdit? Edit => Session.Levels;
    private LevelsSettings Settings => Edit?.Settings ?? new LevelsSettings();
    private LevelRange Current => Settings.Current;
    private void Update(Func<LevelsSettings, LevelsSettings> change) => Session.UpdateLevels(change(Settings), Edit?.Preview ?? true);
    private void UpdateRange(Func<LevelRange, LevelRange> change) => Update(s => s.WithCurrent(change(s.Current)));

    public LevelsPanel(EditorSession session) : base(session)
    {
        histogram = Drawing(392, 150, DrawHistogram);
        inputHandles = Drawing(392, 20, (c, e) => DrawHandles(e, false));
        outputHandles = Drawing(392, 20, (c, e) => DrawHandles(e, true));
        HookHandles(inputHandles, false);
        HookHandles(outputHandles, true);
        var channel = Ui.Picker(LevelsChannels.All.Select(c => (c, c.ToName())).ToList(), () => Settings.Channel, c => Update(s => s with { Channel = c }), Binder, 180);
        var histogramHost = new Grid { Background = Ui.Solid(64, 0, 0, 0) };
        histogramHost.Children.Add(histogram);
        loading.Margin = new Thickness(8);
        loading.VerticalAlignment = VerticalAlignment.Top;
        histogramHost.Children.Add(loading);
        FrameworkElement Field(string name, Func<LevelRange, double> get, Func<LevelRange, double, LevelRange> set, int decimals, HorizontalAlignment alignment = HorizontalAlignment.Left)
        {
            var label = Ui.Label(name, 11, brush: Ui.Secondary);
            var number = Ui.Number(() => get(Current), v => UpdateRange(r => set(r, v)), Binder, 80, decimals);
            label.HorizontalAlignment = number.HorizontalAlignment = alignment;
            var column = Column(5, label, number);
            column.HorizontalAlignment = alignment;
            return column;
        }
        var inputs = new Grid();
        inputs.Children.Add(Field("Input black", r => r.Black, (r, v) => r with { Black = v }, 0));
        var gamma = Field("Gamma", r => r.Gamma, (r, v) => r with { Gamma = v }, 2, HorizontalAlignment.Center);
        gamma.HorizontalAlignment = HorizontalAlignment.Center;
        inputs.Children.Add(gamma);
        var white = Field("Input white", r => r.White, (r, v) => r with { White = v }, 0, HorizontalAlignment.Right);
        white.HorizontalAlignment = HorizontalAlignment.Right;
        inputs.Children.Add(white);
        var ramp = new Border
        {
            Height = 14,
            Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops = { new GradientStop { Color = Colors.Black, Offset = 0 }, new GradientStop { Color = Colors.White, Offset = 1 } } },
        };
        var outputs = new Grid();
        outputs.Children.Add(Field("Output black", r => r.OutputBlack, (r, v) => r with { OutputBlack = v }, 0));
        var outputWhite = Field("Output white", r => r.OutputWhite, (r, v) => r with { OutputWhite = v }, 0, HorizontalAlignment.Right);
        outputWhite.HorizontalAlignment = HorizontalAlignment.Right;
        outputs.Children.Add(outputWhite);
        var samples = Ui.Row(8, Ui.Label("Sample", 11, brush: Ui.Secondary));
        foreach (var mode in Enum.GetValues<LevelsSample>())
        {
            var captured = mode;
            var button = Ui.Capsule(mode.ToString(), () =>
            {
                if (Edit is not { } edit) return;
                edit.SampleMode = edit.SampleMode == captured ? null : captured;
                Session.InvalidateCanvas();
            });
            Binder.Add(() => button.BorderBrush = Edit?.SampleMode == captured ? new SolidColorBrush(Colors.DodgerBlue) : null);
            samples.Children.Add(button);
        }
        Binder.Add(() => sampleHint.Text = Edit?.SampleMode is { } m ? $"Click the original layer to set {m.ToString().ToLowerInvariant()}. Click the eyedropper again to stop." : "");
        sampleHint.TextWrapping = TextWrapping.Wrap;
        var autos = Ui.Row(8);
        foreach (var (mode, title) in new[] { (LevelsAuto.Contrast, "Contrast"), (LevelsAuto.Color, "Color"), (LevelsAuto.Neutral, "Color + neutral midtones") })
        {
            var button = Ui.Capsule(title, () => Session.AutoLevels(mode));
            Binder.Add(() => button.IsEnabled = Edit?.HistogramReady == true);
            autos.Children.Add(button);
        }
        var previewRow = new Grid();
        previewRow.Children.Add(Ui.Check("Preview", () => Edit?.Preview ?? true, v => Session.UpdateLevels(Settings, v), Binder));
        var reset = Ui.Capsule("Reset", () => { if (Edit is { } e) e.SampleMode = null; Update(_ => new LevelsSettings()); });
        reset.HorizontalAlignment = HorizontalAlignment.Right;
        previewRow.Children.Add(reset);
        var note = Ui.Label("", 11, brush: Ui.Secondary);
        Binder.Add(() => note.Text = Session.AdjustmentOriginal != null ? "Underlying pixels · alpha-weighted histogram"
            : Session.Selection == null ? "Original pixels · alpha-weighted histogram" : "Original pixels · selection and alpha-weighted histogram");
        Binder.Add(() => loading.Visibility = Edit?.HistogramReady == true ? Visibility.Collapsed : Visibility.Visible);
        Content = new StackPanel
        {
            Spacing = 16, Padding = new Thickness(24), Width = 440,
            Children =
            {
                channel, Column(0, histogramHost, inputHandles), inputs, Column(0, ramp, outputHandles), outputs, samples, sampleHint,
                Column(6, Ui.Label("Auto", 11, brush: Ui.Secondary), autos), previewRow, note, Ui.HorizontalDivider(),
                Footer(Session.CancelLevels, () => _ = Session.CommitLevels()),
            },
        };
    }

    public override void Refresh()
    {
        base.Refresh();
        histogram.Invalidate();
        inputHandles.Invalidate();
        outputHandles.Invalidate();
    }

    private void DrawHistogram(CanvasControl sender, CanvasDrawEventArgs e)
    {
        if (Edit is not { } edit) return;
        var bins = edit.Histogram[Settings.Channel.Index()];
        double peak = LevelsEdit.HistogramScale(bins);
        if (!(peak > 0)) return;
        float w = (float)sender.ActualWidth, h = (float)sender.ActualHeight;
        var color = Settings.Channel switch
        {
            LevelsChannel.Red => Colors.Red, LevelsChannel.Green => Colors.LimeGreen, LevelsChannel.Blue => Colors.DodgerBlue, _ => Colors.Gray,
        };
        for (int i = 0; i < 256; i++)
        {
            float height = h * (float)Math.Clamp(bins[i] / peak, 0, 1);
            e.DrawingSession.FillRectangle(i * w / 256, h - height, w / 256 + 0.1f, height, color);
        }
    }

    private double[] Positions(bool output) => output ? new[] { Current.OutputBlack, Current.OutputWhite }
        : new[] { Current.Black, Current.Black + (Current.White - Current.Black) * Math.Pow(0.5, Current.Gamma), Current.White };

    private void DrawHandles(CanvasDrawEventArgs e, bool output)
    {
        var positions = Positions(output);
        float w = 392;
        for (int i = 0; i < positions.Length; i++)
        {
            float x = (float)(positions[i] / 255 * w);
            var fill = i == 0 ? Colors.Black : i == positions.Length - 1 ? Colors.White : Colors.Gray;
            using var builder = new CanvasPathBuilder(e.DrawingSession);
            builder.BeginFigure(x, 3);
            builder.AddLine(x + 6, 15);
            builder.AddLine(x - 6, 15);
            builder.EndFigure(CanvasFigureLoop.Closed);
            using var triangle = CanvasGeometry.CreatePath(builder);
            e.DrawingSession.FillGeometry(triangle, fill);
            e.DrawingSession.DrawGeometry(triangle, Colors.Gray, 1);
        }
    }

    private void HookHandles(CanvasControl control, bool output)
    {
        control.PointerPressed += (_, e) =>
        {
            double x = e.GetCurrentPoint(control).Position.X / 392 * 255;
            var positions = Positions(output);
            dragging = Enumerable.Range(0, positions.Length).OrderBy(i => Math.Abs(positions[i] - x)).First();
            control.CapturePointer(e.Pointer);
            Drag(x, output);
        };
        control.PointerMoved += (_, e) => { if (dragging >= 0) Drag(e.GetCurrentPoint(control).Position.X / 392 * 255, output); };
        control.PointerReleased += (_, e) => { dragging = -1; control.ReleasePointerCapture(e.Pointer); };
    }

    private void Drag(double position, bool output)
    {
        double x = Math.Clamp(position, 0, 255);
        UpdateRange(range =>
        {
            if (output) return dragging == 0 ? range with { OutputBlack = Math.Round(x) } : range with { OutputWhite = Math.Round(x) };
            if (dragging == 0) return range with { Black = Math.Min(range.White - 1, Math.Round(x)) };
            if (dragging == 2) return range with { White = Math.Max(range.Black + 1, Math.Round(x)) };
            double fraction = Math.Clamp((x - range.Black) / (range.White - range.Black), 0.001, 0.999);
            return range with { Gamma = Math.Log(fraction) / Math.Log(0.5) };
        });
    }
}

// MARK: Hue/Saturation (HueSaturationSheet.swift)

public sealed class HueSaturationPanel : PanelContent
{
    private readonly CanvasControl before, handles, after;
    private readonly TextBlock handleText = Ui.Label("", 11, brush: Ui.Secondary);
    private readonly StackPanel spectrum;
    private readonly StackPanel samplers = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly CheckBox invert;
    private int dragging = -1;
    private HueSaturationEdit? Edit => Session.HueSaturation;
    private HueSaturationSettings Current => Edit?.Settings ?? new HueSaturationSettings();
    private void Set(HueSaturationSettings s) => Session.UpdateHueSaturation(s, Edit?.Preview ?? true);
    private bool ShowsSpectrum => Current.Range != ColorRange.Master && !Current.Colorize;

    public HueSaturationPanel(EditorSession session) : base(session)
    {
        var range = Ui.Picker(ColorRanges.All.Select(r => (r, r.ToName())).ToList(), () => Current.Range, r => Set(Current with { Range = r }), Binder, 160);
        Binder.Add(() => range.IsEnabled = !Current.Colorize);
        before = Drawing(412, 16, (c, e) => DrawSpectrum(e, false));
        after = Drawing(412, 16, (c, e) => DrawSpectrum(e, true));
        handles = Drawing(412, 12, DrawHandles);
        handles.PointerPressed += (_, e) =>
        {
            double degrees = Math.Clamp(e.GetCurrentPoint(handles).Position.X / 412, 0, 1) * 360;
            dragging = Nearest(degrees);
            handles.CapturePointer(e.Pointer);
            Set(Current.WithBand(Current.Band.WithHandle(dragging, degrees)));
        };
        handles.PointerMoved += (_, e) =>
        {
            if (dragging < 0) return;
            double degrees = Math.Clamp(e.GetCurrentPoint(handles).Position.X / 412, 0, 1) * 360;
            Set(Current.WithBand(Current.Band.WithHandle(dragging, degrees)));
        };
        handles.PointerReleased += (_, e) => { dragging = -1; handles.ReleasePointerCapture(e.Pointer); };
        spectrum = Column(5, before, handles, after, handleText);
        invert = Ui.Check("Apply outside this range instead", () => Current.InvertRange, v => Set(Current with { InvertRange = v }), Binder);
        var hue = Control("Hue", () => Current.Hue, v => Set(Current.WithCurrent(a => a with { Hue = v })), -180, 360, "°", 0, false);
        var saturation = Control("Saturation", () => Current.Saturation, v => Set(Current.WithCurrent(a => a with { Saturation = v })), -100, 100, "", 0, false);
        var lightness = Control("Lightness", () => Current.Lightness, v => Set(Current.WithCurrent(a => a with { Lightness = v })), -100, 100, "", 0, false);
        var options = Ui.Row(18,
            Ui.Check("Colorize", () => Current.Colorize, v => Set(v ? HueSaturationSettings.ColorizeStart : new HueSaturationSettings()), Binder),
            Ui.Check("Preview", () => Edit?.Preview ?? true, v => Session.UpdateHueSaturation(Current, v), Binder),
            Ui.Capsule("Reset", () => Set(Current.Colorize ? HueSaturationSettings.ColorizeStart : new HueSaturationSettings())));
        var limited = Ui.Label("Limited to the selection", 12, brush: Ui.Secondary);
        Binder.Add(() => limited.Visibility = Session.AdjustmentOriginal == null && Session.Selection != null ? Visibility.Visible : Visibility.Collapsed);
        Binder.Add(() => { spectrum.Visibility = invert.Visibility = ShowsSpectrum ? Visibility.Visible : Visibility.Collapsed; });
        Binder.Add(() => handleText.Text = string.Join("   ", Current.Band.Handles.Select(h => $"{Math.Round(h)}°")));
        BuildSamplers();
        var top = new Grid();
        top.Children.Add(range);
        samplers.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(samplers);
        Content = new StackPanel
        {
            Spacing = 16, Padding = new Thickness(24), Width = 460,
            Children = { top, hue, saturation, lightness, spectrum, invert, options, limited, Ui.HorizontalDivider(),
                Footer(Session.CancelHueSaturation, () => _ = Session.CommitHueSaturation()) },
        };
    }

    private void BuildSamplers()
    {
        foreach (var mode in Enum.GetValues<HueSampleMode>())
        {
            var captured = mode;
            var label = mode == HueSampleMode.Replace ? "Sample" : mode == HueSampleMode.Add ? "Add" : "Remove";
            var button = Ui.IconButton(Icons.Tool(NavigationTool.Eyedropper, Session), () =>
            {
                Session.HueTargeting = false;
                Session.HueSampleMode = Session.HueSampleMode == captured ? null : captured;
                Session.Notify();
            }, mode switch
            {
                HueSampleMode.Replace => "Click the image to center this range on that color",
                HueSampleMode.Add => "Click the image to widen this range to include that color",
                _ => "Click the image to narrow this range to exclude that color",
            }, 28, 24);
            if (mode != HueSampleMode.Replace)
                button.Content = new Grid { Children = { Icons.Tool(NavigationTool.Eyedropper, Session), new TextBlock { Text = mode == HueSampleMode.Add ? "+" : "−", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom } } };
            Binder.Add(() => { button.Visibility = ShowsSpectrum ? Visibility.Visible : Visibility.Collapsed; button.Background = Session.HueSampleMode == captured ? Ui.Solid(64, 10, 132, 255) : new SolidColorBrush(Colors.Transparent); });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{label} color");
            samplers.Children.Add(button);
        }
        var targeted = Ui.IconButton(Icons.Tool(NavigationTool.Hand, Session), () =>
        {
            Session.HueSampleMode = null;
            Session.HueTargeting = !Session.HueTargeting;
            Session.Notify();
        }, "Targeted adjustment: drag on the image to change that color's saturation, or its hue with Ctrl held", 28, 24);
        Binder.Add(() => { targeted.Visibility = Current.Colorize ? Visibility.Collapsed : Visibility.Visible; targeted.Background = Session.HueTargeting ? Ui.Solid(64, 10, 132, 255) : new SolidColorBrush(Colors.Transparent); });
        samplers.Children.Add(targeted);
    }

    private int Nearest(double degrees)
    {
        var distances = Current.Band.Handles.Select(h => { double gap = Math.Abs(h - degrees) % 360; return Math.Min(gap, 360 - gap); }).ToList();
        return distances.IndexOf(distances.Min());
    }

    public override void Refresh()
    {
        base.Refresh();
        before.Invalidate();
        after.Invalidate();
        handles.Invalidate();
    }

    private void DrawSpectrum(CanvasDrawEventArgs e, bool shifted)
    {
        const int slices = 72;
        float width = 412f / slices;
        for (int slice = 0; slice < slices; slice++)
        {
            double hue = slice / (double)slices * 360;
            double shown = shifted ? AdjustmentPixels.ShiftedHue(hue, Current) : hue;
            var rgb = new PickerHsb(shown, 1, 1).Rgb;
            e.DrawingSession.FillRectangle(slice * width, 0, width + 0.5f, 16, Ui.Color(rgb));
        }
    }

    private void DrawHandles(CanvasControl sender, CanvasDrawEventArgs e)
    {
        var handleValues = Current.Band.Handles;
        for (int index = 0; index < 4; index++)
        {
            float x = (float)(handleValues[index] / 360 * 412);
            if (index is 1 or 2) e.DrawingSession.FillRectangle(x - 1, 0, 2, 12, Colors.White);
            else e.DrawingSession.FillRectangle(x - 3.5f, 3.5f, 7, 5, Colors.White);
        }
    }
}

// MARK: Filters (FilterSheet.swift, CurvesControls.swift)

public sealed class FilterPanel : PanelContent
{
    private readonly CanvasControl? curves;
    private int? selected, draggingPoint;
    private readonly TextBlock pointText = Ui.Label("", 12);
    private FilterEdit? Edit => Session.FilterEdit;
    private FilterSettings Settings => Edit?.Settings ?? new FilterSettings();
    private void Update(Func<FilterSettings, FilterSettings> change) => Session.UpdateFilter(change(Settings), Edit?.Preview ?? true);

    public FilterPanel(EditorSession session, Action<bool> pickGradientColor) : base(session)
    {
        var kind = Edit?.Kind ?? FilterKind.GaussianBlur;
        var body = new StackPanel { Spacing = 16 };
        switch (kind)
        {
            case FilterKind.Curves:
                curves = Drawing(332, 260, DrawCurves);
                HookCurves(curves);
                var channel = Ui.Picker(LevelsChannels.All.Select(c => (c, c.ToName())).ToList(), () => Settings.Curves.Channel,
                    c => { selected = null; draggingPoint = null; Update(s => s with { Curves = s.Curves with { Channel = c } }); }, Binder);
                var remove = Ui.Capsule("Remove point", () =>
                {
                    var points = Settings.Curves.Channels[Settings.Curves.Channel.Index()];
                    if (selected is { } i && i > 0 && i < points.Count - 1)
                    {
                        var list = points.ToList();
                        list.RemoveAt(i);
                        selected = null;
                        Update(s => s with { Curves = s.Curves.WithChannelPoints(s.Curves.Channel.Index(), list) });
                    }
                });
                Binder.Add(() =>
                {
                    var points = Settings.Curves.Channels[Settings.Curves.Channel.Index()];
                    remove.IsEnabled = selected is { } i && i > 0 && i < points.Count - 1;
                    pointText.Text = selected is { } j && j < points.Count ? $"Input {(int)points[j].X} · Output {(int)points[j].Y}" : "";
                });
                var pointRow = new Grid();
                pointRow.Children.Add(pointText);
                remove.HorizontalAlignment = HorizontalAlignment.Right;
                pointRow.Children.Add(remove);
                body.Children.Add(channel);
                body.Children.Add(new Border { Background = Ui.Solid(90, 0, 0, 0), Child = curves });
                body.Children.Add(Ui.Label("Click to add a point. Drag to adjust.", 11, brush: Ui.Secondary));
                body.Children.Add(pointRow);
                body.Children.Add(Ui.Capsule("Reset curve", () =>
                {
                    selected = null;
                    Update(s => s with { Curves = s.Curves.WithChannelPoints(s.Curves.Channel.Index(), new[] { new CurvePoint(0, 0), new CurvePoint(255, 255) }) });
                }));
                break;
            case FilterKind.Exposure:
                body.Children.Add(Control("Exposure", () => Settings.Exposure.Exposure, v => Update(s => s with { Exposure = s.Exposure with { Exposure = v } }), ExposureSettings.ExposureMin, ExposureSettings.ExposureMax, "", 2, false));
                body.Children.Add(Control("Offset", () => Settings.Exposure.Offset, v => Update(s => s with { Exposure = s.Exposure with { Offset = v } }), ExposureSettings.OffsetMin, ExposureSettings.OffsetMax, "", 4, false));
                body.Children.Add(Control("Gamma", () => Settings.Exposure.Gamma, v => Update(s => s with { Exposure = s.Exposure with { Gamma = v } }), ExposureSettings.GammaMin, ExposureSettings.GammaMax, "", 2, true));
                break;
            case FilterKind.GradientMap:
                var bar = new Border { Height = 20, CornerRadius = new CornerRadius(4), BorderBrush = Ui.Solid(90, 0, 0, 0), BorderThickness = new Thickness(1) };
                Binder.Add(() =>
                {
                    var (dark, light) = Settings.GradientMap.Ends;
                    bar.Background = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0),
                        GradientStops = { new GradientStop { Offset = 0, Color = Ui.Color(new PaletteColor(dark.Red, dark.Green, dark.Blue)) },
                                          new GradientStop { Offset = 1, Color = Ui.Color(new PaletteColor(light.Red, light.Green, light.Blue)) } },
                    };
                });
                FrameworkElement End(string title, bool highlights) => Ui.Row(8,
                    Ui.Swatch(() => { var c = highlights ? Settings.GradientMap.Highlights : Settings.GradientMap.Shadows; return new PaletteColor(c.Red, c.Green, c.Blue); },
                        () => pickGradientColor(highlights), Binder, 24, 24, $"Choose the {title.ToLowerInvariant()} color"), Ui.Label(title));
                body.Children.Add(bar);
                body.Children.Add(Ui.Row(20, End("Shadows", false), End("Highlights", true)));
                body.Children.Add(Ui.Check("Reverse", () => Settings.GradientMap.Reversed, v => Update(s => s with { GradientMap = s.GradientMap with { Reversed = v } }), Binder));
                break;
            case FilterKind.Grain:
                body.Children.Add(Control("Amount", () => Settings.Grain.Amount, v => Update(s => s with { Grain = s.Grain with { Amount = v } }), GrainSettings.AmountMin, GrainSettings.AmountMax, "", 0, false));
                body.Children.Add(Control("Size", () => Settings.Grain.Size, v => Update(s => s with { Grain = s.Grain with { Size = v } }), GrainSettings.SizeMin, GrainSettings.SizeMax, "px", 1, true));
                body.Children.Add(Control("Roughness", () => Settings.Grain.Roughness, v => Update(s => s with { Grain = s.Grain with { Roughness = v } }), GrainSettings.RoughnessMin, GrainSettings.RoughnessMax, "", 0, false));
                break;
            case FilterKind.RemoveBackground:
                body.Children.Add(Wrap("Hide the background behind a layer mask, keeping the foreground subjects. The pixels stay, so the background can be painted back at any time."));
                body.Children.Add(Ui.Segmented(new[] { (BackgroundQuality.Basic, "Basic"), (BackgroundQuality.Advanced, "Advanced") }, () => Settings.BackgroundQuality,
                    v => Update(s => s with { BackgroundQuality = v }), Binder, "Basic is quick; Advanced refines the mask against the layer's own detail, for hair and fur"));
                var advanced = Column(16,
                    Control("Refine", () => Settings.RefineEdges, v => Update(s => s with { RefineEdges = v }), 0, 40, "px", 0, false),
                    Control("Contrast", () => Settings.MatteContrast, v => Update(s => s with { MatteContrast = v }), 0, 100, "%", 0, false),
                    Control("Shift Edge", () => Settings.ShiftEdge, v => Update(s => s with { ShiftEdge = v }), -10, 10, "px", 0, false));
                Binder.Add(() => advanced.Visibility = Settings.BackgroundQuality == BackgroundQuality.Advanced ? Visibility.Visible : Visibility.Collapsed);
                body.Children.Add(advanced);
                break;
            case FilterKind.ContentAwareFill:
                body.Children.Add(Wrap("Fill the selection using surrounding pixels from this layer."));
                break;
            case FilterKind.GaussianBlur:
                body.Children.Add(Control("Radius", () => Settings.Radius, v => Update(s => s with { Radius = v }), 0.1, 250, "px", 1, true));
                break;
            case FilterKind.MotionBlur:
                body.Children.Add(Control("Angle", () => Settings.Angle, v => Update(s => s with { Angle = v }), -90, 90, "°", 0, false));
                body.Children.Add(Control("Distance", () => Settings.Distance, v => Update(s => s with { Distance = v }), 1, 2000, "px", 0, true));
                break;
            case FilterKind.AddNoise:
                body.Children.Add(Control("Amount", () => Settings.Amount, v => Update(s => s with { Amount = v }), 0.1, 400, "%", 1, true));
                body.Children.Add(Ui.Segmented(new[] { (false, "Uniform"), (true, "Gaussian") }, () => Settings.Gaussian, v => Update(s => s with { Gaussian = v }), Binder));
                body.Children.Add(Ui.Check("Monochromatic", () => Settings.Monochromatic, v => Update(s => s with { Monochromatic = v }), Binder));
                break;
            case FilterKind.LensCorrection:
                body.Children.Add(Control("Remove Distortion", () => Settings.Distortion, v => Update(s => s with { Distortion = v }), -100, 100, "", 0, false));
                body.Children.Add(Wrap("Positive straightens lines that bow outward (barrel); negative, lines that bow inward (pincushion).", true));
                break;
        }
        body.Children.Add(Ui.Check("Preview", () => Edit?.Preview ?? true, v => Session.UpdateFilter(Settings, v), Binder));
        var error = Wrap("");
        error.Foreground = new SolidColorBrush(Colors.Orange);
        Binder.Add(() => { error.Text = Edit?.PreviewError ?? ""; error.Visibility = Edit?.PreviewError == null ? Visibility.Collapsed : Visibility.Visible; });
        body.Children.Add(error);
        var limited = Ui.Label("Limited to the selection", 12, brush: Ui.Secondary);
        Binder.Add(() => limited.Visibility = Session.AdjustmentOriginal == null && Session.Selection != null ? Visibility.Visible : Visibility.Collapsed);
        body.Children.Add(limited);
        body.Children.Add(Ui.HorizontalDivider());
        var status = Ui.Row(6, new ProgressRing { Width = 16, Height = 16, IsActive = true }, Ui.Label("", 12, brush: Ui.Secondary));
        Binder.Add(() =>
        {
            bool busy = Edit?.Committing == true || Edit?.Preparing == true;
            status.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)status.Children[1]).Text = Edit?.Committing == true ? "Applying…" : "Working…";
        });
        body.Children.Add(Footer(Session.CancelFilter, () => _ = Session.CommitFilter(), status));
        body.Padding = new Thickness(24);
        body.Width = 380;
        Content = body;
    }

    private static TextBlock Wrap(string text, bool secondary = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12,
        Foreground = secondary ? Ui.Secondary : Ui.Brush("TextFillColorPrimaryBrush"),
    };

    public override void Refresh()
    {
        base.Refresh();
        curves?.Invalidate();
    }

    private IReadOnlyList<CurvePoint> Points => Settings.Curves.Channels[Settings.Curves.Channel.Index()];

    private void DrawCurves(CanvasControl sender, CanvasDrawEventArgs e)
    {
        var ds = e.DrawingSession;
        float w = (float)sender.ActualWidth, h = (float)sender.ActualHeight;
        Vector2 Position(double x, double y) => new((float)(x / 255 * w), (float)((1 - y / 255) * h));
        var grid = WinColor.FromArgb(31, 255, 255, 255);
        for (int i = 0; i <= 4; i++)
        {
            float f = i / 4f;
            ds.DrawLine(f * w, 0, f * w, h, grid);
            ds.DrawLine(0, f * h, w, f * h, grid);
        }
        using var builder = new CanvasPathBuilder(ds);
        int channel = Settings.Curves.Channel.Index();
        builder.BeginFigure(Position(0, Settings.Curves.Value(0, channel)));
        for (int x = 1; x <= 255; x++) builder.AddLine(Position(x, Settings.Curves.Value(x, channel)));
        builder.EndFigure(CanvasFigureLoop.Open);
        using var line = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(line, Colors.White, 2);
        var points = Points;
        for (int i = 0; i < points.Count; i++)
            ds.FillCircle(Position(points[i].X, points[i].Y), 4, selected == i ? Colors.DodgerBlue : Colors.White);
    }

    private void HookCurves(CanvasControl control)
    {
        void Drag(Windows.Foundation.Point location)
        {
            double x = Math.Clamp(location.X / control.ActualWidth * 255, 0, 255);
            double y = Math.Clamp(255 - location.Y / control.ActualHeight * 255, 0, 255);
            var p = Points.ToList();
            if (draggingPoint == null)
            {
                int nearest = Enumerable.Range(0, p.Count).OrderBy(i => Math.Sqrt(Math.Pow(p[i].X - x, 2) + Math.Pow(p[i].Y - y, 2))).First();
                if (Math.Sqrt(Math.Pow(p[nearest].X - x, 2) + Math.Pow(p[nearest].Y - y, 2)) < 14) draggingPoint = nearest;
                else if (p.Count < 32 && x > 1 && x < 254 && p.All(q => Math.Abs(q.X - x) > 1))
                {
                    p.Add(new CurvePoint(x, y));
                    p = p.OrderBy(q => q.X).ToList();
                    draggingPoint = p.FindIndex(q => q.X == x);
                }
            }
            if (draggingPoint is not { } i || i < 0 || i >= p.Count) { Update(s => s with { Curves = s.Curves.WithChannelPoints(s.Curves.Channel.Index(), p) }); return; }
            selected = i;
            double nx = i > 0 && i < p.Count - 1 ? Math.Min(p[i + 1].X - 1, Math.Max(p[i - 1].X + 1, x)) : p[i].X;
            p[i] = new CurvePoint(nx, y);
            Update(s => s with { Curves = s.Curves.WithChannelPoints(s.Curves.Channel.Index(), p) });
        }
        control.PointerPressed += (_, e) => { control.CapturePointer(e.Pointer); Drag(e.GetCurrentPoint(control).Position); };
        control.PointerMoved += (_, e) => { if (draggingPoint != null) Drag(e.GetCurrentPoint(control).Position); };
        control.PointerReleased += (_, e) => { draggingPoint = null; control.ReleasePointerCapture(e.Pointer); };
    }
}

// MARK: Color picker (ColorPickerSheet.swift)

public sealed class ColorPickerPanel : PanelContent
{
    private readonly CanvasControl field, strip, preview;
    private readonly TextBox hex = new() { Width = 84, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly Action<bool> finish;
    private bool hexFocused;
    private const float Size = 256;
    private ColorPickerState? State => Session.ColorPicker;

    public ColorPickerPanel(EditorSession session, Action<bool> finish) : base(session)
    {
        this.finish = finish;
        field = Drawing(Size, Size, DrawField);
        strip = Drawing(34, Size, DrawStrip);
        preview = Drawing(64, 64, (c, e) =>
        {
            if (State is { } s) e.DrawingSession.FillRoundedRectangle(0, 0, 64, 64, 5, 5, Ui.Color(s.Color));
            e.DrawingSession.DrawRoundedRectangle(0.5f, 0.5f, 63, 63, 5, 5, WinColor.FromArgb(153, 0, 0, 0), 1);
        });
        void FieldDrag(Windows.Foundation.Point p)
        {
            if (State is not { } s) return;
            s.Hsb.Saturation = Math.Clamp(p.X / Size, 0, 1);
            s.Hsb.Brightness = 1 - Math.Clamp(p.Y / Size, 0, 1);
            Changed();
        }
        void StripDrag(Windows.Foundation.Point p)
        {
            if (State is not { } s) return;
            s.Hsb.Hue = (1 - Math.Clamp(p.Y / Size, 0, 1)) * 360;
            Changed();
        }
        Hook(field, FieldDrag);
        Hook(strip, StripDrag);
        var fields = new Grid { RowSpacing = 6, ColumnSpacing = 8 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        foreach (var (label, index) in new[] { ("R", 0), ("G", 1), ("B", 2) })
        {
            fields.RowDefinitions.Add(new RowDefinition());
            var l = Ui.Label(label);
            Grid.SetRow(l, row);
            fields.Children.Add(l);
            var captured = index;
            var box = Ui.Number(() => Channel(captured), v => SetChannel(captured, v), Binder, 52);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            fields.Children.Add(box);
            row++;
        }
        fields.RowDefinitions.Add(new RowDefinition());
        var hash = Ui.Label("#");
        Grid.SetRow(hash, row);
        fields.Children.Add(hash);
        Grid.SetRow(hex, row);
        Grid.SetColumn(hex, 1);
        fields.Children.Add(hex);
        hex.GotFocus += (_, _) => hexFocused = true;
        hex.LostFocus += (_, _) => { hexFocused = false; CommitHex(); };
        hex.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { CommitHex(); e.Handled = true; } };
        Binder.Add(() => { if (!hexFocused && State is { } s) hex.Text = s.Color.Hex; });
        var buttons = Column(8, Ui.Capsule("OK", () => finish(true), accent: true), Ui.Capsule("Cancel", () => finish(false)));
        foreach (Button b in buttons.Children) b.HorizontalAlignment = HorizontalAlignment.Stretch;
        buttons.Width = 90;
        var right = new StackPanel { Width = 180, Spacing = 12, Children = { Ui.Row(16, preview, buttons), fields, Ui.Label("Click the canvas to sample", 11, brush: Ui.Secondary) } };
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Padding = new Thickness(20), Children = { field, strip, right } };
    }

    private static void Hook(CanvasControl control, Action<Windows.Foundation.Point> drag)
    {
        bool down = false;
        control.PointerPressed += (_, e) => { down = true; control.CapturePointer(e.Pointer); drag(e.GetCurrentPoint(control).Position); };
        control.PointerMoved += (_, e) => { if (down) drag(e.GetCurrentPoint(control).Position); };
        control.PointerReleased += (_, e) => { down = false; control.ReleasePointerCapture(e.Pointer); };
    }

    private double Channel(int index) => State is { } s ? Math.Round((index == 0 ? s.Color.Red : index == 1 ? s.Color.Green : s.Color.Blue) * 255) : 0;

    private void SetChannel(int index, double value)
    {
        if (State is not { } s) return;
        var c = s.Color;
        double v = Math.Clamp(Math.Round(value), 0, 255) / 255;
        s.Hsb.SetRgb(index == 0 ? c with { Red = v } : index == 1 ? c with { Green = v } : c with { Blue = v });
        Changed();
    }

    private void CommitHex()
    {
        if (State is { } s && PaletteColor.FromHex(hex.Text) is { } parsed) { s.Hsb.SetRgb(parsed); Changed(); }
        if (State is { } st) hex.Text = st.Color.Hex;
    }

    private void Changed()
    {
        Session.PreviewGradientMapColor();
        Session.Notify();
        Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
        field.Invalidate();
        strip.Invalidate();
        preview.Invalidate();
    }

    private void DrawField(CanvasControl sender, CanvasDrawEventArgs e)
    {
        if (State is not { } s) return;
        var ds = e.DrawingSession;
        var pure = Ui.Color(new PickerHsb(s.Hsb.Hue, 1, 1).Rgb);
        using (var horizontal = new CanvasLinearGradientBrush(ds, Colors.White, pure) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(Size, 0) })
            ds.FillRectangle(0, 0, Size, Size, horizontal);
        using (var vertical = new CanvasLinearGradientBrush(ds, Colors.Transparent, Colors.Black) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, Size) })
            ds.FillRectangle(0, 0, Size, Size, vertical);
        var marker = new Vector2((float)(s.Hsb.Saturation * Size), (float)((1 - s.Hsb.Brightness) * Size));
        ds.DrawCircle(marker, 6, Colors.White, 1.5f);
        ds.DrawCircle(marker, 6.75f, Colors.Black, 0.75f);
        ds.DrawRectangle(0.5f, 0.5f, Size - 1, Size - 1, WinColor.FromArgb(153, 0, 0, 0), 1);
    }

    private void DrawStrip(CanvasControl sender, CanvasDrawEventArgs e)
    {
        if (State is not { } s) return;
        var ds = e.DrawingSession;
        for (int y = 0; y < (int)Size; y++)
            ds.FillRectangle(7, y, 20, 1, Ui.Color(new PickerHsb((1 - y / Size) * 360, 1, 1).Rgb));
        ds.DrawRectangle(7.5f, 0.5f, 19, Size - 1, WinColor.FromArgb(153, 0, 0, 0), 1);
        float marker = (float)((1 - s.Hsb.Hue / 360) * Size);
        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(0, marker - 5); builder.AddLine(7, marker); builder.AddLine(0, marker + 5); builder.EndFigure(CanvasFigureLoop.Closed);
        builder.BeginFigure(34, marker - 5); builder.AddLine(27, marker); builder.AddLine(34, marker + 5); builder.EndFigure(CanvasFigureLoop.Closed);
        using var arrows = CanvasGeometry.CreatePath(builder);
        ds.FillGeometry(arrows, Colors.White);
    }
}
