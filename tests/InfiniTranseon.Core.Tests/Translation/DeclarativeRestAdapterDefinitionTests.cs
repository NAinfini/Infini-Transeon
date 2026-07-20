using InfiniTranseon.Contracts.Translation;

namespace InfiniTranseon.Core.Tests.Translation;

public sealed class DeclarativeRestAdapterDefinitionTests
{
    [Fact]
    public void HttpsAdapterUsesControlledVariablesAndCredentialReferences()
    {
        var definition = new DeclarativeRestAdapterDefinition(
            schemaVersion: 1,
            id: "custom.translate",
            displayName: "Custom Translate",
            endpoint: new Uri("https://api.example.test/v1/translate"),
            method: RestHttpMethod.Post,
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer {{credential:primary}}",
            },
            bodyTemplate: "{\"text\":\"{{sourceText}}\",\"target\":\"{{targetLanguage}}\"}",
            responseTextJsonPointer: "/data/translation",
            responseErrorJsonPointer: "/error/message",
            credentialReferences: ["primary"]);

        Assert.Equal("custom.translate", definition.Id);
        Assert.Equal(["primary"], definition.CredentialReferences);
    }

    [Theory]
    [InlineData("http://api.example.test/v1")]
    [InlineData("file:///C:/secrets.txt")]
    public void NonHttpsEndpointsAreRejected(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => Create(endpoint));
    }

    [Fact]
    public void UnknownTemplateVariablesAndHeaderInjectionAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(
            "https://api.example.test/v1",
            body: "{{executeCode}}"));
        Assert.Throws<ArgumentException>(() => Create(
            "https://api.example.test/v1",
            headers: new Dictionary<string, string> { ["X-Test"] = "ok\r\nInjected: true" }));
    }

    private static DeclarativeRestAdapterDefinition Create(
        string endpoint,
        string body = "{{sourceText}}",
        IReadOnlyDictionary<string, string>? headers = null) => new(
            1,
            "custom.translate",
            "Custom Translate",
            new Uri(endpoint),
            RestHttpMethod.Post,
            headers ?? new Dictionary<string, string>(),
            body,
            "/translation",
            null,
            []);
}
