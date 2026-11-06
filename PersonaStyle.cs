using System;
using System.Text.Json;

namespace Suran.DailyReport;

/// <summary>
/// 从角色文件夹的 `index.json` 里取人设文本，给日报锐评当"怎么说"的参考。
///
/// 为什么单独一个纯函数：这段逻辑要能离开宿主独立测试。
/// 这里只认 JSON 的 `Prompt` 字段；找不到语气小节就退回全文。
/// </summary>
public static class PersonaStyle
{
    public sealed record Result(string Text, string Mode);

    // 常见写法，命中任意一个就只取那一节。都不是必需项——找不到就退化全文。
    static readonly string[] StyleHeadings =
    [
        "## 性格与语言", "## 性格", "## 语言风格", "## 说话方式", "## 口癖", "## 风格",
        "## Personality", "## Speaking Style", "## Style",
    ];

    public static Result Extract(string json, bool styleOnly, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Result("", "没有 index.json");

        string prompt;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new Result("", "index.json 不是对象");
            if (!document.RootElement.TryGetProperty("Prompt", out JsonElement element)
                || element.ValueKind != JsonValueKind.String)
                return new Result("", "index.json 没有 Prompt 字段");
            prompt = (element.GetString() ?? "").Trim();
        }
        catch (JsonException)
        {
            return new Result("", "index.json 解析失败");
        }

        if (prompt.Length == 0)
            return new Result("", "Prompt 是空的");

        string mode = "全文";
        if (styleOnly)
        {
            int start = -1;
            foreach (string heading in StyleHeadings)
            {
                int at = prompt.IndexOf(heading, StringComparison.Ordinal);
                if (at >= 0 && (start < 0 || at < start))
                    start = at;
            }

            if (start >= 0)
            {
                // 取到下一个同级标题为止；没有下一节就取到文末
                int next = prompt.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
                prompt = (next > start ? prompt[start..next] : prompt[start..]).Trim();
                mode = "仅语气段";
            }
            else
            {
                mode = "全文（没找到语气小节）";
            }
        }

        if (maxChars > 0 && prompt.Length > maxChars)
        {
            prompt = prompt[..maxChars] + "…";
            mode += "·已截断";
        }
        return new Result(prompt, mode);
    }

    /// <summary>
    /// 把「角色人设语气」和「设置页手填的锐评风格」合成一段。
    /// 手填是**追加**不是覆盖：用户要的是在角色本来的说话方式上加一句要求，
    /// 而不是把人设丢掉。人设读不到时手填也能单独成立。
    /// </summary>
    public static string Combine(string persona, string extra)
    {
        string p = (persona ?? "").Trim();
        string e = (extra ?? "").Trim();
        if (e.Length == 0)
            return p;
        return p.Length == 0 ? e : p + "\n\n【额外风格要求】\n" + e;
    }
}
