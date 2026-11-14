using System;
using System.Text.RegularExpressions;

namespace Suran.DailyReport;

/// <summary>
/// 总评的写法检查。模型很容易写成「一半人…另一半人…剩下的…」这种分组罗列，
/// 这类句子与当天聊了什么关系不大，读起来就是模板。
/// </summary>
public static class ReportVoice
{
    static readonly Regex SomeSome = new(@"有人.{0,24}有人", RegexOptions.Compiled);
    static readonly Regex PartPart = new(@"一部分.{0,24}(另一部分|还有一部分)", RegexOptions.Compiled);
    static readonly Regex AllNone = new(@"全员.{0,12}(无一人|没一个)", RegexOptions.Compiled);

    /// <summary>是不是分组罗列的模板写法。</summary>
    public static bool LooksTemplated(string comment)
    {
        string text = (comment ?? "").Trim();
        if (text.Length == 0)
            return false;

        if (Count(text, "一半") >= 2)
            return true;
        if (PartPart.IsMatch(text) || SomeSome.IsMatch(text) || AllNone.IsMatch(text))
            return true;
        if (text.Contains("剩下一小撮") || text.Contains("剩下的都"))
            return true;

        // 图上已经有日期，再从「今天/今晚」起头就是套话
        return text.StartsWith("今天", StringComparison.Ordinal)
            || text.StartsWith("今晚", StringComparison.Ordinal);
    }

    static int Count(string text, string word)
    {
        int total = 0;
        int at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            total++;
            at += word.Length;
        }
        return total;
    }
}
