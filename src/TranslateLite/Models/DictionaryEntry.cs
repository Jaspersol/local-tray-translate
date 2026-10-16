namespace TranslateLite.Models;

public sealed record DictionaryEntry(
    string Word,
    string Language,
    string? Phonetic,
    string Definition,
    string Translation,
    string? Example,
    string Source);
