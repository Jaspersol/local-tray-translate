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

    public ResultPopupWindow(string query, IReadOnlyList<DictionaryEntry> results, DictionaryService dictionary)
    {
        InitializeComponent();
        _dictionary = dictionary;
        QueryBox.Text = query;
        RenderResults(results);
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
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var result in results)
        {
            var container = new Border
            {
                BorderBrush = ThemeBrush("BrushBorder"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 12, 0, 12)
            };

            var headword = string.IsNullOrWhiteSpace(result.Phonetic)
                ? result.Word
                : $"{result.Word}  [{result.Phonetic}]";
            var stack = new StackPanel();
            stack.Children.Add(ReadOnlyText(
                headword,
                fontSize: 18,
                fontWeight: FontWeights.SemiBold,
                foreground: ThemeBrush("BrushTextPrimary"),
                topMargin: 0,
                italic: false));
            stack.Children.Add(ReadOnlyText(
                result.Translation,
                fontSize: 15,
                fontWeight: null,
                foreground: ThemeBrush("BrushTextPrimary"),
                topMargin: 8,
                italic: false));
            stack.Children.Add(ReadOnlyText(
                result.Definition,
                fontSize: 13,
                fontWeight: null,
                foreground: ThemeBrush("BrushTextSecondary"),
                topMargin: 4,
                italic: false));

            if (!string.IsNullOrWhiteSpace(result.Example))
            {
                stack.Children.Add(ReadOnlyText(
                    result.Example,
                    fontSize: 13,
                    fontWeight: null,
                    foreground: ThemeBrush("BrushTextMuted"),
                    topMargin: 6,
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
            VerticalAlignment = VerticalAlignment.Top
        };
        return box;
    }
}
