using System.Windows;
using TranslateLite.Models;

namespace TranslateLite.Windows;

public sealed record UserEntryInput(
    string Word,
    string Language,
    string? Phonetic,
    string Definition,
    string Translation,
    string? Example,
    string[] Tags);

/// <summary>用户词条编辑器：新增与编辑共用同一套字段，保存后通过 <see cref="Result"/> 回传。</summary>
public partial class UserEntryEditorWindow : Window
{
    /// <summary>保存成功时返回输入；取消为 null。</summary>
    public UserEntryInput? Result { get; private set; }

    public UserEntryEditorWindow()
        : this(word: "", language: "en", phonetic: null, definition: "", translation: "", example: null, tags: [])
    {
    }

    public UserEntryEditorWindow(string word, string language, string? phonetic,
        string definition, string translation, string? example, string[] tags)
    {
        InitializeComponent();
        WordBox.Text = word;
        LangBox.SelectedIndex = language == "zh" ? 1 : 0;
        PhoneticBox.Text = phonetic ?? "";
        TranslationBox.Text = translation ?? "";
        DefinitionBox.Text = definition ?? "";
        ExampleBox.Text = example ?? "";
        TagsBox.Text = string.Join(",", tags);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var word = WordBox.Text.Trim();
        var translation = TranslationBox.Text.Trim();
        if (word.Length == 0)
        {
            System.Windows.MessageBox.Show(this, "词条不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (translation.Length == 0 && DefinitionBox.Text.Trim().Length == 0)
        {
            System.Windows.MessageBox.Show(this, "翻译和释义至少填一项。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var tags = TagsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var language = LangBox.SelectedItem is System.Windows.Controls.ComboBoxItem item
            ? (string?)item.Tag ?? "en"
            : "en";

        Result = new UserEntryInput(
            word,
            language,
            string.IsNullOrWhiteSpace(PhoneticBox.Text) ? null : PhoneticBox.Text.Trim(),
            DefinitionBox.Text.Trim(),
            translation,
            string.IsNullOrWhiteSpace(ExampleBox.Text) ? null : ExampleBox.Text.Trim(),
            tags);
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
