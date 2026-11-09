using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using WpfPoint = System.Windows.Point;

namespace TranslateLite.Windows;

/// <summary>
/// 全屏截图遮罩：进入时冻结一帧屏幕作为不透明窗口的背景，拖拽选区后从这一帧裁剪位图。
/// Esc 或空白处点击取消。
/// </summary>
public partial class CaptureOverlayWindow : Window
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    // 虚拟屏的物理像素矩形：GetSystemMetrics / GetCursorPos / CopyFromScreen 处在同一坐标系，
    // 不依赖进程的 DPI awareness，也不再把 PointToScreen 的 DIP 当物理像素用。
    private readonly int _virtualLeft;
    private readonly int _virtualTop;
    private readonly int _virtualWidth;
    private readonly int _virtualHeight;

    private readonly RectangleGeometry _fullGeometry = new();
    private readonly RectangleGeometry _selectionGeometry = new();

    private DrawingBitmap? _screenBitmap;
    private DrawingBitmap? _capturedBitmap;

    private WpfPoint? _startPoint;
    private WpfPoint _currentPoint;
    private bool _selectionDirty;
    private POINT? _startCursor;

    public CaptureOverlayWindow()
    {
        InitializeComponent();

        _virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        _virtualTop = GetSystemMetrics(SmYVirtualScreen);
        _virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
        _virtualHeight = GetSystemMetrics(SmCyVirtualScreen);

        // 窗口用 DIP 铺满虚拟屏；冻结帧（物理像素）由 Image 拉伸到窗口 DIP 尺寸。
        // 显示坐标取 e.GetPosition（DIP），裁剪坐标取 GetCursorPos（物理像素），
        // 二者描述同一块屏幕区域，不需要互相乘除缩放系数。
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        ScreenImage.Width = Width;
        ScreenImage.Height = Height;
        Canvas.SetLeft(ScreenImage, 0);
        Canvas.SetTop(ScreenImage, 0);

        _fullGeometry.Rect = new Rect(0, 0, Width, Height);
        DimMask.Data = new CombinedGeometry(GeometryCombineMode.Exclude, _fullGeometry, _selectionGeometry);

        FreezeScreen();

        // 拖拽期间按每渲染帧合并一次选区更新，避免每个 MouseMove 都触发一次 WPF 布局。
        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            ScreenImage.Source = null;
            _screenBitmap?.Dispose();
            _screenBitmap = null;
        };
    }

    /// <summary>冻结一帧全屏位图，作为不透明窗口的背景与后续裁剪来源。</summary>
    private void FreezeScreen()
    {
        _screenBitmap = new DrawingBitmap(_virtualWidth, _virtualHeight);
        using (var graphics = DrawingGraphics.FromImage(_screenBitmap))
        {
            graphics.CopyFromScreen(_virtualLeft, _virtualTop, 0, 0,
                new DrawingSize(_virtualWidth, _virtualHeight));
        }

        // CreateBitmapSourceFromHBitmap 会复制像素，拿到 BitmapSource 后即可释放 HBITMAP。
        var hBitmap = _screenBitmap.GetHbitmap();
        try
        {
            ScreenImage.Source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    /// <summary>显示遮罩并返回选区位图；窗口关闭时冻结帧已释放，裁剪结果交给调用方。</summary>
    public DrawingBitmap? CaptureRegion()
    {
        ShowDialog();
        return _capturedBitmap;
    }

    /// <summary>从冻结帧裁剪。screenRect 是 Win32 屏幕物理坐标，需减去虚拟屏原点换算到位图坐标。</summary>
    private DrawingBitmap? Crop(DrawingRectangle screenRect)
    {
        if (_screenBitmap is null)
        {
            return null;
        }

        var x = Math.Clamp(screenRect.Left - _virtualLeft, 0, Math.Max(0, _virtualWidth - 1));
        var y = Math.Clamp(screenRect.Top - _virtualTop, 0, Math.Max(0, _virtualHeight - 1));
        var width = Math.Clamp(screenRect.Width, 1, _virtualWidth - x);
        var height = Math.Clamp(screenRect.Height, 1, _virtualHeight - y);
        return _screenBitmap.Clone(new DrawingRectangle(x, y, width, height), _screenBitmap.PixelFormat);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_selectionDirty || _startPoint is null)
        {
            return;
        }

        _selectionDirty = false;
        UpdateSelection(_startPoint.Value, _currentPoint);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(RootCanvas);
        _currentPoint = _startPoint.Value;
        _startCursor = GetCursorPosition();
        SelectionBorder.Visibility = Visibility.Visible;
        _selectionDirty = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_startPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // 只记录坐标，实际布局更新合并到渲染帧（OnRendering）。
        _currentPoint = e.GetPosition(RootCanvas);
        _selectionDirty = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_startPoint is null || _startCursor is null)
        {
            Close();
            return;
        }

        // 物理像素坐标直接来自鼠标，与 CopyFromScreen 同源，缩放后也不会错位。
        var end = GetCursorPosition();
        var left = Math.Min(_startCursor.Value.X, end.X);
        var top = Math.Min(_startCursor.Value.Y, end.Y);
        var width = Math.Abs(_startCursor.Value.X - end.X);
        var height = Math.Abs(_startCursor.Value.Y - end.Y);
        if (width >= 4 && height >= 4)
        {
            _capturedBitmap = Crop(new DrawingRectangle(left, top, width, height));
        }

        // 冻结帧在进入时已抓完，窗口关闭后不必等 DWM 合成，也不用再 CopyFromScreen。
        Close();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _capturedBitmap?.Dispose();
            _capturedBitmap = null;
            Close();
        }
    }

    private void UpdateSelection(WpfPoint start, WpfPoint current)
    {
        var left = Math.Min(start.X, current.X);
        var top = Math.Min(start.Y, current.Y);
        var width = Math.Abs(start.X - current.X);
        var height = Math.Abs(start.Y - current.Y);

        // 挖洞遮罩：全窗口矩形减去选区矩形，选区内显示亮的原图，选区外压暗。
        _selectionGeometry.Rect = new Rect(left, top, width, height);
        Canvas.SetLeft(SelectionBorder, left);
        Canvas.SetTop(SelectionBorder, top);
        SelectionBorder.Width = width;
        SelectionBorder.Height = height;
    }

    private static POINT GetCursorPosition()
    {
        GetCursorPos(out var point);
        return point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
