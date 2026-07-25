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

    /// <summary>
    /// DeepL has two mutually exclusive endpoints, and a key issued for one is rejected with 403 by
    /// the other. Free keys (the suffixed ":fx" ones anybody can get without a card) only work
    /// against api-free.deepl.com. The two therefore have to be two distinct providers with distinct
    /// ids, so each gets its own credential slot and the user picks the tier explicitly instead of
    /// being told "authorization failed" with no way to act on it.
    /// </summary>
    public static DeclarativeRestAdapterDefinition DeepL(bool freeEndpoint) => new(
        schemaVersion: 1,
        id: freeEndpoint ? "translation.deepl-free" : "translation.deepl",
        displayName: freeEndpoint ? "DeepL API Free" : "DeepL",
        endpoint: new Uri(freeEndpoint
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate"),
        method: RestHttpMethod.Post,
        headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Credential references are the Credential Manager keys and must stay globally unique,
            // so the two tiers cannot share the bare "api-key" slot — entering a free key would
            // overwrite the Pro one. The Pro reference keeps its original unqualified name so
            // already-stored keys are not orphaned; only the new tier is fully qualified.
            ["Authorization"] = freeEndpoint
                ? "DeepL-Auth-Key {{credential:translation.deepl-free.api-key}}"
                : "DeepL-Auth-Key {{credential:api-key}}",
        },
        bodyTemplate: "{\"text\":[\"{{sourceText}}\"],\"target_lang\":\"{{targetLanguage}}\"}",
        responseTextJsonPointer: "/translations/0/text",
        responseErrorJsonPointer: "/message",
        credentialReferences: freeEndpoint ? ["translation.deepl-free.api-key"] : ["api-key"],
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

    public static DeclarativeRestAdapterDefinition NiuTrans() => new(
        schemaVersion: 1,
        id: "translation.niutrans",
        displayName: "NiuTrans",
        endpoint: new Uri("https://api.niutrans.com/NiuTransServer/translation"),
        method: RestHttpMethod.Post,
        headers: new Dictionary<string, string>(),
        bodyTemplate:
            "from={{sourceLanguage}}&to={{targetLanguage}}&" +
            "apikey={{credential:translation.niutrans.api-key}}&src_text={{sourceText}}",
        responseTextJsonPointer: "/tgt_text",
        responseErrorJsonPointer: "/error_code",
        credentialReferences: ["translation.niutrans.api-key"],
        bodyFormat: RestBodyFormat.FormUrlEncodedUtf8,
        statusMappings: new Dictionary<int, RestStatusMapping>
        {
            [429] = new("provider.niutrans.rateLimited", true),
            [500] = new("provider.niutrans.server", true),
            [502] = new("provider.niutrans.server", true),
            [503] = new("provider.niutrans.unavailable", true),
            [504] = new("provider.niutrans.timeout", true),
        },
        languageCodeStyle: RestLanguageCodeStyle.BaseLanguage);

    public static DeclarativeRestAdapterDefinition Yandex() => new(
        schemaVersion: 1,
        id: "translation.yandex",
        displayName: "Yandex Cloud Translate",
        endpoint: new Uri("https://translate.api.cloud.yandex.net/translate/v2/translate"),
        method: RestHttpMethod.Post,
        headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Api-Key {{credential:translation.yandex.api-key}}",
        },
        bodyTemplate:
            "{\"targetLanguageCode\":\"{{targetLanguage}}\",\"texts\":[\"{{sourceText}}\"]," +
            "\"folderId\":\"{{credential:translation.yandex.folder-id}}\",\"format\":\"PLAIN_TEXT\"}",
        responseTextJsonPointer: "/translations/0/text",
        responseErrorJsonPointer: "/message",
        credentialReferences:
            ["translation.yandex.api-key", "translation.yandex.folder-id"],
        statusMappings: new Dictionary<int, RestStatusMapping>
        {
            [401] = new("provider.yandex.authorization", false),
            [403] = new("provider.yandex.forbidden", false),
            [429] = new("provider.yandex.rateLimited", true),
            [500] = new("provider.yandex.server", true),
            [502] = new("provider.yandex.server", true),
            [503] = new("provider.yandex.unavailable", true),
            [504] = new("provider.yandex.timeout", true),
        },
        languageCodeStyle: RestLanguageCodeStyle.BaseLanguage);

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
