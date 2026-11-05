using System.Windows;
using System.Windows.Input;
using TranslateLite.Services;

namespace TranslateLite.Windows;

/// <summary>主查询窗口：输入词条查内置词典与用户词典（关闭时只隐藏，常驻托盘）。</summary>
public partial class MainWindow : Window
{
    private readonly DictionaryService _dictionary;

    public MainWindow(DictionaryService dictionary)
    {
        InitializeComponent();
        _dictionary = dictionary;
    }

    private async void OnLookupClicked(object sender, RoutedEventArgs e) => await LookupAsync();

    // KeyEventArgs 保留全限定名：本项目 UseWindowsForms + ImplicitUsings 会把 System.Windows.Forms
    // 一并放进全局 using，短名与 Forms.KeyEventArgs 冲突（CS0104）。其余文件同类写法同理。
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
            ResultsList.ItemsSource = null;
            return;
        }

        ResultsList.ItemsSource = await _dictionary.LookupAsync(query);
    }

    private void OnUserDictionaryClick(object sender, RoutedEventArgs e)
    {
        new UserDictionaryWindow(_dictionary) { Owner = this }.ShowDialog();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
