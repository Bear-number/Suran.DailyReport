
# Suran.DailyReport

群聊日报，移植自 KiraAI 的 KiraAI_daily_report_plugin：从协议端拉取群聊记录做统计分析（消息总数/参与人数/最活跃时段/活跃用户 TOP），LLM 精选热门话题与金句并带角色人设锐评，渲染成 5 套主题的精美图片发回群里。

## 版本
5.0.0（对应 Alife 客户端 5.0.x；本机 5.0.2 在役客户端已热编译装载成功）

## 5.0.0 的改动
- 版本号对齐客户端主版本（官方约定：插件版本首位应与客户端一致，`PluginSystem.IsClientCompatible` 按主版本判定）
- 浏览器发现的注册表分支包进 `#if !ANDROID`（5.0 热编译按平台注入 `WINDOWS/ANDROID/LINUX` 符号，Android 端引不到 `Microsoft.Win32.Registry` 这个类型）；非 Windows 改为在 PATH 上找 chrome/chromium/microsoft-edge/brave，找不到就报「请在配置里填浏览器路径」而不是抛平台异常

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
| ApiProvider | auto | 协议端类型：auto 自动识别 / napcat / llonebot / snowluma，决定历史翻页用哪个锚点字段 |
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
- 历史翻页锚点按协议端区分：NapCat 与 LLOneBot 只读 `message_seq`，SnowLuma 只读 `message_id`（其值是带符号哈希，可为负且不表征时间顺序）。每页先用 `message_seq` 定位最旧那条消息，再把这条消息自己的对应字段发出去当锚点，两个字段的值绝不混用——锚点发错字段**不报错**，但有两种完全不同的表现：认不出的锚点被直接丢掉的端回**最新那一页**，反解不出锚点的端回**空列表**，所以「回空」和「最旧一条没比上一页更旧」都当成锚点失效处理
- SnowLuma 的历史是「本地存过 + 服务器补拉」两条路：不带锚点时先从本地 store 取 anchorSeq，`anchorSeq>0` 才走服务器 `SsoGetGroupMsg`，一条都没存过（或它的历史同步没开）第一页就是空——这时诊断会直接写明「协议端没有该群的任何历史」，不是插件翻页的问题。它的 `count` 被夹在 1..200（`<=0` 当 20），返回的历史条目会被剥掉 `post_type`/`self_id`
- `auto` 下先按 `get_version_info`（app_name/name/vendor）认端；认不出则两种锚点各试一轮，猜对的标准是"真翻到了更旧的消息"（历史见底时错锚点会回最新那一页），生效的方式记住后续不再试
- 取到 0 条时，普通日志直接打出协议端名、锚点方式、原始条目数、被过滤条数与首条原始返回（截 500 字），并且"消息数不足"的提示会带上同一份拉取情况，用户不用开详细日志就能回报
- 图片消息段按端兜底：先用 `file:///` 本地路径（NapCat 与 LLOneBot 走 url→file 顺序，不给 url 时才读 file），被拒时同一张图改走 `base64://`，再失败才降级文字日报
- 接口未实现（retcode 1404）时报「协议端 X 未实现该接口」，不再只丢一个裸错误码
- 失败原因同时读 `message` 与 `wording`：SnowLuma 的失败包只填 `wording`（LLOneBot/NapCat 两个键填同一个值），只读 `message` 会让 SnowLuma 的所有报错都显示成「未知错误」
