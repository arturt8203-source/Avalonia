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
using System.Threading.Tasks;

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

    // Skala szyny DIN - stosowana do importowanych modułów
    private double _dinRailScale = 0.20;

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
                            var importService = new Services.SymbolImportService();
                            var (image, width, height) = importService.CreateSvgPreview(moduleFilePath);
                            if (image != null)
                            {
                                previewImage.Source = image;
                                // Zastosuj skalę szyny DIN do podglądu
                                previewImage.Width = width * _dinRailScale;
                                previewImage.Height = height * _dinRailScale;
                            }
                        }
                        else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                        {
                            using var fs = System.IO.File.OpenRead(moduleFilePath);
                            var bitmap = new global::Avalonia.Media.Imaging.Bitmap(fs);
                            previewImage.Source = bitmap;
                            // Zastosuj skalę szyny DIN do podglądu
                            previewImage.Width = bitmap.Size.Width * _dinRailScale;
                            previewImage.Height = bitmap.Size.Height * _dinRailScale;
                        }

                        previewBorder.IsVisible = true;
                    }
                }
                catch (Exception ex)
                {
                    Services.AppLog.Warn($"Błąd tworzenia podglądu: {moduleFilePath}", ex);
                }
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
                double finalX = pos.X - previewBorder.Bounds.Width / 2;
                double finalY = pos.Y - previewBorder.Bounds.Height / 2;

                // --- Magnetic Snap (Preview) ---
                if (ViewModel.DinRailAxes.Count > 0)
                {
                    double moduleCenterY = pos.Y;
                    double closestAxis = ViewModel.DinRailAxes[0];
                    double minDistance = Math.Abs(moduleCenterY - closestAxis);

                    foreach (var axis in ViewModel.DinRailAxes)
                    {
                        double d = Math.Abs(moduleCenterY - axis);
                        if (d < minDistance)
                        {
                            minDistance = d;
                            closestAxis = axis;
                        }
                    }

                    // Snap threshold matches OnCanvasDrop (80px)
                    if (minDistance < 80.0)
                    {
                        finalY = closestAxis - (previewBorder.Bounds.Height / 2.0);
                    }
                }

                // Set Position (centered or snapped)
                Canvas.SetLeft(previewBorder, finalX);
                Canvas.SetTop(previewBorder, finalY);
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
            if (schematicGrid != null && !string.IsNullOrEmpty(moduleFilePath))
            {
                try
                {
                    // Get drop position directly relative to the grid (World Coords)
                    var dropPos = e.GetPosition(schematicGrid);

                    // Use SymbolImportService for import
                    var importService = new Services.SymbolImportService();
                    var newSymbol = importService.ImportFromFile(moduleFilePath, moduleType, moduleName);

                    if (newSymbol != null)
                    {
                        // Skaluj moduł proporcjonalnie do szyny DIN
                        newSymbol.Width *= _dinRailScale;
                        newSymbol.Height *= _dinRailScale;

                        // Wycentruj moduł względem kursora (ale pozwól na przyciąganie Y)
                        double finalX = dropPos.X - newSymbol.Width / 2.0;
                        double finalY = dropPos.Y - newSymbol.Height / 2.0;

                        // --- MAGNETIC SNAP LOGIC (UPUSZCZANIE) ---
                        if (ViewModel.DinRailAxes.Count > 0)
                        {
                            double moduleCenterY = dropPos.Y; // Kursor jest mniej więcej na środku
                            double closestAxis = ViewModel.DinRailAxes[0];
                            double minDistance = Math.Abs(moduleCenterY - closestAxis);

                            foreach (var axis in ViewModel.DinRailAxes)
                            {
                                double d = Math.Abs(moduleCenterY - axis);
                                if (d < minDistance)
                                {
                                    minDistance = d;
                                    closestAxis = axis;
                                }
                            }

                            // Próg przyciągania przy upuszczaniu (np. 80px)
                            if (minDistance < 80.0)
                            {
                                // Ustaw środek modułu na osi
                                finalY = closestAxis - (newSymbol.Height / 2.0);
                                newSymbol.IsSnappedToRail = true;
                                ViewModel.StatusMessage = $"Dodano: {moduleName} (SNAP)";
                            }
                            else
                            {
                                newSymbol.IsSnappedToRail = false;
                            }
                        }

                        newSymbol.X = finalX;
                        newSymbol.Y = finalY;

                        ViewModel.Symbols.Add(newSymbol);
                        ViewModel.StatusMessage = $"Dodano: {moduleName} (skala: {_dinRailScale:P0})";
                    }
                    else
                    {
                        ViewModel.StatusMessage = $"Błąd importu: {moduleName}";
                    }
                }
                catch (Exception ex)
                {
                    Services.AppLog.Error($"Błąd drop symbolu: {moduleFilePath}", ex);
                    ViewModel.StatusMessage = $"Błąd: {ex.Message}";
                }
            }
        }
    }

    public void RefreshSymbolVisual(Models.SymbolItem symbol)
    {
        var importService = new Services.SymbolImportService();
        importService.RefreshVisual(symbol);
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

    // ===== MARQUEE SELECTION (REWRITTEN) =====

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_zoomContainer == null || _selectionRectangle == null) return;

        // Check if event was already handled (e.g. by clicking on a symbol)
        if (e.Handled) return;

        var point = e.GetCurrentPoint(_zoomContainer);

        if (!point.Properties.IsLeftButtonPressed) return;

        // 1. Handle Selection State based on Modifiers
        bool isModifierHeld = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (!isModifierHeld)
        {
            // Clear existing selection if no modifier is held
            foreach (var s in ViewModel.Symbols)
            {
                s.IsSelected = false;
            }
        }

        // 2. Start Marquee Selection
        _isSelecting = true;
        _selectionStartPoint = point.Position;

        // Reset and show rectangle
        Canvas.SetLeft(_selectionRectangle, _selectionStartPoint.X);
        Canvas.SetTop(_selectionRectangle, _selectionStartPoint.Y);
        _selectionRectangle.Width = 0;
        _selectionRectangle.Height = 0;
        _selectionRectangle.IsVisible = true;

        e.Pointer.Capture(_canvasContainer);
        e.Handled = true;
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSelecting || _zoomContainer == null || _selectionRectangle == null) return;

        var currentPoint = e.GetPosition(_zoomContainer);

        // Calculate geometry
        var x = Math.Min(_selectionStartPoint.X, currentPoint.X);
        var y = Math.Min(_selectionStartPoint.Y, currentPoint.Y);
        var w = Math.Abs(currentPoint.X - _selectionStartPoint.X);
        var h = Math.Abs(currentPoint.Y - _selectionStartPoint.Y);

        // Update Visuals
        Canvas.SetLeft(_selectionRectangle, x);
        Canvas.SetTop(_selectionRectangle, y);
        _selectionRectangle.Width = w;
        _selectionRectangle.Height = h;

        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSelecting || _zoomContainer == null || _selectionRectangle == null) return;

        // Finalize Selection
        var currentPoint = e.GetPosition(_zoomContainer);
        var rectX = Math.Min(_selectionStartPoint.X, currentPoint.X);
        var rectY = Math.Min(_selectionStartPoint.Y, currentPoint.Y);
        var rectW = Math.Abs(currentPoint.X - _selectionStartPoint.X);
        var rectH = Math.Abs(currentPoint.Y - _selectionStartPoint.Y);

        var selectionRect = new Rect(rectX, rectY, rectW, rectH);

        // Select intersecting items
        foreach (var symbol in ViewModel.Symbols)
        {
            // Symbol bounds (centered at X, Y)
            var symbolRect = new Rect(
                symbol.X - symbol.Width / 2.0,
                symbol.Y - symbol.Height / 2.0,
                symbol.Width,
                symbol.Height
            );

            if (selectionRect.Intersects(symbolRect))
            {
                symbol.IsSelected = true;
            }
        }

        // Cleanup
        _selectionRectangle.IsVisible = false;
        _isSelecting = false;
        e.Pointer.Capture(null);
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
        // Pokaż dialog konfiguracji szyny DIN
        var dialog = new Dialogs.DinRailDialog();
        await dialog.ShowDialog(this);

        if (!dialog.Confirmed)
            return;

        var rows = dialog.Rows;
        var modules = dialog.Modules;

        try
        {
            // Używamy generatora proceduralnego - zachowuje proporcje elementów
            var generator = new Services.DinRailGeneratorProcedural();
            string svg = generator.Generate(rows, modules);
            var (width, height) = generator.GetDimensions(rows, modules);

            var dinRailDisplay = this.FindControl<Controls.DinRailView>("DinRailDisplay");
            if (dinRailDisplay != null && _canvasContainer != null)
            {
                // Pobierz wymiary widocznego obszaru (CanvasContainer)
                double visibleWidth = _canvasContainer.Bounds.Width;
                double visibleHeight = _canvasContainer.Bounds.Height;

                // Dodaj margines bezpieczeństwa (50px z każdej strony)
                const double margin = 50.0;
                double availableWidth = visibleWidth - (2 * margin);
                double availableHeight = visibleHeight - (2 * margin);

                // Skalowanie proporcjonalne aby zmieścić całą szynę w widocznym obszarze
                double scaleX = availableWidth / width;
                double scaleY = availableHeight / height;
                double scale = Math.Min(scaleX, scaleY);

                // Nie powiększaj ponad 0.25 (żeby nie było za duże dla małych szyn)
                scale = Math.Min(scale, 0.25);

                // Zapisz skalę dla modułów
                _dinRailScale = scale;

                double scaledWidth = width * scale;
                double scaledHeight = height * scale;

                // Ustaw rozmiar szyny
                dinRailDisplay.SetRail(svg, scaledWidth, scaledHeight);

                // Wycentruj szynę w widocznym obszarze
                double centerX = (visibleWidth - scaledWidth) / 2;
                double centerY = (visibleHeight - scaledHeight) / 2;

                Canvas.SetLeft(dinRailDisplay, centerX);
                Canvas.SetTop(dinRailDisplay, centerY);

                Services.AppLog.Info($"DIN Rail: {width}x{height} -> scaled: {scaledWidth:F0}x{scaledHeight:F0}, centered at ({centerX:F0}, {centerY:F0})");
                ViewModel.IsDinRailVisible = true;
                ViewModel.StatusMessage = $"Szyna DIN: {rows}x{modules} ({scaledWidth:F0}x{scaledHeight:F0})";

                // --- OBLICZANIE OSI PRZYCIĄGANIA (MAGNETIC SNAP) ---
                ViewModel.DinRailAxes.Clear();
                var rawCenters = generator.GetRowCenters(rows);
                foreach (var rawCenter in rawCenters)
                {
                    // Globalna współrzędna Y = (LokalnaY * Skala) + PrzesunięcieTop
                    double globalY = (rawCenter * scale) + centerY;
                    ViewModel.DinRailAxes.Add(globalY);
                }
                Services.AppLog.Info($"Wygenerowano {ViewModel.DinRailAxes.Count} osi przyciągania.");
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Błąd: {ex.Message}";
        }
    }


}
