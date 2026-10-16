using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingSize = System.Drawing.Size;
using WpfPoint = System.Windows.Point;

namespace TranslateLite.Windows;

public partial class CaptureOverlayWindow : Window
{
    private WpfPoint? _startPoint;
    private DrawingBitmap? _capturedBitmap;

    public CaptureOverlayWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public DrawingBitmap? CaptureRegion()
    {
        ShowDialog();
        return _capturedBitmap;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(RootCanvas);
        Canvas.SetLeft(SelectionBorder, _startPoint.Value.X);
        Canvas.SetTop(SelectionBorder, _startPoint.Value.Y);
        SelectionBorder.Width = 0;
        SelectionBorder.Height = 0;
        SelectionBorder.Visibility = Visibility.Visible;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_startPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(RootCanvas);
        UpdateSelection(_startPoint.Value, current);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_startPoint is null)
        {
            Close();
            return;
        }

        var endPoint = e.GetPosition(RootCanvas);
        var screenStart = PointToScreen(_startPoint.Value);
        var screenEnd = PointToScreen(endPoint);
        var left = (int)Math.Round(Math.Min(screenStart.X, screenEnd.X));
        var top = (int)Math.Round(Math.Min(screenStart.Y, screenEnd.Y));
        var width = (int)Math.Round(Math.Abs(screenStart.X - screenEnd.X));
        var height = (int)Math.Round(Math.Abs(screenStart.Y - screenEnd.Y));

        Hide();
        if (width >= 4 && height >= 4)
        {
            Thread.Sleep(80);
            _capturedBitmap = new DrawingBitmap(width, height);
            using var graphics = DrawingGraphics.FromImage(_capturedBitmap);
            graphics.CopyFromScreen(left, top, 0, 0, new DrawingSize(width, height));
        }

        Close();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
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

        Canvas.SetLeft(SelectionBorder, left);
        Canvas.SetTop(SelectionBorder, top);
        SelectionBorder.Width = width;
        SelectionBorder.Height = height;
    }
}
