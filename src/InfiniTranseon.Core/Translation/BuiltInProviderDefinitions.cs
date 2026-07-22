using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation;

public static class BuiltInProviderDefinitions
{
    public static OpenAiCompatibleOptions OpenAi(
        string model,
        string credentialReference,
        ProxyPolicy proxyPolicy = ProxyPolicy.System) => OpenAiCompatible(
            "llm.openai", "https://api.openai.com/v1/chat/completions",
            model, credentialReference, proxyPolicy);

    public static OpenAiCompatibleOptions DeepSeek(
        string model,
        string credentialReference,
        ProxyPolicy proxyPolicy = ProxyPolicy.System) => OpenAiCompatible(
            "llm.deepseek", "https://api.deepseek.com/chat/completions",
            model, credentialReference, proxyPolicy);

    public static OpenAiCompatibleOptions QwenModelStudio(
        string model,
        string credentialReference,
        ProxyPolicy proxyPolicy = ProxyPolicy.System) => OpenAiCompatible(
            "llm.qwen-model-studio",
            "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
            model, credentialReference, proxyPolicy);

    public static OpenAiCompatibleOptions BaiduQianfan(
        string model,
        string credentialReference,
        ProxyPolicy proxyPolicy = ProxyPolicy.System) => OpenAiCompatible(
            "llm.baidu-qianfan", "https://qianfan.baidubce.com/v2/chat/completions",
            model, credentialReference, proxyPolicy);

    public static DeclarativeRestAdapterDefinition DeepL(bool freeEndpoint) => new(
        schemaVersion: 1,
        id: "translation.deepl",
        displayName: "DeepL",
        endpoint: new Uri(freeEndpoint
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate"),
        method: RestHttpMethod.Post,
        headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "DeepL-Auth-Key {{credential:api-key}}",
        },
        bodyTemplate: "{\"text\":[\"{{sourceText}}\"],\"target_lang\":\"{{targetLanguage}}\"}",
        responseTextJsonPointer: "/translations/0/text",
        responseErrorJsonPointer: "/message",
        credentialReferences: ["api-key"],
        statusMappings: new Dictionary<int, RestStatusMapping>
        {
            [400] = new("provider.deepl.badRequest", false),
            [403] = new("provider.deepl.authorization", false),
            [413] = new("provider.deepl.requestTooLarge", false),
            [429] = new("provider.deepl.rateLimited", true),
            [456] = new("provider.deepl.quotaExceeded", false),
            [500] = new("provider.deepl.server", true),
            [502] = new("provider.deepl.server", true),
            [503] = new("provider.deepl.unavailable", true),
            [504] = new("provider.deepl.timeout", true),
        });

    private static OpenAiCompatibleOptions OpenAiCompatible(
        string providerId,
        string endpoint,
        string model,
        string credentialReference,
        ProxyPolicy proxyPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReference);
        return new OpenAiCompatibleOptions(
            providerId,
            new Uri(endpoint),
            model,
            credentialReference,
            proxyPolicy,
            IncludeGameContext: true,
            IncludeRecentHistory: true);
    }
}
