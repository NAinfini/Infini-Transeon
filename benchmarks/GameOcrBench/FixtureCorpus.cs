using System.Collections.Generic;

namespace GameOcrBench;

/// <summary>
/// Deterministic, source-embedded, game-flavoured text corpus. No network access:
/// every string below is compiled into the generator. Covers Simplified Chinese,
/// Traditional Chinese, Japanese (kana + kanji), Korean and English/Latin.
/// </summary>
internal static class FixtureCorpus
{
    /// <summary>Ordered language identifiers the generator renders by default.</summary>
    public static readonly IReadOnlyList<string> Languages = new[]
    {
        "zh-Hans",
        "zh-Hant",
        "ja",
        "ko",
        "en",
    };

    /// <summary>
    /// Per-language game-flavoured strings: dialogue, HUD hints and item names.
    /// Order is fixed so seed-driven selection is reproducible.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> Strings =
        new Dictionary<string, string[]>(System.StringComparer.Ordinal)
        {
            ["zh-Hans"] = new[]
            {
                "剑士登场，准备战斗！",
                "生命值不足，请及时治疗",
                "获得道具：治愈药水 x3",
                "按下 F 键与村民对话",
                "任务完成：击败暗影魔王",
                "存档已保存到槽位 2",
            },
            ["zh-Hant"] = new[]
            {
                "劍士登場，準備戰鬥！",
                "生命值不足，請及時治療",
                "獲得道具：治癒藥水 x3",
                "按下 F 鍵與村民對話",
                "任務完成：擊敗暗影魔王",
                "遊戲進度已存至欄位 2",
            },
            ["ja"] = new[]
            {
                "勇者よ、いざ出発だ！",
                "ライフが足りない、回復せよ",
                "入手：回復のポーション x3",
                "Ｆキーで村人と話す",
                "クエスト達成：魔王討伐",
                "スロット2にセーブしました",
            },
            ["ko"] = new[]
            {
                "용사여, 이제 출발하자!",
                "체력이 부족합니다, 회복하세요",
                "획득: 회복 물약 x3",
                "F 키를 눌러 마을 사람과 대화",
                "퀘스트 완료: 마왕 토벌",
                "슬롯 2에 저장했습니다",
            },
            ["en"] = new[]
            {
                "The hero arrives, ready for battle!",
                "Health is low, heal up now",
                "Obtained: Healing Potion x3",
                "Press F to talk to the villager",
                "Quest complete: Slay the Shadow Boss",
                "Game saved to slot 2",
            },
        };

    public static bool IsKnownLanguage(string language) => Strings.ContainsKey(language);

    /// <summary>Returns the fixed string list for a language, or throws for an unknown one.</summary>
    public static IReadOnlyList<string> ForLanguage(string language)
    {
        if (!Strings.TryGetValue(language, out string[]? values))
            throw new System.InvalidOperationException($"No corpus is defined for language '{language}'.");
        return values;
    }
}
