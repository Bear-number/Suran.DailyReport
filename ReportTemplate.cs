namespace Suran.DailyReport;

// 日报 HTML 模板：CSS 变量驱动 5 套主题，占位符由模块在生成时替换
public static class ReportTemplate
{
    public const string Html =
"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<style>
:root {
    --bg: #fdf6e3; --card: #fffdf7; --primary: #e6a23c; --accent: #b8860b;
    --text: #5b4636; --soft: #9c8a72; --line: #f0e2c8;
}
.theme-sakura { --bg:#fff0f5; --card:#fffafa; --primary:#ff8fab; --accent:#e75480; --text:#6d4356; --soft:#c494a6; --line:#ffd9e3; }
.theme-clover { --bg:#f0fff4; --card:#fafffa; --primary:#6abe83; --accent:#2e8b57; --text:#2f4f3a; --soft:#8aa894; --line:#d4ecd9; }
.theme-diamond { --bg:#2b2b2b; --card:#383838; --primary:#d4af37; --accent:#ffd700; --text:#f0f0f0; --soft:#9a9a8a; --line:#4a4a4a; }
.theme-sky { --bg:#e8f4fd; --card:#f7fcff; --primary:#5db8ee; --accent:#1e90ff; --text:#2f5b7c; --soft:#8fb4cc; --line:#d0e8fa; }
* { margin: 0; padding: 0; box-sizing: border-box; }
/* 这里不能再给 html 设背景：主题变量挂在 body 的 class 上，html 用 :root 的暖黄底色会把
   body 之外的整块画布染成暖黄（实测非暖黄主题时，body 右侧 40px 和文档下方都是暖黄）。
   不设的话浏览器会把 body 的背景 propagation 到画布，整张图就是主题色。 */
body {
    background: var(--bg); color: var(--text);
    font-family: "Microsoft YaHei", "PingFang SC", sans-serif;
    /* 整张图统一放大字号：改一处 zoom 比逐条 px 靠谱（不会漏掉哪个角落）。
       宽 610 × 1.18 ≈ 720，截图窗口仍是 760，横向不会溢出；行高、头像、间距一起跟着长。 */
    width: 610px; margin: 0 auto; padding: 24px 20px; zoom: 1.18;
}
.card {
    background: var(--card); border: 2px solid var(--line); border-radius: 16px;
    padding: 18px 20px; margin-bottom: 16px; box-shadow: 0 2px 8px rgba(0,0,0,.06);
}
.header { text-align: center; padding: 22px 20px 18px; }
.header .group-name { font-size: 26px; font-weight: 800; color: var(--accent); letter-spacing: 2px; }
.header .date-line { margin-top: 6px; font-size: 14px; color: var(--soft); }
.header .stamp { width: 64px; height: 64px; border-radius: 50%; margin: 0 auto 10px; border: 3px solid var(--primary); object-fit: cover; }
.stats { display: flex; justify-content: space-between; gap: 10px; }
.stat { flex: 1; text-align: center; }
.stat .num { font-size: 22px; font-weight: 800; color: var(--primary); }
.stat .label { font-size: 12px; color: var(--soft); margin-top: 4px; }
.section-title { font-size: 16px; font-weight: 800; color: var(--accent); margin-bottom: 10px; }
/* 话题卡：标题和「由 X 引出」同一行，摘要与点评独占整行。
   摘要跟署名并排时会被压窄、换行难看，署名也会垂直居中插在摘要中间。 */
.topic { display: block; margin-bottom: 16px; padding-left: 12px; border-left: 4px solid var(--primary); }
.topic-body { min-width: 0; text-align: left; }
.t-head { display: flex; align-items: center; gap: 8px; }
.t-head .t-name { flex: 0 1 auto; min-width: 0; font-size: 15px; font-weight: 700; line-height: 1.4; }
.t-initiator { display: flex; align-items: center; gap: 6px; margin-left: auto; flex-shrink: 0; }
.t-starter { color: var(--primary); font-size: 12px; white-space: nowrap; }
.t-avatar, .ph.t-avatar { width: 24px; height: 24px; border-radius: 50%; object-fit: cover; flex-shrink: 0; }
.topic .t-sum { font-size: 12px; color: var(--soft); margin-top: 4px; line-height: 1.6; }
.t-cmt, .q-reason { display: flex; align-items: center; gap: 6px; font-size: 12px; color: var(--accent); margin-top: 5px; }
.t-cmt .c-face, .q-reason .c-face, .ph.c-face { width: 18px; height: 18px; border-radius: 50%; object-fit: cover; flex-shrink: 0; }
.ph.c-face { font-size: 10px; }
.quote { position: relative; display: flex; align-items: flex-start; gap: 10px; background: var(--bg); border-radius: 10px; padding: 10px 12px; margin-bottom: 8px; }
.q-avatar, .ph.q-avatar { width: 34px; height: 34px; border-radius: 50%; object-fit: cover; flex-shrink: 0; margin-top: 2px; }
.quote-body { flex: 1; min-width: 0; }
.quote .q-text { font-size: 14px; font-weight: 600; line-height: 1.5; }
/* 「今日最佳」固定在卡片右上角，正文给它让出位置 */
.q-best { position: absolute; top: 8px; right: 7px; padding: 1px 6px; border-radius: 7px; background: var(--primary); color: #fff; font-size: 10px; font-weight: 700; letter-spacing: 0; }
.q-hot .q-text { padding-right: 44px; }
/* 署名单独一行右对齐 */
.q-who { margin-top: 5px; text-align: right; font-size: 12px; color: var(--soft); }
.comment-row { display: flex; align-items: center; gap: 14px; }
.c-avatar, .ph.c-avatar { width: 76px; height: 76px; border-radius: 14px; object-fit: cover; flex-shrink: 0; border: 3px solid var(--primary); }
.comment { font-size: 14px; line-height: 1.7; }
.ph { display: flex; align-items: center; justify-content: center; background: var(--primary); color: #fff; font-weight: 700; }
.user-row { display: flex; align-items: center; margin-bottom: 8px; }
.user-row .rank { width: 26px; font-weight: 800; color: var(--primary); }
.user-row img { width: 34px; height: 34px; border-radius: 50%; margin-right: 10px; object-fit: cover; }
.user-row .u-name { font-size: 14px; font-weight: 600; }
.user-row .u-count { margin-left: auto; font-size: 13px; color: var(--soft); }
.footer { text-align: center; font-size: 11px; color: var(--soft); margin-top: 4px; }
</style>
</head>
<body class="theme-__THEME__">
<div class="card header">
    <img class="stamp" src="__GROUP_AVATAR__">
    <div class="group-name">__GROUP_NAME__ 群聊日报</div>
    <div class="date-line">__DATE_RANGE__ · __THEME_NAME__</div>
</div>
<div class="card">
    <div class="stats">
        <div class="stat"><div class="num">__TOTAL__</div><div class="label">消息总数</div></div>
        <div class="stat"><div class="num">__PARTICIPANTS__</div><div class="label">参与人数</div></div>
        <div class="stat"><div class="num">__ACTIVE_RANGE__</div><div class="label">最活跃时段</div></div>
    </div>
</div>
<div class="card">
    <div class="section-title">🔥 热门话题</div>
    __TOPICS__
</div>
<div class="card">
    <div class="section-title">👤 活跃用户 TOP__TOPN__</div>
    __USERS__
</div>
<div class="card">
    <div class="section-title">💡 今日金句</div>
    __QUOTES__
</div>
<div class="card">
    <div class="section-title">🤖 __BOT_NAME__ 锐评</div>
    <div class="comment-row">__BOT_AVATAR__<div class="comment">__COMMENT__</div></div>
</div>
<div class="footer">Generated by Suran.DailyReport · 数据来自群聊记录</div>
</body>
</html>
""";
}
