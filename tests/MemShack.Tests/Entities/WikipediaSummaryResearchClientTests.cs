using System.Net;
using System.Net.Http;
using System.Text;
using MemShack.Application.Entities;

namespace MemShack.Tests.Entities;

[TestClass]
public sealed class WikipediaSummaryResearchClientTests
{
    [TestMethod]
    public void Lookup_NameIndicatorSummary_ReturnsPerson()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {
              "type": "standard",
              "title": "Grace",
              "extract": "Grace is a feminine given name of Latin origin."
            }
            """);
        var client = new WikipediaSummaryResearchClient(httpClient);

        var result = client.Lookup("Grace");

        Assert.Equal("person", result.InferredType);
        Assert.Equal(0.90, result.Confidence);
        Assert.Equal("Grace", result.WikiTitle);
        Assert.Contains("given name", result.WikiSummary);
    }

    [TestMethod]
    public void Lookup_DisambiguationNamePage_ReturnsPersonWithNote()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {
              "type": "disambiguation",
              "title": "Sam",
              "description": "name",
              "extract": "Sam may refer to people, places, or fictional characters."
            }
            """);
        var client = new WikipediaSummaryResearchClient(httpClient);

        var result = client.Lookup("Sam");

        Assert.Equal("person", result.InferredType);
        Assert.Equal(0.65, result.Confidence);
        Assert.Equal("disambiguation page with name entries", result.Note);
    }

    [TestMethod]
    public void Lookup_NotFound_ReturnsLikelyProperNounFallback()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.NotFound, """
            {
              "type": "https://mediawiki.org/wiki/HyperSwitch/errors/not_found"
            }
            """);
        var client = new WikipediaSummaryResearchClient(httpClient);

        var result = client.Lookup("Rylith");

        Assert.Equal("person", result.InferredType);
        Assert.Equal(0.70, result.Confidence);
        Assert.Equal("not found in Wikipedia - likely a proper noun or unusual name", result.Note);
    }

    [TestMethod]
    public void Lookup_PlaceIndicatorSummary_ReturnsPlace()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {
              "type": "standard",
              "title": "Arcadia",
              "extract": "Arcadia is a city in California, United States."
            }
            """);
        var client = new WikipediaSummaryResearchClient(httpClient);

        var result = client.Lookup("Arcadia");

        Assert.Equal("place", result.InferredType);
        Assert.Equal(0.80, result.Confidence);
    }

    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, string content)
    {
        return new HttpClient(new StubHttpMessageHandler(statusCode, content))
        {
            BaseAddress = new Uri("https://en.wikipedia.org"),
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public StubHttpMessageHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
