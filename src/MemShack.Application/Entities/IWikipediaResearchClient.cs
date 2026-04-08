namespace MemShack.Application.Entities;

public interface IWikipediaResearchClient
{
    bool IsSupported { get; }

    WikipediaResearchResult Lookup(string word);
}
