using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using TranslateLite.Models;
using TranslateLite.Services;

namespace TranslateLite.Windows;

public partial class UserDictionaryWindow : Window
{
    private readonly DictionaryService _dictionary;
    private readonly ObservableCollection<UserEntry> _entries = [];

    public UserDictionaryWindow(DictionaryService dictionary)
    {
        InitializeComponent();
        _dictionary = dictionary;
        EntriesList.ItemsSource = _entries;
        _ = ReloadAsync();
    }

    private async Task ReloadAsync(string? filter = null)
    {
        var entries = await _dictionary.ListUserEntriesAsync(filter);
        var selectedWord = (EntriesList.SelectedItem as UserEntry)?.Word;
        _entries.Clear();
        foreach (var entry in entries)
        {
            _entries.Add(entry);
        }

        if (selectedWord is not null)
        {
            EntriesList.SelectedItem = _entries.FirstOrDefault(item => item.Word == selectedWord);
        }

        StatusBar.Text = $"共 {_entries.Count} 条用户词条。";
    }

    private async void OnFilterKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ReloadAsync(FilterBox.Text);
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var editor = new UserEntryEditorWindow();
        if (editor.ShowDialog() == true && editor.Result is not null)
        {
            var entry = editor.Result;
            await _dictionary.AddUserEntryAsync(entry.Word, entry.Language, entry.Phonetic,
                entry.Definition, entry.Translation, entry.Example, null, entry.Tags);
            await ReloadAsync(string.IsNullOrWhiteSpace(FilterBox.Text) ? null : FilterBox.Text);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not UserEntry entry)
        {
            StatusBar.Text = "请先选中要删除的词条。";
            return;
        }

        await _dictionary.DeleteUserEntryAsync(entry.Id);
        await ReloadAsync(string.IsNullOrWhiteSpace(FilterBox.Text) ? null : FilterBox.Text);
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入用户词条",
            Filter = "文本文件 (*.txt;*.tsv;*.csv)|*.txt;*.tsv;*.csv|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _dictionary.ImportUserEntriesAsync(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            System.Windows.MessageBox.Show(this, exception.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ReloadAsync(string.IsNullOrWhiteSpace(FilterBox.Text) ? null : FilterBox.Text);
    }

    private async void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EntriesList.SelectedItem is not UserEntry entry)
        {
            return;
        }

        var editor = new UserEntryEditorWindow(
            entry.Word, entry.Language, entry.Phonetic, entry.Definition,
            entry.Translation, entry.Example, ParseTags(entry.TagsJson));
        if (editor.ShowDialog() != true || editor.Result is null)
        {
            return;
        }

        var updated = editor.Result;
        var languageChanged = !string.Equals(updated.Language, entry.Language, StringComparison.Ordinal);
        await _dictionary.UpdateUserEntryAsync(entry.Id, updated.Word, updated.Phonetic,
            updated.Definition, updated.Translation, updated.Example, null, updated.Tags);
        if (languageChanged)
        {
            System.Windows.MessageBox.Show(this, "语言不可通过编辑修改，忽略 language 变更。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        await ReloadAsync(string.IsNullOrWhiteSpace(FilterBox.Text) ? null : FilterBox.Text);
    }


    private static string[] ParseTags(string tagsJson)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(tagsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
