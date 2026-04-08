namespace MemShack.Application.Entities;

public sealed record WikipediaResearchResult(
    string Word,
    string InferredType,
    double Confidence,
    string? WikiSummary = null,
    string? WikiTitle = null,
    string? Note = null,
    bool Confirmed = false,
    string? ConfirmedType = null)
{
    public string EffectiveType => string.IsNullOrWhiteSpace(ConfirmedType) ? InferredType : ConfirmedType;

    public static WikipediaResearchResult Unknown(string word, string? note = null) =>
        new(word, "unknown", 0.0, null, null, note);
}
