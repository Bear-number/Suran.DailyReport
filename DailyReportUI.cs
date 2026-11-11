using Alife.Framework;
using Microsoft.AspNetCore.Components.Rendering;

namespace Suran.DailyReport;

/// <summary>群聊日报 的设置页。内容由 SuranUiKit 按配置项自动分组生成。</summary>
public sealed class DailyReportUI : ModuleUIBase<GroupDailyReportModule, DailyReportConfig>
{
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "配置未加载");
            return;
        }
        SuranUiKit.Render(this, StateHasChanged, b, Configuration, "群聊日报", "拉群聊记录做统计，LLM 写人设锐评，渲染成主题图片发回群里");
    }
}
