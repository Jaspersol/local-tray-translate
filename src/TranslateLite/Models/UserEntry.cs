namespace TranslateLite.Models;

public sealed record UserEntry(
    int Id,
    string Word,
    string Language,
    string? Phonetic,
    string Definition,
    string Translation,
    string? Example,
    string TagsJson,
    string? Note,
    string CreatedAt,
    string UpdatedAt);
