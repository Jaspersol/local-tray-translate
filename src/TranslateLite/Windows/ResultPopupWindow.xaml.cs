using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TranslateLite.Models;
using TranslateLite.Services;

namespace TranslateLite.Windows;

public partial class ResultPopupWindow : Window
{
    private readonly DictionaryService _dictionary;

    public ResultPopupWindow(DictionaryService dictionary)
    {
        InitializeComponent();
        _dictionary = dictionary;
    }

    /// <summary>复用同一窗口展示一次新的查询结果：刷新内容、回到顶部并带到前台。</summary>
    public void ShowResult(string query, IReadOnlyList<DictionaryEntry> results)
    {
        QueryBox.Text = query;
        RenderResults(results);
        ResultScroll.ScrollToTop();
        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    // 与主窗口一致：关闭只是隐藏，窗口对象留给下一次查询复用。
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private async void OnQueryClicked(object sender, RoutedEventArgs e) => await LookupAsync();

    private async void OnQueryKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await LookupAsync();
        }
    }

    private async Task LookupAsync()
    {
        var query = QueryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var results = await _dictionary.LookupAsync(query);
        RenderResults(results);
    }

    private void RenderResults(IReadOnlyList<DictionaryEntry> results)
    {
        ResultPanel.Children.Clear();
        if (results.Count == 0)
        {
            ResultPanel.Children.Add(new TextBlock
            {
                Text = "没有找到词典条目。",
                Foreground = ThemeBrush("BrushTextMuted"),
                FontSize = 13.5,
                Margin = new Thickness(4, 8, 4, 8),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var result in results)
        {
            var container = new Border
            {
                BorderBrush = ThemeBrush("BrushDivider"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 16, 0, 16)
            };

            var headword = string.IsNullOrWhiteSpace(result.Phonetic)
                ? result.Word
                : $"{result.Word}  [{result.Phonetic}]";
            var stack = new StackPanel();
            stack.Children.Add(ReadOnlyText(
                headword,
                fontSize: 19,
                fontWeight: FontWeights.SemiBold,
                foreground: ThemeBrush("BrushTextPrimary"),
                topMargin: 0,
                italic: false));
            stack.Children.Add(ReadOnlyText(
                result.Translation,
                fontSize: 15,
                fontWeight: null,
                foreground: ThemeBrush("BrushTextPrimary"),
                topMargin: 10,
                italic: false));
            stack.Children.Add(ReadOnlyText(
                result.Definition,
                fontSize: 13,
                fontWeight: null,
                foreground: ThemeBrush("BrushTextSecondary"),
                topMargin: 6,
                italic: false));

            if (!string.IsNullOrWhiteSpace(result.Example))
            {
                stack.Children.Add(ReadOnlyText(
                    result.Example,
                    fontSize: 13,
                    fontWeight: null,
                    foreground: ThemeBrush("BrushTextMuted"),
                    topMargin: 8,
                    italic: true));
            }

            container.Child = stack;
            ResultPanel.Children.Add(container);
        }
    }

    /// <summary>取主题画刷，保证弹窗样式与应用其余界面一致。</summary>
    private static System.Windows.Media.Brush ThemeBrush(string key) =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(key);

    /// <summary>创建一个只读、可换行的文本块（WPF 的 TextBlock 不可选中文本，故这里用 TextBox）。</summary>
    private static System.Windows.Controls.TextBox ReadOnlyText(
        string text,
        double fontSize,
        FontWeight? fontWeight,
        System.Windows.Media.Brush foreground,
        double topMargin,
        bool italic)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            Foreground = foreground,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            FontWeight = fontWeight ?? FontWeights.Normal,
            Margin = new Thickness(0, topMargin, 0, 0),
            Padding = new Thickness(0),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Top
        };
        return box;
    }
}
