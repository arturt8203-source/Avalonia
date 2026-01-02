using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Media;
using Elektrykpomocnik.ViewModels;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.IO;

namespace Elektrykpomocnik.Avalonia;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private Border? _canvasContainer;
    private Canvas? _zoomContainer;
    private Border? _dragPreviewBorder;
    private Image? _dragPreviewImage;
    private Border? _selectionRectangle;

    // Marquee selection state
    private bool _isSelecting;
    private Point _selectionStartPoint;

    private void CacheControls()
    {
        _canvasContainer = this.FindControl<Border>("CanvasContainer");
        _zoomContainer = this.FindControl<Canvas>("ZoomContainer");
        _dragPreviewBorder = this.FindControl<Border>("DragPreviewBorder");
        _dragPreviewImage = this.FindControl<Image>("DragPreviewImage");
        _selectionRectangle = this.FindControl<Border>("SelectionRectangle");

        // Attach marquee selection handlers to the CONTAINER, not the Grid
        // This ensures check hits even if ItemsControl or other layers are on top
        if (_canvasContainer != null)
        {
            _canvasContainer.AddHandler(PointerPressedEvent, OnCanvasPointerPressed, global::Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            _canvasContainer.AddHandler(PointerMovedEvent, OnCanvasPointerMoved, global::Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            _canvasContainer.AddHandler(PointerReleasedEvent, OnCanvasPointerReleased, global::Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        CacheControls();
        ViewModel = null!;
    }

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        CacheControls();
        DataContext = ViewModel;
        Title = "Elektryk Pomocnik - Avalonia";

        this.KeyDown += MainWindow_KeyDown;

        // Drag & Drop handlers
        if (_canvasContainer != null)
        {
            _canvasContainer.AddHandler(DragDrop.DragEnterEvent, OnCanvasDragEnter);
            _canvasContainer.AddHandler(DragDrop.DragOverEvent, OnCanvasDragOver);
            _canvasContainer.AddHandler(DragDrop.DragLeaveEvent, OnCanvasDragLeave);
            _canvasContainer.AddHandler(DragDrop.DropEvent, OnCanvasDrop);
        }

        // Subscribe to theme changes
        ViewModel.OnThemeChanged = ApplyTheme;
    }

    private void ApplyTheme(bool isDark)
    {
        // Change theme variant
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = isDark
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            ViewModel.DeleteSelectedCommand.Execute(null);
        }
    }

    private void OnCanvasDragEnter(object? sender, DragEventArgs e)
    {
        // Show drag preview when entering canvas
        if (e.DataTransfer.Contains(DragDropFormats.ModuleFilePath))
        {
            var moduleFilePath = e.DataTransfer.TryGetValue(DragDropFormats.ModuleFilePath);
            if (!string.IsNullOrEmpty(moduleFilePath))
            {
                try
                {
                    string ext = System.IO.Path.GetExtension(moduleFilePath).ToLowerInvariant();
                    var previewImage = _dragPreviewImage;
                    var previewBorder = _dragPreviewBorder;

                    if (previewImage != null && previewBorder != null)
                    {
                        if (ext == ".svg")
                        {
                            var content = System.IO.File.ReadAllText(moduleFilePath);

                            // Calculate size based on zoom level
                            var normResult = NormalizeSvgAndCalculateSize(content, moduleFilePath);

                            var svgSource = global::Avalonia.Svg.Skia.SvgSource.LoadFromSvg(normResult.NormalizedSvg);
                            if (svgSource != null)
                            {
                                previewImage.Source = new global::Avalonia.Svg.Skia.SvgImage { Source = svgSource };
                                previewImage.Width = normResult.Width;
                                previewImage.Height = normResult.Height;
                            }
                        }
                        else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                        {
                            using var fs = System.IO.File.OpenRead(moduleFilePath);
                            previewImage.Source = new global::Avalonia.Media.Imaging.Bitmap(fs);
                        }

                        previewBorder.IsVisible = true;
                    }
                }
                catch { /* Ignore errors */ }
            }
        }
    }

    private void OnCanvasDragOver(object? sender, DragEventArgs e)
    {
        // Accept drag if it comes from our palette
        if (e.DataTransfer.Contains(DragDropFormats.ModuleType))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        // Update drag preview position
        var previewBorder = _dragPreviewBorder;
        if (previewBorder != null && previewBorder.IsVisible)
        {
            var schematicGrid = _zoomContainer;

            if (schematicGrid != null)
            {
                var pos = e.GetPosition(schematicGrid);

                // Set Position (centered)
                Canvas.SetLeft(previewBorder, pos.X - previewBorder.Bounds.Width / 2);
                Canvas.SetTop(previewBorder, pos.Y - previewBorder.Bounds.Height / 2);
            }
        }
    }

    private void OnCanvasDragLeave(object? sender, DragEventArgs e)
    {
        // Hide drag preview when leaving canvas
        var previewBorder = _dragPreviewBorder;
        if (previewBorder != null)
        {
            previewBorder.IsVisible = false;
        }
    }

    private void OnCanvasDrop(object? sender, DragEventArgs e)
    {
        // Hide drag preview
        var previewBorder = _dragPreviewBorder;
        if (previewBorder != null) previewBorder.IsVisible = false;

        if (e.DataTransfer.Contains(DragDropFormats.ModuleType))
        {
            var moduleType = e.DataTransfer.TryGetValue(DragDropFormats.ModuleType);
            var moduleName = e.DataTransfer.TryGetValue(DragDropFormats.ModuleName);
            var moduleFilePath = e.DataTransfer.TryGetValue(DragDropFormats.ModuleFilePath);

            // Get position relative to the Grid control (not the border)
            var schematicGrid = _zoomContainer;
            if (schematicGrid != null)
            {
                // Get drop position directly relative to the grid (World Coords)
                var dropPos = e.GetPosition(schematicGrid);

                // Create new Symbol 
                var newSymbol = new Models.SymbolItem
                {
                    Type = moduleType ?? "Unknown",
                    Label = moduleName ?? "Module",
                    VisualPath = moduleFilePath
                };

                // Always load Visual from file for consistent scaling
                if (!string.IsNullOrEmpty(moduleFilePath))
                {
                    string ext = System.IO.Path.GetExtension(moduleFilePath).ToLowerInvariant();
                    string svgContent = "";

                    try
                    {
                        if (ext == ".svg")
                        {
                            svgContent = System.IO.File.ReadAllText(moduleFilePath);

                            // Set default parameters for distribution block
                            if (moduleFilePath.Contains("blok rozdzielczy", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!newSymbol.Parameters.ContainsKey("BLUE_COVER_VISIBLE"))
                                    newSymbol.Parameters["BLUE_COVER_VISIBLE"] = "True";
                            }

                            // Initial Parameter Extraction
                            // Detect placeholders {{KEY}}
                            var placeholders = Regex.Matches(svgContent, @"{{([A-Z0-9_]+)}}");
                            foreach (Match ph in placeholders)
                            {
                                string key = ph.Groups[1].Value;
                                if (!newSymbol.Parameters.ContainsKey(key))
                                {
                                    // Set defaults based on parameter key and module type
                                    if (key == "CURRENT") newSymbol.Parameters[key] = "40A";
                                    else if (key == "SENSITIVITY") newSymbol.Parameters[key] = "30mA";
                                    else if (key == "TYPE") newSymbol.Parameters[key] = "Typ A";
                                    else if (key == "LABEL")
                                    {
                                        string fileName = System.IO.Path.GetFileNameWithoutExtension(moduleFilePath);
                                        string labelValue = fileName.Split(' ')[0];
                                        newSymbol.Parameters[key] = labelValue;
                                    }
                                    else if (key == "SUBTEXT") newSymbol.Parameters[key] = "";
                                    else newSymbol.Parameters[key] = "?";
                                }
                            }

                            // Apply parameters to SVG content
                            svgContent = ApplyParametersToSvg(svgContent, newSymbol.Parameters);

                            // Calculate dimensions and normalize SVG (e.g. for custom scales like distribution block)
                            var normResult = NormalizeSvgAndCalculateSize(svgContent, moduleFilePath);
                            newSymbol.Width = normResult.Width;
                            newSymbol.Height = normResult.Height;
                            svgContent = normResult.NormalizedSvg;

                            var svgSource = global::Avalonia.Svg.Skia.SvgSource.LoadFromSvg(svgContent);
                            if (svgSource != null)
                            {
                                newSymbol.Visual = new global::Avalonia.Svg.Skia.SvgImage { Source = svgSource };
                            }
                        }
                        else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                        {
                            using var fs = System.IO.File.OpenRead(moduleFilePath);
                            var bitmap = new global::Avalonia.Media.Imaging.Bitmap(fs);
                            newSymbol.Visual = bitmap;
                            newSymbol.Width = bitmap.Size.Width;
                            newSymbol.Height = bitmap.Size.Height;
                        }

                        // Center symbol on cursor
                        newSymbol.X = dropPos.X - newSymbol.Width / 2;
                        newSymbol.Y = dropPos.Y - newSymbol.Height / 2;
                    }
                    catch
                    {
                        // Error loading symbol
                    }
                }

                ViewModel.Symbols.Add(newSymbol);
                ViewModel.StatusMessage = $"Dodano: {moduleName} @ ({newSymbol.X:F0}, {newSymbol.Y:F0})";
            }
        }
    }

    private string ApplyParametersToSvg(string svgContent, System.Collections.Generic.Dictionary<string, string> parameters)
    {
        string result = svgContent;
        foreach (var kvp in parameters)
        {
            result = result.Replace("{{" + kvp.Key + "}}", kvp.Value);
        }

        // Logic for "osłona niebieska" toggle (hides cover, danger icon, and text)
        if (parameters.TryGetValue("BLUE_COVER_VISIBLE", out var visible))
        {
            // Remove existing display="none" from relevant elements to start clean
            result = Regex.Replace(result, @"(<[^>]*id=""(osłona-niebieska|danger)""[^>]*?)\s+display=""none""", "$1");
            // Improved regex for text cleanup: allows any characters (including <) between start of tag and target text
            result = Regex.Replace(result, @"(<text[^>]*?)\s+display=""none""(?=[^>]*?>[\s\S]*?lok rozdzielczy)", "$1");

            if (visible == "False")
            {
                // Hide ID-based elements (osłona-niebieska and danger)
                result = Regex.Replace(result, @"(<[^>]*id=""(osłona-niebieska|danger)""[^>]*?)\s*(/?>)", "$1 display=\"none\"$3");

                // Hide the text "blok rozdzielczy" (finding text tag that contains the string somewhere in its body)
                result = Regex.Replace(result, @"(<text[^>]*?)\s*(?=>[\s\S]*?lok rozdzielczy[\s\S]*?<\/text>)", "$1 display=\"none\"");
            }
        }

        return result;
    }

    public (string NormalizedSvg, double Width, double Height) NormalizeSvgAndCalculateSize(string svgContent, string? filePath)
    {
        string result = svgContent;
        double width = 232.58; // Default 1P (approx 17.5mm)
        double height = 1103.0; // Default height
        const double SCALE_FACTOR = 232.58 / 212.0;

        try
        {
            bool isDist = filePath != null && filePath.Contains("blok rozdzielczy", StringComparison.OrdinalIgnoreCase);

            double translateX = 0;
            double translateY = 0;
            bool hasOuterTranslate = false;

            var outerTranslateMatch = Regex.Match(svgContent, @"<svg[^>]*>\s*<g[^>]*?\btransform\s*=\s*""\s*translate\(\s*([\d\.-]+)\s*(?:,|\s)\s*([\d\.-]+)\s*\)\s*""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!outerTranslateMatch.Success)
                outerTranslateMatch = Regex.Match(svgContent, @"<svg[^>]*>\s*<g[^>]*?\btransform\s*=\s*'\s*translate\(\s*([\d\.-]+)\s*(?:,|\s)\s*([\d\.-]+)\s*\)\s*'", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (outerTranslateMatch.Success)
            {
                translateX = double.Parse(outerTranslateMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                translateY = double.Parse(outerTranslateMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                hasOuterTranslate = true;
            }

            // 1. Detect body rectangle (the largest visible element)
            var rectMatches = Regex.Matches(svgContent, @"<rect\s+[^>]*>", RegexOptions.IgnoreCase);
            double maxArea = 0;
            double bx = 0, by = 0, bw = 0, bh = 0;
            bool found = false;

            foreach (Match m in rectMatches)
            {
                string tag = m.Value;
                // Ignore invisible containers
                if (tag.Contains("fill:none", StringComparison.OrdinalIgnoreCase) || tag.Contains("id=\"Page", StringComparison.OrdinalIgnoreCase))
                    continue;

                var wM = Regex.Match(tag, @"\bwidth\s*=\s*""([\d\.-]+)""", RegexOptions.IgnoreCase);
                var hM = Regex.Match(tag, @"\bheight\s*=\s*""([\d\.-]+)""", RegexOptions.IgnoreCase);
                if (wM.Success && hM.Success)
                {
                    double w = double.Parse(wM.Groups[1].Value, CultureInfo.InvariantCulture);
                    double h = double.Parse(hM.Groups[1].Value, CultureInfo.InvariantCulture);

                    if (w * h > maxArea && w > 10 && h > 10)
                    {
                        var xM = Regex.Match(tag, @"\bx\s*=\s*""([\d\.-]+)""", RegexOptions.IgnoreCase);
                        var yM = Regex.Match(tag, @"\by\s*=\s*""([\d\.-]+)""", RegexOptions.IgnoreCase);
                        bx = xM.Success ? double.Parse(xM.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                        by = yM.Success ? double.Parse(yM.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                        bw = w; bh = h;
                        maxArea = w * h;
                        found = true;
                    }
                }
            }

            // 2. Normalization (Cropping to body)
            // Safety check: only crop if the detected coordinates are relatively close to original origin.
            // MCB/RCD often have huge absolute coordinates (e.g. 1650) because of inner translate() groups.
            // If we crop to those values without accounting for translate, the content disappears.
            var cropX = bx;
            var cropY = by;
            if (hasOuterTranslate)
            {
                cropX = bx + translateX;
                cropY = by + translateY;
            }

            if (found && cropX < 500 && cropY < 500)
            {
                string newViewBox = $"viewBox=\"{cropX.ToString(CultureInfo.InvariantCulture)} {cropY.ToString(CultureInfo.InvariantCulture)} {bw.ToString(CultureInfo.InvariantCulture)} {bh.ToString(CultureInfo.InvariantCulture)}\"";

                // Surgical update of ONLY the first <svg ...> tag to avoid XML damage
                var svgTagMatch = Regex.Match(result, @"<svg[^>]*?>", RegexOptions.IgnoreCase);
                if (svgTagMatch.Success)
                {
                    string oldTag = svgTagMatch.Value;
                    string newTag;

                    if (Regex.IsMatch(oldTag, @"\bviewBox\s*=", RegexOptions.IgnoreCase))
                    {
                        // Replace existing viewBox (handling both " and ' )
                        newTag = Regex.Replace(oldTag, @"\bviewBox\s*=\s*""[^""]*""", newViewBox, RegexOptions.IgnoreCase);
                        newTag = Regex.Replace(newTag, @"\bviewBox\s*=\s*'[^']*'", newViewBox, RegexOptions.IgnoreCase);
                    }
                    else
                    {
                        // Insert new viewBox before the closing >
                        newTag = oldTag.Insert(oldTag.Length - 1, " " + newViewBox + " ");
                    }

                    result = result.Remove(svgTagMatch.Index, svgTagMatch.Length).Insert(svgTagMatch.Index, newTag);
                }

                width = bw * SCALE_FACTOR;
                height = bh * SCALE_FACTOR;
            }
            else
            {
                // Fallback: extract from original viewBox
                var vbMatch = Regex.Match(svgContent, @"viewBox\s*=\s*""\s*([\d\.-]+)\s+([\d\.-]+)\s+([\d\.-]+)\s+([\d\.-]+)""", RegexOptions.IgnoreCase);
                if (!vbMatch.Success)
                    vbMatch = Regex.Match(svgContent, @"viewBox\s*=\s*'\s*([\d\.-]+)\s+([\d\.-]+)\s+([\d\.-]+)\s+([\d\.-]+)'", RegexOptions.IgnoreCase);

                if (vbMatch.Success)
                {
                    double vbW = double.Parse(vbMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                    double vbH = double.Parse(vbMatch.Groups[4].Value, CultureInfo.InvariantCulture);
                    width = vbW * SCALE_FACTOR;
                    height = vbH * SCALE_FACTOR;
                }
            }

            // 3. Forced dimensions for distribution block
            if (isDist)
            {
                width = 101.0 * 13.29;
                height = 88.0 * 13.29;
            }
        }
        catch (Exception)
        {
            // Error loading symbol
        }

        return (result, width, height);
    }

    public void RefreshSymbolVisual(Models.SymbolItem symbol)
    {
        if (symbol == null || string.IsNullOrEmpty(symbol.VisualPath) || !System.IO.File.Exists(symbol.VisualPath))
            return;

        try
        {
            string content = System.IO.File.ReadAllText(symbol.VisualPath);
            content = ApplyParametersToSvg(content, symbol.Parameters);

            var normResult = NormalizeSvgAndCalculateSize(content, symbol.VisualPath);
            symbol.Width = normResult.Width;
            symbol.Height = normResult.Height;
            content = normResult.NormalizedSvg;

            var svgSource = global::Avalonia.Svg.Skia.SvgSource.LoadFromSvg(content);
            if (svgSource != null)
            {
                symbol.Visual = new global::Avalonia.Svg.Skia.SvgImage { Source = svgSource };
            }
        }
        catch (Exception)
        {
            // Error loading symbol
        }
    }

    // Context Menu Handler (to be wired up in UI or via command)
    public async void EditSymbolParameters(Models.SymbolItem symbol)
    {
        if (symbol == null || symbol.Parameters.Count == 0) return;

        var dialog = new Dialogs.ModuleParametersDialog(symbol.Parameters);
        var result = await dialog.ShowDialog<bool?>(this);

        if (result == true && dialog.Result != null)
        {
            // Update Parameters
            symbol.Parameters = dialog.Result;

            // Regenerate Visual
            RefreshSymbolVisual(symbol);
        }
    }

    // ===== MARQUEE SELECTION =====

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_zoomContainer == null || _selectionRectangle == null) return;

        // If something else handled this (e.g. a SymbolControl), we don't want to clear selection
        // unless Ctrl is held (which is handled by SymbolControl logic anyway).
        // However, we might want to start marquee selection if we clicked on a symbol? No.
        if (e.Handled) return;

        var point = e.GetCurrentPoint(_zoomContainer);

        // Only start selection on left button
        if (point.Properties.IsLeftButtonPressed)
        {
            // Clear previous selection if not holding Ctrl
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                foreach (var symbol in ViewModel.Symbols)
                {
                    symbol.IsSelected = false;
                }
            }

            _isSelecting = true;
            _selectionStartPoint = point.Position;

            // Initialize selection rectangle
            Canvas.SetLeft(_selectionRectangle, _selectionStartPoint.X);
            Canvas.SetTop(_selectionRectangle, _selectionStartPoint.Y);
            _selectionRectangle.Width = 0;
            _selectionRectangle.Height = 0;
            _selectionRectangle.IsVisible = true;

            e.Handled = true;
        }
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSelecting || _zoomContainer == null || _selectionRectangle == null) return;

        var currentPoint = e.GetPosition(_zoomContainer);

        // Calculate selection rectangle bounds
        var left = Math.Min(_selectionStartPoint.X, currentPoint.X);
        var top = Math.Min(_selectionStartPoint.Y, currentPoint.Y);
        var width = Math.Abs(currentPoint.X - _selectionStartPoint.X);
        var height = Math.Abs(currentPoint.Y - _selectionStartPoint.Y);

        // Update selection rectangle
        Canvas.SetLeft(_selectionRectangle, left);
        Canvas.SetTop(_selectionRectangle, top);
        _selectionRectangle.Width = width;
        _selectionRectangle.Height = height;

        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSelecting || _zoomContainer == null || _selectionRectangle == null) return;

        var currentPoint = e.GetPosition(_zoomContainer);

        // Calculate final selection rectangle in screen coordinates
        var selLeft = Math.Min(_selectionStartPoint.X, currentPoint.X);
        var selTop = Math.Min(_selectionStartPoint.Y, currentPoint.Y);
        var selRight = Math.Max(_selectionStartPoint.X, currentPoint.X);
        var selBottom = Math.Max(_selectionStartPoint.Y, currentPoint.Y);

        var selectionRect = new Rect(selLeft, selTop, selRight - selLeft, selBottom - selTop);

        // Select modules that intersect with selection rectangle
        foreach (var symbol in ViewModel.Symbols)
        {
            // Direct coordinates
            var moduleScreenPos = new Point(symbol.X, symbol.Y);
            var moduleRect = new Rect(
                moduleScreenPos.X - symbol.Width / 2,
                moduleScreenPos.Y - symbol.Height / 2,
                symbol.Width,
                symbol.Height
            );

            // Check intersection
            if (selectionRect.Intersects(moduleRect))
            {
                symbol.IsSelected = true;
            }
        }

        // Hide selection rectangle
        _selectionRectangle.IsVisible = false;
        _isSelecting = false;

        e.Handled = true;
    }

    private void CanvasContainer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.StatusMessage = $"Obszar roboczy: {e.NewSize.Width:F0}x{e.NewSize.Height:F0}";
        }
    }

    private async void BtnDinRail_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Simple input dialog
        var rows = 1;
        var modules = 24;

        try
        {
            var generator = new Services.DinRailGeneratorV2();
            string svg = generator.Generate(rows, modules);
            var (width, height) = generator.GetDimensions(rows, modules);

            var dinRailDisplay = this.FindControl<Controls.DinRailView>("DinRailDisplay");
            if (dinRailDisplay != null)
            {
                dinRailDisplay.SetRail(svg, width, height);
                ViewModel.IsDinRailVisible = true;
                ViewModel.StatusMessage = $"Szyna DIN wygenerowana: {rows}x{modules}";
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Błąd: {ex.Message}";
        }

        await Task.CompletedTask;
    }
}
