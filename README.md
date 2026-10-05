
# Suran.DailyReport

群聊日报，移植自 KiraAI 的 KiraAI_daily_report_plugin：从协议端拉取群聊记录做统计分析（消息总数/参与人数/最活跃时段/活跃用户 TOP），LLM 精选热门话题与金句并带角色人设锐评，渲染成 5 套主题的精美图片发回群里。

## 版本
4.0.0（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller
- 图片渲染需要系统装有 **Microsoft Edge 或 Google Chrome**（Windows 默认自带 Edge，无需额外安装浏览器）

## 工作方式（与原版的核心差异）
原版持续监听消息存 SQLite，日报时分析库里的数据。Alife 版架构不同：**生成日报时通过 OneBot `get_group_msg_history` 从协议端分页拉取最近的消息**（最多 MaxMessages 条、限定 AnalysisDays 窗口），因此：
- 无需消息收集与数据库，插件更轻
- 数据完整性取决于协议端保留的历史消息量（NapCat 默认可翻页拉取较多，太久远的信息拉不到）

## AI 函数
- GenerateDailyReport(groupId, operatorId) - 生成日报（异步）：先返回"已开始生成"，后台完成分析渲染后**自动把日报发到群里**并通知AI

## 触发方式
- **自然语言**：用户说"总结一下今天的群聊""生成日报"，AI 调用 GenerateDailyReport
- **/日报 命令**：原版的斜杠命令在 Alife 中由 AI 承接（QChat 会把 "/日报" 交给 AI，函数文档已注明该触发词）
- **定时自动**：每天 AutoReportTime（默认 23:59）为 ScheduledGroups 里的群自动生成

## 配置
| 键 | 默认 | 说明 |
|---|---|---|
| OneBotWsUrl | ws://127.0.0.1:3001 | OneBot 正向 WebSocket 地址 |
| BotNickname | 空 | 锐评署名，留空自动获取 |
| EnabledGroups | 空 | 可使用日报的群号（逗号分隔），留空=所有群 |
| ScheduledGroups | 空 | 定时生成的群号，留空=跟随 EnabledGroups |
| DetailLog | 关 | 拉取/统计/渲染调试日志 |
| AnalysisDays | 1 | 分析最近几天的消息 |
| MaxMessages | 1000 | 单次最多分析的消息数 |
| MinMessages | 10 | 少于该值不生成 |
| EnableAutoReport | 开 | 定时任务开关 |
| AutoReportTime | 23:59 | 自动生成时间（HH:mm） |
| ActiveWindowMinutes | 90 | 活跃时段滑动窗口宽度 |
| EnableComment | 开 | AI 锐评 |
| MinTopics / MaxTopics | 1 / 5 | 话题数量范围 |
| QuoteCount | 3 | 金句数量（0=不展示） |
| TopUserCount | 10 | 活跃用户展示数 |
| CooldownHours | 4 | 同群两次日报的最小间隔 |
| WhitelistUsers | 空 | 白名单QQ号（逗号分隔） |
| WhitelistExemptCooldown | 开 | 白名单用户豁免冷却 |
| OutputFormat | image | image=图片 / text=纯文本 |
| Theme | warm | warm暖黄 / sakura樱花粉 / clover幸运草绿 / diamond钻石黑 / sky天空蓝 / random随机 |

## 日报内容
群头像印章 + 群名 + 日期范围 + 主题风格；消息总数 / 参与人数 / 最活跃时段（滑动窗口精确到分钟）；热门话题（LLM 精选+概括）；活跃用户 TOP N（带QQ头像，缓存3天）；今日金句（LLM 评选+理由）；人设锐评（interactor.ChatAsync 走当前角色人设，天然注入）。

## 实现说明
- 统计（总数/参与人数/活跃用户/活跃时段滑动窗口）全部从原始消息计算，零 Token
- LLM 一次调用完成话题+金句+锐评（原版 v1.2.5 的合并优化思路），回复要求严格 JSON，解析失败有兜底
- 渲染用系统 Edge/Chrome 无头截图（`--headless=new --screenshot`），无 Playwright、无 NuGet 依赖；找不到浏览器自动回退文字日报
- 产出文件存 `{存储目录}/DailyReport/reports/`，头像缓存 `avatars/`
- 同群生成中不重复触发；冷却按群记录（内存，插件重载后清零）
