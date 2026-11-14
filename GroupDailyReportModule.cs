using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Agents;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;
// 宿主热编译会按平台注入 WINDOWS/ANDROID/LINUX 符号；注册表只在桌面 Windows 侧可用，Android 端连类型都引不到
#if !ANDROID
using Microsoft.Win32;
#endif

namespace Suran.DailyReport;

public class DailyReportConfig
{
    [DisplayName("运行·功能文档常驻")]
    [Description("开：功能说明常驻系统提示词，模型随时看得到工具；关：隐式加载省 token，模型先输出入口标签再读文档。日报多是主人明确要求或定时触发，默认关")]
    public bool DocumentAlwaysResident { get; set; } = false;

    // ---- 基础设置 ----
    [DisplayName("基础·协议端地址")]
    [Description("留空 = 自动跟随「QQ 聊天」插件的配置（推荐）。填了就用这里的，例如 ws://127.0.0.1:3001")]
    public string OneBotWsUrl { get; set; } = "";

    [DisplayName("基础·访问令牌")]
    [Description("与协议端配置的 token 一致。留空 = 跟随「QQ 聊天」插件。填了地址却没填这里，协议端一开 token 就会连不上")]
    public string OneBotToken { get; set; } = "";

    [DisplayName("基础·协议端类型")]
    [Description("协议端类型，auto 自动识别，也可指定 napcat / llonebot / snowluma。")]
    public string ApiProvider { get; set; } = "auto";

    [DisplayName("基础·指定机器人昵称")]
    [Description("锐评署名，留空自动获取Bot昵称")]
    public string BotNickname { get; set; } = "";

    [DisplayName("基础·可使用日报的群组")]
    [Description("群号英文逗号分隔，留空=所有群")]
    public string EnabledGroups { get; set; } = "";

    [DisplayName("基础·定时排除的群组")]
    [Description("群号英文逗号分隔，这些群不参与定时自动生成（不影响手动生成）")]
    public string DisabledGroups { get; set; } = "";

    [DisplayName("基础·定时开启日报的群组")]
    [Description("群号英文逗号分隔，留空=与可使用群组一致")]
    public string ScheduledGroups { get; set; } = "";

    [DisplayName("基础·详细日志")]
    [Description("开启后输出拉取/统计/渲染的调试日志")]
    public bool DetailLog { get; set; } = false;

    // ---- 分析参数 ----
    [DisplayName("分析·分析天数")]
    [Description("日报分析最近几天的消息")]
    public int AnalysisDays { get; set; } = 1;

    [DisplayName("分析·单次最大分析消息数")]
    [Description("最多拉取的消息条数，防止消耗过大")]
    public int MaxMessages { get; set; } = 1000;

    [DisplayName("分析·提示词字符预算")]
    [Description("群聊记录拼进提示词的最大字符数；超出时优先丢最老的消息，防止上下文超限被模型拒答")]
    public int PromptMaxChars { get; set; } = 20000;

    [DisplayName("分析·最小消息数阈值")]
    [Description("少于该值不生成日报")]
    public int MinMessages { get; set; } = 10;

    [DisplayName("分析·启用自动日报")]
    [Description("开启后每天在指定时间自动生成")]
    public bool EnableAutoReport { get; set; } = true;

    [DisplayName("分析·自动分析时间")]
    [Description("每天自动生成日报的时间，格式 HH:mm，默认 23:59")]
    public string AutoReportTime { get; set; } = "23:59";

    [DisplayName("分析·定时补发窗口(分钟)")]
    [Description("到点没发成（进程重启、模型失败）时，在这个窗口内继续补发；超过窗口就等下一次定时。0 = 只在到点那一刻发")]
    public int AutoReportCatchUpMinutes { get; set; } = 180;

    [DisplayName("分析·活跃时段窗口(分钟)")]
    [Description("滑动窗口统计最活跃时段的窗口宽度")]
    public int ActiveWindowMinutes { get; set; } = 90;

    [DisplayName("分析·时间范围口径")]
    [Description("rolling=最近 N 天滚动窗口；naturalDay=按自然日（定时按到点那天、手动按今天算）")]
    public string AnalysisWindowMode { get; set; } = "rolling";

    // ---- 内容设置 ----
    [DisplayName("内容·启用AI锐评")]
    [Description("日报末尾增加人设风格的点评")]
    public bool EnableComment { get; set; } = true;

    [DisplayName("内容·启用锐评改写")]
    [Description("总评写成「一半人…另一半人…」这类模板时，再调一次模型改写；关掉则保留原句，省一次调用")]
    public bool EnableCommentRewrite { get; set; } = true;

    [DisplayName("内容·锐评读角色人设")]
    [Description("关掉就不注入角色人设，锐评只按通用要求写")]
    public bool PersonaFromCharacter { get; set; } = true;

    [DisplayName("内容·锐评人设只取语气段")]
    [Description("只截取人设里讲口癖和句式的那一节；关掉则用整段")]
    public bool PersonaStyleOnly { get; set; } = true;

    [DisplayName("内容·锐评人设字数上限")]
    [Description("注入的人设最多多少字，0 = 不限")]
    public int PersonaMaxChars { get; set; } = 4000;

    [DisplayName("内容·锐评风格（手填）")]
    [Description("额外写一句想要的锐评口吻，追加在人设后面；留空就只用角色人设")]
    public string CommentStyle { get; set; } = "";

    [DisplayName("内容·最少话题数")]
    [Description("话题不足时显示问号占位")]
    public int MinTopics { get; set; } = 1;

    [DisplayName("内容·最多话题数")]
    [Description("最多展示的话题数")]
    public int MaxTopics { get; set; } = 5;

    [DisplayName("内容·金句数量")]
    [Description("展示的金句数，0为不展示")]
    public int QuoteCount { get; set; } = 3;

    [DisplayName("内容·活跃用户数量")]
    [Description("展示的活跃用户数")]
    public int TopUserCount { get; set; } = 10;

    // ---- 权限与安全 ----
    [DisplayName("权限·调用冷却时间(小时)")]
    [Description("同一群两次日报的最小间隔")]
    public double CooldownHours { get; set; } = 4;

    [DisplayName("权限·白名单用户")]
    [Description("QQ号英文逗号分隔，不受冷却限制（受豁免开关控制）")]
    public string WhitelistUsers { get; set; } = "";

    [DisplayName("权限·白名单豁免冷却")]
    [Description("关闭后白名单用户也受冷却限制")]
    public bool WhitelistExemptCooldown { get; set; } = true;

    // ---- 输出与渲染 ----
    [DisplayName("输出·输出格式")]
    [Description("image=图片（需要系统装有Edge或Chrome）/ text=纯文本")]
    public string OutputFormat { get; set; } = "image";

    [DisplayName("输出·日报主题风格")]
    [Description("warm暖黄 / sakura樱花粉 / clover幸运草绿 / diamond钻石黑 / sky天空蓝 / random随机")]
    public string Theme { get; set; } = "warm";

    [DisplayName("输出·裁掉图片底部空白")]
    [Description("无头浏览器只能按窗口高度截图，估算富余会变成图底部的空白。开着会在截图后从下往上找到最后一行有内容的像素并裁掉多余部分；裁失败就用原图，不影响发送")]
    public bool CropBottomBlank { get; set; } = true;

    [DisplayName("输出·浏览器可执行文件")]
    [Description("无头截图用的 Edge/Chrome 完整路径，留空自动探测")]
    public string BrowserPath { get; set; } = "";

    [DisplayName("输出·保留最近几份日报文件")]
    [Description("每次生成都往存储目录写一份 .html 与 .png。这里是最多保留的「份数」（一份=同批的 html+png），超出的按时间老的删；0=不清理")]
    public int ReportKeepCount { get; set; } = 30;
}

[Module("群聊日报",
    "图片式智能群聊日报：从协议端拉取群聊记录做统计分析（消息数/参与人数/活跃时段/活跃用户），LLM精选话题金句并带人设锐评，渲染成5套主题的精美图片发回群里",
    defaultCategory: "苏染的工具/QQ与群管理",
    EditorUI = typeof(DailyReportUI))]
public partial class GroupDailyReportModule(
    XmlFunctionCaller functionCaller,
    ILogger<GroupDailyReportModule> logger,
    Interactor<GroupDailyReportModule> interactor
) : ChatBehaviour, IConfigurable<DailyReportConfig>
{
    public DailyReportConfig Configuration { get; set; } = null!;

    // 模型不一定会照提示词里的字数来，解析后按版面能容下的长度再截一次
    const int TopicTitleMaxChars = 20;
    const int TopicSummaryMaxChars = 80;
    const int TopicCommentMaxChars = 40;
    const int QuoteWhoMaxChars = 30;
    const int QuoteTextMaxChars = 80;
    const int QuoteReasonMaxChars = 40;
    const int CommentMaxChars = 120;

    /// <summary>
    /// 协议端连接信息。
    ///
    /// 地址留空时跟随「QQ 聊天」插件的配置——那是唯一值得人填一次的地方。
    /// 换端口、给协议端开 token，都不用回来改这个插件，读它的配置就够。
    /// 读不到就退回上一个版本的默认值，行为和以前完全一致。
    /// </summary>
    (string Url, string Token, string Source) ResolveOneBot()
    {
        string url = (Configuration.OneBotWsUrl ?? "").Trim();
        string token = (Configuration.OneBotToken ?? "").Trim();

        if (url.Length > 0)
            return (url, token, "本插件");

        // 按角色存，所以换角色读的是那个角色自己的 QQ 配置——不同角色可能是不同 bot 账号，这是对的。
        // 第二条是兜底：StorageFolderPath 若已含角色层、或 StorageKey 为空，靠第一条拼不出来。
        string[] candidates =
        [
            Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey,
                "Configuration", "Alife.Function.QChat.QChatService.json"),
            Path.Combine(AlifePath.StorageFolderPath,
                "Configuration", "Alife.Function.QChat.QChatService.json"),
        ];

        foreach (string path in candidates)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                string sharedUrl = document.RootElement.TryGetProperty("Url", out JsonElement u) ? u.GetString() ?? "" : "";
                string sharedToken = document.RootElement.TryGetProperty("Token", out JsonElement t) ? t.GetString() ?? "" : "";
                if (sharedUrl.Trim().Length > 0)
                    return (sharedUrl.Trim(), token.Length > 0 ? token : sharedToken.Trim(), "QQ 聊天插件");
            }
            catch (Exception)
            {
                // 这个候选读不了就试下一个。配置坏了也不该让发消息失败在读取配置上
            }
        }

        return ("ws://127.0.0.1:3001", token, "默认");
    }

    /// <summary>
    /// 建连并鉴权。
    /// token 必须在 ConnectAsync 之前写进请求头——连上之后 ClientWebSocket 不允许再改 Options。
    /// </summary>
    async Task ConnectOneBotAsync(ClientWebSocket connection, CancellationToken cancellationToken)
    {
        (string url, string token, _) = ResolveOneBot();
        if (token.Length > 0)
            connection.Options.SetRequestHeader("Authorization", "Bearer " + token);
        await connection.ConnectAsync(new Uri(url), cancellationToken);
    }


    // ---- 连接层 ----
    // 全部按实例持有：原来这些是 static，多角色时后一个角色会直接复用前一个角色的连接，
    // 用错 bot 账号；一个角色卸载还会把别人正在用的连接 Dispose 掉。
    readonly SemaphoreSlim sendLock = new(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pendingResponses = new();
    ClientWebSocket sharedConnection;
    TaskCompletionSource<bool> connectionReady = CreateReadySource();
    static readonly HttpClient sharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    bool receiveLoopStarted;

    // ---- 协议端适配 ----
    string providerName = "";
    bool providerResolved;
    string historyAnchorField = "message_seq";

    // ---- 报告状态 ----
    readonly ConcurrentDictionary<long, byte> generatingGroups = new();

    // 多个群同时到点时限并发跑：共用同一个模型实例，并发太高会互相挤，后面的容易写成敷衍的套话。
    // 每群本身已有 generatingGroups 去重，这里只限制总数，不再让所有群排成一条队。
    readonly SemaphoreSlim reportGate = new(2, 2);
    // 每群后台任务都会写这张表，普通 Dictionary 并发写会坏（甚至丢条目），和上面那个引出人的问题是同一类
    readonly ConcurrentDictionary<long, DateTime> lastReportTimes = new();
    // 定时补发状态落盘：重启后要知道每群上次成功发送的日期，否则错过窗口就一整天不发
    readonly object scheduleLock = new();
    string scheduleStatePath = "";
    // 失败后的重试节流（只在内存里，重启就重新给一次机会）
    readonly ConcurrentDictionary<long, DateTime> lastAutoAttempts = new();
    // 补发窗口变长后扫描会持续几小时，限制每分钟只检查一次，别每个 Tick 都去问协议端要群列表
    DateTime lastAutoScan = DateTime.MinValue;
    bool warnedBadAutoTime;
    bool warnedBadTheme;
    bool warnedBadFormat;
    bool warnedBadBrowser;
    string cachedBotNickname = "";
    long cachedBotId = 0;
    // 头像并发预取用：同一个缓存文件只让一个任务写，整体最多 6 张同时在途
    readonly ConcurrentDictionary<string, SemaphoreSlim> avatarLocks = new();
    readonly SemaphoreSlim avatarGate = new(6, 6);
    // 补发窗口里每 30 秒就会扫一次，群列表与群名都缓存几分钟，别把协议端当数据库查
    readonly object groupIdCacheLock = new();
    List<long> cachedScheduledGroupIds = new();
    DateTime scheduledGroupIdsAt = DateTime.MinValue;
    readonly ConcurrentDictionary<long, (string Name, DateTime At)> groupNameCache = new();
    // 人设文本按群现取现用，不再放字段：多群之间会互相覆盖
    // 每群一份的运行时数据（参与者表、群号）随 RunReportAsync 一路传参下去；
    // 以前挂在实例上，定时多群并跑时后一个群一 Clear 就把前一个群正在用的表清了

    string reportDirectory = "";
    string avatarDirectory = "";

    // ============================================================
    // 生命周期
    // ============================================================

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "生成群聊日报：统计群聊消息（总数/参与人数/活跃时段/活跃用户），LLM精选热门话题与金句，并用人设风格锐评，渲染成精美图片发回群里。",
            Explanation = "用户说『总结一下今天的群聊』『生成日报』或发送『/日报』时调用 GenerateDailyReport。生成是异步的：先返回已开始，完成后日报自动发到群里。同一群有冷却时间（配置控制），消息太少会提示不足。"
                + "只想看效果不发群用 daily_report_preview（返回图片/HTML 路径）；查定时与冷却用 daily_report_status；要重发先 daily_report_reset。"
        };
        functionCaller.RegisterHandler(handler,
            (Configuration?.DocumentAlwaysResident ?? false) ? DocumentMode.Explicit : DocumentMode.Implicit,
            cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        reportDirectory = Path.Combine(AlifePath.StorageFolderPath, "DailyReport", "reports");
        avatarDirectory = Path.Combine(AlifePath.StorageFolderPath, "DailyReport", "avatars");
        Directory.CreateDirectory(reportDirectory);
        Directory.CreateDirectory(avatarDirectory);
        // 定时状态按角色分开：Storage 是全局目录，两个角色共用一份会互相覆盖上次发送时间
        scheduleStatePath = ScheduleStateFilePath(AlifePath.StorageFolderPath, Character.StorageKey);
        LoadScheduleState();
        PruneReportFiles();
        cachedBotNickname = Configuration.BotNickname.Trim();
        ValidateConfiguration();
        StartReceiveLoop();
        // 启动后主动跑一次定时检查：宿主若只在有消息时才 Tick，安静的群会错过补发窗口
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), DestroyCancellationToken);
                await OnUpdate();
            }
            catch (OperationCanceledException)
            {
                // 插件卸载，正常退出
            }
            catch (Exception startupError)
            {
                logger.LogWarning("启动补发检查失败：{Message}", startupError.Message);
            }
        }, DestroyCancellationToken);
        logger.LogInformation("群聊日报插件已启动，输出格式 {Format}，主题 {Theme}",
            Configuration.OutputFormat, Configuration.Theme);
        return Task.CompletedTask;
    }

    static string ScheduleStateFilePath(string storageRoot, string storageKey)
    {
        string characterRoot = Path.Combine(storageRoot, storageKey.Replace('/', Path.DirectorySeparatorChar));
        return Path.Combine(characterRoot, "DailyReport", "schedule-state.json");
    }

    // catchUpMinutes=0 表示「只在到点那一分钟发」，不能要求秒级完全相等
    static bool IsWithinCatchUp(DateTime now, DateTime due, int catchUpMinutes)
    {
        double elapsedMinutes = (now - due).TotalMinutes;
        if (elapsedMinutes < 0)
        {
            return false;
        }
        if (catchUpMinutes <= 0)
        {
            return elapsedMinutes < 1;
        }
        return elapsedMinutes <= catchUpMinutes;
    }

    void ValidateConfiguration()
    {
        WarnRange("分析天数", Configuration.AnalysisDays, 1, 30);
        WarnRange("单次最大分析消息数", Configuration.MaxMessages, 10, 10000);
        WarnRange("最小消息数阈值", Configuration.MinMessages, 0, 10000);
        WarnRange("活跃时段窗口", Configuration.ActiveWindowMinutes, 1, 1440);
        WarnRange("定时补发窗口", Configuration.AutoReportCatchUpMinutes, 0, 1440);
        WarnRange("最少话题数", Configuration.MinTopics, 0, 10);
        WarnRange("最多话题数", Configuration.MaxTopics, 0, 20);
        WarnRange("金句数量", Configuration.QuoteCount, 0, 10);
        WarnRange("活跃用户数量", Configuration.TopUserCount, 0, 30);
        WarnRange("保留日报份数", Configuration.ReportKeepCount, 0, 1000);
        if (Configuration.CooldownHours < 0 || Configuration.CooldownHours > 168)
        {
            logger.LogWarning("日报冷却时间 {Value} 小时超出 0-168，按边界值处理", Configuration.CooldownHours);
        }
        string mode = Configuration.AnalysisWindowMode.Trim().ToLowerInvariant();
        if (mode.Length > 0 && mode != "rolling" && mode != "naturalday" && mode != "natural")
        {
            logger.LogWarning("日报时间范围口径 {Value} 不认识（只支持 rolling/naturalDay），按 rolling 处理", Configuration.AnalysisWindowMode);
        }
    }

    void WarnRange(string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            logger.LogWarning("日报配置「{Name}」{Value} 超出 {Min}-{Max}，运行时按边界值处理", name, value, min, max);
        }
    }

    protected override async Task OnUpdate()

    {
        if (Configuration.EnableAutoReport == false)
        {
            return;
        }
        if (TimeSpan.TryParseExact(Configuration.AutoReportTime, "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan scheduledTime) == false)
        {
            // 配置写错时不能一声不吭：原来静默 return，表现就是"定时日报永远不发"
            if (warnedBadAutoTime == false)
            {
                warnedBadAutoTime = true;
                logger.LogWarning("自动日报时间格式不对（{Value}），应为 HH:mm，自动日报不会触发", Configuration.AutoReportTime);
            }
            return;
        }
        warnedBadAutoTime = false;
        DateTime now = DateTime.Now;
        // 最近一次"应该发"的时间：今天还没到点就算昨天那次。
        // 这样进程在窗口内重启时，昨天错过的那次还能补上，而不是要等今天到点
        DateTime due = now.TimeOfDay >= scheduledTime ? now.Date + scheduledTime : now.Date.AddDays(-1) + scheduledTime;
        int catchUpMinutes = Math.Clamp(Configuration.AutoReportCatchUpMinutes, 0, 1440);
        if (IsWithinCatchUp(now, due, catchUpMinutes) == false)
        {
            return;
        }
        // 只按分钟级检查：补发窗口有好几小时，没必要每个 Tick 都跑一遍
        if ((now - lastAutoScan).TotalSeconds < 30)
        {
            return;
        }
        lastAutoScan = now;
        List<long> scheduledGroupIds = await ResolveScheduledGroupIds();
        bool scheduledExplicit = Configuration.ScheduledGroups.Trim().Length > 0;
        foreach (long groupId in scheduledGroupIds)
        {
            if (generatingGroups.ContainsKey(groupId))
            {
                continue;
            }
            // 这次该发的时间点已经发过 → 不重复发。
            // 只拦「到点后」或「到点前 30 分钟内」的发送：白天手动发的日报不该把晚上的定时吞掉
            if (lastReportTimes.TryGetValue(groupId, out DateTime lastSent)
                && (lastSent >= due || (due - lastSent).TotalMinutes < 30))
            {
                continue;
            }
            // 失败后 30 分钟内不重试，别把模型接口和群刷成事故现场
            if (lastAutoAttempts.TryGetValue(groupId, out DateTime attempted) && (now - attempted).TotalMinutes < 30)
            {
                continue;
            }
            // 定时排除名单：只拦定时生成，不影响手动
            if (SplitList(Configuration.DisabledGroups).Contains(groupId.ToString(CultureInfo.InvariantCulture)))
            {
                continue;
            }
            // 显式写进「定时开启日报的群组」就是明确意图，不再被「可使用日报的群组」二次过滤；
            // 两者都留空时才按「所有群」处理（此时才走权限名单）
            if (scheduledExplicit == false && IsGroupAllowed(groupId) == false)
            {
                continue;
            }
            // 定时不走调用冷却：冷却管的是手动刷屏；上面已经保证每个时间点只发一次
            if (TryStartReport(groupId, "system", due))
            {
                lastAutoAttempts[groupId] = now;
                LogDetail("定时日报开始生成：群 " + groupId + "（应发时间 " + due.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "）");
            }
        }
    }

    protected override async Task OnDestroy()
    {
        await sendLock.WaitAsync();
        try
        {
            sharedConnection?.Dispose();
            sharedConnection = null;
            pendingResponses.Clear();
            receiveLoopStarted = false;
            providerName = "";
            providerResolved = false;
        }
        finally
        {
            sendLock.Release();
        }
        logger.LogInformation("群聊日报插件已卸载");
    }

    void LoadScheduleState()
    {
        try
        {
            if (!File.Exists(scheduleStatePath))
            {
                return;
            }
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(scheduleStatePath));
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("lastReports", out JsonElement reports) && reports.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in reports.EnumerateObject())
                {
                    if (long.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long groupId) == false
                        || entry.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    if (DateTime.TryParse(entry.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime last))
                    {
                        lastReportTimes[groupId] = last;
                    }
                }
                LogDetail("定时状态已恢复：" + lastReportTimes.Count + " 个群有上次发送时间");
            }
        }
        catch (Exception stateError)
        {
            logger.LogWarning("定时状态读取失败（按未发处理）：{Message}", stateError.Message);
        }
    }

    void SaveScheduleState()
    {
        lock (scheduleLock)
        {
            try
            {
                JsonObject lastReports = new();
                foreach (KeyValuePair<long, DateTime> entry in lastReportTimes)
                {
                    lastReports[entry.Key.ToString(CultureInfo.InvariantCulture)] = entry.Value.ToString("o", CultureInfo.InvariantCulture);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(scheduleStatePath)!);
                WriteAtomicText(scheduleStatePath, new JsonObject { ["lastReports"] = lastReports }.ToJsonString());
            }
            catch (Exception stateError)
            {
                logger.LogWarning("定时状态写入失败（本次不影响发送）：{Message}", stateError.Message);
            }
        }
    }

    static void WriteAtomicText(string file, string text)
    {
        string temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, text, Encoding.UTF8);
            File.Move(temp, file, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                // [静默] 文件不存在或不可读时跳过
                try { File.Delete(temp); } catch { }
            }
        }
    }

    // 定时群组解析：显式配置优先；两者都留空时按原版语义“留空=所有群”，从协议端取全部群
    async Task<List<long>> ResolveScheduledGroupIds()
    {
        string raw = Configuration.ScheduledGroups.Trim();
        if (raw.Length == 0)
        {
            raw = Configuration.EnabledGroups;
        }
        if (raw.Trim().Length > 0)
        {
            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(text => long.TryParse(text, out long parsed) ? parsed : 0)
                .Where(id => id > 0)
                .ToList();
        }
        lock (groupIdCacheLock)
        {
            if (cachedScheduledGroupIds.Count > 0 && (DateTime.Now - scheduledGroupIdsAt).TotalMinutes < 5)
            {
                return new List<long>(cachedScheduledGroupIds);
            }
        }
        try
        {
            string response = await CallActionAsync("get_group_list", new JsonObject());
            JsonElement data = ParseActionResponse(response, "获取群列表");
            List<long> groupIds = new();
            foreach (JsonElement group in data.EnumerateArray())
            {
                long id = GetNumericField(group, "group_id");
                if (id > 0)
                {
                    groupIds.Add(id);
                }
            }
            if (groupIds.Count > 0)
            {
                lock (groupIdCacheLock)
                {
                    cachedScheduledGroupIds = groupIds;
                    scheduledGroupIdsAt = DateTime.Now;
                }
            }
            LogDetail("自动日报未配置群组，按所有群处理（" + groupIds.Count + " 个）");
            return groupIds;
        }
        catch (Exception listError)
        {
            logger.LogWarning("自动日报获取群列表失败：{Message}", listError.Message);
            return new List<long>();
        }
    }

    // ============================================================
    // OneBot 连接层
    // ============================================================

    static TaskCompletionSource<bool> CreateReadySource()
    {
        TaskCompletionSource<bool> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return source;
    }

    void StartReceiveLoop()
    {
        if (receiveLoopStarted)
        {
            return;
        }
        receiveLoopStarted = true;
        Task.Run(async () =>
        {
            while (DestroyCancellationToken.IsCancellationRequested == false)
            {
                try
                {
                    sharedConnection?.Dispose();
                    ClientWebSocket connection = new();
                    using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(10));
                    await ConnectOneBotAsync(connection, connectTimeout.Token);
                    sharedConnection = connection;
                    connectionReady.TrySetResult(true);
                    LogDetail("协议端已连接：" + ResolveOneBot().Url);
                    await ReceiveLoopCoreAsync(connection);
                }
                catch (Exception loopError)
                {
                    logger.LogWarning("协议端连接中断，3秒后重连：{Message}", loopError.Message);
                }
                connectionReady = CreateReadySource();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), DestroyCancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, DestroyCancellationToken);
    }

    async Task ReceiveLoopCoreAsync(ClientWebSocket connection)
    {
        byte[] receiveBuffer = new byte[1024 * 1024];
        while (DestroyCancellationToken.IsCancellationRequested == false)
        {
            MemoryStream messageStream = new();
            WebSocketReceiveResult receiveResult;
            do
            {
                receiveResult = await connection.ReceiveAsync(
                    new ArraySegment<byte>(receiveBuffer), DestroyCancellationToken);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    throw new Exception("协议端主动断开连接");
                }
                messageStream.Write(receiveBuffer, 0, receiveResult.Count);
            }
            while (receiveResult.EndOfMessage == false);

            string messageText = Encoding.UTF8.GetString(messageStream.ToArray());
            CompletePendingResponse(messageText);
        }
    }

    void CompletePendingResponse(string messageText)
    {
        JsonElement messageElement;
        try
        {
            using JsonDocument parsedMessage = JsonDocument.Parse(messageText);
            messageElement = parsedMessage.RootElement.Clone();
        }
        catch
        {
            return;
        }
        if (messageElement.TryGetProperty("echo", out JsonElement echoElement) && echoElement.ValueKind == JsonValueKind.String)
        {
            string echo = echoElement.GetString() ?? "";
            if (pendingResponses.TryRemove(echo, out TaskCompletionSource<string> waiter))
            {
                waiter.TrySetResult(messageText);
            }
        }
    }

    async Task<string> CallActionAsync(string action, JsonObject parameters)
    {
        await connectionReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
        string echo = Guid.NewGuid().ToString("N");
        TaskCompletionSource<string> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingResponses[echo] = waiter;
        try
        {
            await sendLock.WaitAsync();
            try
            {
                if (sharedConnection == null || sharedConnection.State != WebSocketState.Open)
                {
                    connectionReady = CreateReadySource();
                    sharedConnection?.Dispose();
                    ClientWebSocket connection = new();
                    using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(10));
                    await ConnectOneBotAsync(connection, connectTimeout.Token);
                    sharedConnection = connection;
                    connectionReady.TrySetResult(true);
                }

                JsonObject payload = new()
                {
                    ["action"] = action,
                    ["params"] = parameters,
                    ["echo"] = echo
                };
                byte[] payloadBytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
                using CancellationTokenSource sendTimeout = new(TimeSpan.FromSeconds(30));
                await sharedConnection.SendAsync(
                    new ArraySegment<byte>(payloadBytes), WebSocketMessageType.Text, true, sendTimeout.Token);
            }
            finally
            {
                sendLock.Release();
            }

            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            // 响应超时/发送失败/取消都要把等待条目摘掉，否则 pendingResponses 会越积越多
            pendingResponses.TryRemove(echo, out _);
        }
    }

    JsonElement ParseActionResponse(string responseText, string operationName)
    {
        using JsonDocument parsedResponse = JsonDocument.Parse(responseText);
        JsonElement root = parsedResponse.RootElement.Clone();
        string status = root.TryGetProperty("status", out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String
            ? statusElement.GetString() ?? ""
            : "";
        int retcode = root.TryGetProperty("retcode", out JsonElement retcodeElement) && retcodeElement.ValueKind == JsonValueKind.Number
            ? retcodeElement.GetInt32()
            : -1;
        bool succeeded = status == "ok" || status == "async" || retcode == 0;
        if (succeeded == false)
        {
            // SnowLuma 的失败包只填 wording（LLOneBot/NapCat 两个键都填同一个值），只读 message 在 SnowLuma 下永远是「未知错误」
            string detail = GetStringField(root, "message");
            if (detail.Length == 0)
            {
                detail = GetStringField(root, "wording");
            }
            if (detail.Length == 0)
            {
                detail = "未知错误";
            }
            // 1404 = 动作名在该协议端不存在，光给错误码用户无从判断是插件坏了还是协议端没这接口
            if (retcode == 1404)
            {
                detail += "（" + (providerName.Length > 0 ? providerName : "该协议端")
                    + " 未实现该接口，请检查协议端设置或更换支持的协议端）";
            }
            throw new Exception(operationName + "失败：" + detail);
        }
        return root.TryGetProperty("data", out JsonElement dataElement) ? dataElement.Clone() : JsonDocument.Parse("null").RootElement.Clone();
    }

    // ============================================================
    // AI 函数
    // ============================================================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("生成指定群的群聊日报（异步）：统计消息总数/参与人数/活跃时段/活跃用户，精选话题与金句并带人设锐评，完成后自动把日报发到群里。用户说『总结今天的群聊』『生成日报』或发『/日报』时调用")]
    public Task GenerateDailyReport(
        [Description("QQ群号")] long groupId,
        [Description("发起请求的用户QQ号，用于冷却豁免判定，可留空")] long? operatorId = null)
    {
        if (IsGroupAllowed(groupId) == false)
        {
            interactor.Poke("❌ 该群未启用日报功能");
            return Task.CompletedTask;
        }
        if (generatingGroups.ContainsKey(groupId))
        {
            interactor.Poke("📊 该群的日报正在生成中，请稍候~");
            return Task.CompletedTask;
        }
        if (CheckCooldown(groupId, operatorId ?? 0))
        {
            double remainingHours = (lastReportTimes[groupId].AddHours(Configuration.CooldownHours) - DateTime.Now).TotalHours;
            interactor.Poke("⏳ 日报生成冷却中，剩余 " + remainingHours.ToString("F1", CultureInfo.InvariantCulture) + " 小时");
            return Task.CompletedTask;
        }
        bool started = TryStartReport(groupId, operatorId?.ToString() ?? "system");
        if (started)
        {
            interactor.Poke("📊 正在生成 " + groupId + " 的群聊日报，请稍候...（完成后会自动发到群里）");
        }
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot, "daily_report_preview")]
    [Description("只生成日报预览、不发到群里（异步）：图片模式返回 png/html 路径，文字模式直接返回文字稿；不受冷却限制，也不更新定时记录")]
    public Task DailyReportPreview(
        [Description("QQ群号")] long groupId,
        [Description("自然日口径下看哪一天，格式 yyyy-MM-dd，留空=今天；滚动窗口忽略")] string day = "")
    {
        if (IsGroupAllowed(groupId) == false)
        {
            interactor.Poke("❌ 该群未启用日报功能");
            return Task.CompletedTask;
        }
        if (generatingGroups.ContainsKey(groupId))
        {
            interactor.Poke("📊 该群的日报正在生成中，请稍候~");
            return Task.CompletedTask;
        }
        DateTime? previewDay = null;
        if (day.Trim().Length > 0)
        {
            if (DateTime.TryParse(day.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed) == false)
            {
                interactor.Poke("❌ daily_report_preview 的 day 格式不对，要 yyyy-MM-dd：" + day);
                return Task.CompletedTask;
            }
            previewDay = parsed.Date;
        }
        if (TryStartReport(groupId, "preview", previewDay, preview: true))
        {
            interactor.Poke("📝 正在生成 " + groupId + " 的日报预览（不会发到群里）...");
        }
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot, "daily_report_status")]
    [Description("查看日报的定时与冷却状态：定时开关/时间/补发窗口/窗口口径、哪些群有发送记录、某群的冷却剩余与是否正在生成")]
    public Task DailyReportStatus(
        [Description("QQ群号；0 = 汇总所有有记录的群")] long groupId = 0)
    {
        List<string> lines = new()
        {
            "定时日报：" + (Configuration.EnableAutoReport ? "开" : "关")
                + "｜时间 " + Configuration.AutoReportTime
                + "｜补发 " + Math.Clamp(Configuration.AutoReportCatchUpMinutes, 0, 1440) + " 分钟"
                + "｜窗口 " + (IsNaturalDayWindow() ? "自然日" : "滚动 " + Math.Clamp(Configuration.AnalysisDays, 1, 30) + " 天")
        };
        string scheduled = Configuration.ScheduledGroups.Trim();
        if (scheduled.Length > 0)
        {
            lines.Add("定时群组：" + scheduled);
        }
        else if (Configuration.EnabledGroups.Trim().Length > 0)
        {
            lines.Add("定时群组：跟随可使用群组 " + Configuration.EnabledGroups.Trim());
        }
        else
        {
            lines.Add("定时群组：所有群（运行时从协议端取列表）");
        }
        if (Configuration.DisabledGroups.Trim().Length > 0)
        {
            lines.Add("定时排除：" + Configuration.DisabledGroups.Trim());
        }
        if (groupId > 0)
        {
            string last = lastReportTimes.TryGetValue(groupId, out DateTime sent)
                ? sent.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "无";
            double remain = 0;
            if (lastReportTimes.TryGetValue(groupId, out DateTime lastSent))
            {
                remain = Math.Max(0.0, (lastSent.AddHours(Configuration.CooldownHours) - DateTime.Now).TotalHours);
            }
            lines.Add("群 " + groupId + "：上次发送 " + last
                + "｜冷却剩余 " + remain.ToString("F1", CultureInfo.InvariantCulture) + " 小时"
                + "｜" + (generatingGroups.ContainsKey(groupId) ? "正在生成" : "空闲"));
            if (lastAutoAttempts.TryGetValue(groupId, out DateTime attempted))
            {
                lines.Add("上次定时尝试：" + attempted.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            }
        }
        else
        {
            string records = lastReportTimes.Count == 0
                ? "无"
                : string.Join("、", lastReportTimes.OrderBy(entry => entry.Key)
                    .Select(entry => entry.Key + "@" + entry.Value.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)));
            lines.Add("有发送记录的群：" + records);
            string running = generatingGroups.Count == 0
                ? "无"
                : string.Join("、", generatingGroups.Keys.OrderBy(id => id));
            lines.Add("正在生成：" + running);
        }
        interactor.Poke(string.Join("\n", lines));
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot, "daily_report_reset")]
    [Description("清掉某群的日报冷却与定时发送记录（写操作）：下一次手动/定时都能重新生成，不会触发重复补发")]
    public Task DailyReportReset(
        [Description("QQ群号")] long groupId,
        [Description("是否同时清掉失败重试节流，默认 true")] bool clearAttempts = true)
    {
        bool removedSent = lastReportTimes.TryRemove(groupId, out _);
        bool removedAttempt = clearAttempts && lastAutoAttempts.TryRemove(groupId, out _);
        SaveScheduleState();
        interactor.Poke("已清掉群 " + groupId + " 的日报记录"
            + (removedSent ? "（冷却与上次发送时间）" : "")
            + (removedAttempt ? "（失败重试节流）" : "")
            + "。下次可以重新生成");
        return Task.CompletedTask;
    }
    bool CheckCooldown(long groupId, long operatorId)
    {
        if (lastReportTimes.TryGetValue(groupId, out DateTime lastTime) == false)
        {
            return false;
        }
        if ((DateTime.Now - lastTime).TotalHours < Configuration.CooldownHours)
        {
            List<string> whitelist = SplitList(Configuration.WhitelistUsers);
            if (Configuration.WhitelistExemptCooldown && whitelist.Contains(operatorId.ToString()))
            {
                return false;
            }
            return true;
        }
        return false;
    }

    bool IsGroupAllowed(long groupId)
    {
        string raw = Configuration.EnabledGroups.Trim();
        if (raw.Length == 0)
        {
            return true;
        }
        return SplitList(raw).Contains(groupId.ToString(CultureInfo.InvariantCulture));
    }

    bool TryStartReport(long groupId, string operatorId, DateTime? reportDay = null, bool preview = false)
    {
        if (generatingGroups.TryAdd(groupId, 0) == false)
        {
            return false;
        }
        _ = Task.Run(() => RunReportAsync(groupId, reportDay, preview), DestroyCancellationToken);
        return true;
    }

    // ============================================================
    // 日报生成主流程（后台）
    // ============================================================

    sealed class ChatMessageRecord
    {
        public long Time { get; set; }
        public long UserId { get; set; }
        public string Nickname { get; set; } = "";
        public string Text { get; set; } = "";
        public string Key { get; set; } = "";
        public bool HasText { get; set; }
    }

    sealed class AnalysisResult
    {
        public List<ReportTopic> Topics { get; set; } = new();
        public List<ReportQuote> Quotes { get; set; } = new();
        public string Comment { get; set; } = "";
    }

    sealed class ReportTopic
    {
        public string Title { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Comment { get; set; } = "";
        public long Uid { get; set; }
    }

    sealed class ReportQuote
    {
        public string Who { get; set; } = "";
        public long Uid { get; set; }
        public string Text { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    sealed class UserStat
    {
        public long UserId { get; set; }
        public string Nickname { get; set; } = "";
        public int Count { get; set; }
    }

    async Task RunReportAsync(long groupId, DateTime? reportDay = null, bool preview = false)
    {
        await reportGate.WaitAsync(DestroyCancellationToken);
        try
        {
            // 1. 拉取群聊历史
            HistoryFetchResult fetch = await CollectHistoryAsync(groupId, reportDay);
            List<ChatMessageRecord> messages = fetch.Messages;
            // 统计里排除机器人自己：它发言多时会自己占活跃用户榜，也可能把自己选进金句
            await ResolveBotNicknameAsync();
            if (cachedBotId > 0)
            {
                messages = messages.Where(message => message.UserId != cachedBotId).ToList();
            }
            int minMessages = Math.Clamp(Configuration.MinMessages, 1, 10000);
            int analysisDays = Math.Clamp(Configuration.AnalysisDays, 1, 30);
            if (messages.Count < minMessages)
            {
                interactor.Poke("📊 群聊日报：最近 " + analysisDays + " 天消息数 (" + messages.Count + ") 不足 "
                    + minMessages + " 条，本次不生成"
                    + (messages.Count == 0 ? "。拉取情况：" + fetch.Diagnosis : ""));
                return;
            }
            LogDetail("拉取到 " + messages.Count + " 条消息，开始统计与分析");

            // 2. 零Token统计
            DateTime windowStart = DateTimeOffset.FromUnixTimeSeconds(messages.Min(message => message.Time)).LocalDateTime;
            DateTime windowEnd = DateTimeOffset.FromUnixTimeSeconds(messages.Max(message => message.Time)).LocalDateTime;
            int participants = messages.Select(message => message.UserId).Distinct().Count();
            int topUserCount = Math.Clamp(Configuration.TopUserCount, 0, 30);
            List<UserStat> topUsers = messages
                .GroupBy(message => message.UserId)
                .Select(group => new UserStat
                {
                    UserId = group.Key,
                    Nickname = group.First().Nickname,
                    Count = group.Count()
                })
                .OrderByDescending(stat => stat.Count)
                .Take(topUserCount)
                .ToList();
            string activeRange = ComputeActiveWindow(messages, Math.Clamp(Configuration.ActiveWindowMinutes, 1, 1440));
            Dictionary<long, string> userNames = new();
            foreach (ChatMessageRecord message in messages)
            {
                userNames.TryAdd(message.UserId, message.Nickname);
            }

            // 3. LLM 分析（话题/金句/锐评一次完成，回复自带当前人设）
            AnalysisResult analysis = await AnalyzeWithLLMAsync(groupId, messages);

            // 4. 生成并发送
            string groupName = await GetGroupNameAsync(groupId);
            string botName = await ResolveBotNicknameAsync();
            if (preview)
            {
                if (IsTextOutput())
                {
                    string textReport = BuildTextReport(groupName, messages.Count, participants, activeRange,
                        topUsers, analysis, botName, windowStart, windowEnd);
                    interactor.Poke("📝 日报预览（未发送）：\n" + textReport);
                    return;
                }
                RenderedReport rendered = await RenderImageReportAsync(groupId, groupName, messages.Count, participants,
                    activeRange, topUsers, analysis, botName, windowStart, windowEnd, userNames);
                if (rendered.Ok)
                {
                    interactor.Poke("📝 日报预览已生成（未发送）：\n图片：" + rendered.ImagePath + "\nHTML：" + rendered.HtmlPath);
                }
                else
                {
                    interactor.Poke("📝 日报预览渲染失败（未发送）：" + rendered.Error
                        + (rendered.HtmlPath.Length > 0 ? "\nHTML：" + rendered.HtmlPath : ""));
                }
                return;
            }
            if (IsTextOutput())
            {
                string textReport = BuildTextReport(groupName, messages.Count, participants, activeRange,
                    topUsers, analysis, botName, windowStart, windowEnd);
                await SendGroupTextAsync(groupId, textReport);
            }
            else
            {
                bool imageSent = await RenderAndSendImageAsync(groupId, groupName, messages.Count, participants,
                    activeRange, topUsers, analysis, botName, windowStart, windowEnd, userNames);
                if (imageSent == false)
                {
                    string textReport = BuildTextReport(groupName, messages.Count, participants, activeRange,
                        topUsers, analysis, botName, windowStart, windowEnd);
                    await SendGroupTextAsync(groupId, "（图片渲染不可用，已改用文字日报，详情见插件日志）\n" + textReport);
                }
            }

            // 只有真正发出去才算数：发送失败/异常不会走到这里，重启后仍会补发
            lastReportTimes[groupId] = DateTime.Now;
            SaveScheduleState();
            interactor.Poke("✅ 群 " + groupId + " 的群聊日报已生成并发送到群里");
        }
        catch (Exception reportError)
        {
            logger.LogError(reportError, "日报生成失败");
            interactor.Poke("❌ 日报生成失败：" + reportError.Message);
        }
        finally
        {
            generatingGroups.TryRemove(groupId, out _);
            reportGate.Release();
        }
    }

    sealed class HistoryFetchResult
    {
        public List<ChatMessageRecord> Messages { get; set; } = new();
        public int RawItemCount { get; set; }
        public int FilteredItemCount { get; set; }
        public int Pages { get; set; }
        public bool WindowCut { get; set; }
        public bool Partial { get; set; }
        public string Diagnosis { get; set; } = "";
    }

    // 分页拉取群历史消息（最多 MaxMessages 条、按滚动窗口或自然日窗口）。
    // 三端的翻页锚点不是同一个字段：NapCat 与 LLOneBot 只读 message_seq，SnowLuma 只读 message_id，
    // 且 SnowLuma 的 message_id 是带符号哈希（可为负、也不代表时间顺序）。
    // 所以每页都用 message_seq 定位最旧的那条消息，再把这条消息自己的 seq 或 message_id 发出去当下一页锚点，
    // 绝不能把两个字段的值混用。发错字段不报错，但有两种完全不同的表现（都在 SnowLuma/LLOneBot 源码里核对过）：
    // 认不出的锚点直接回空列表，被忽略的锚点回最新那一页——所以“空”和“没往更旧走”都得当成锚点失效。
    async Task<HistoryFetchResult> CollectHistoryAsync(long groupId, DateTime? reportDay = null)
    {
        HistoryFetchResult result = new();
        string anchorField = await ResolveHistoryAnchorFieldAsync();
        int analysisDays = Math.Clamp(Configuration.AnalysisDays, 1, 30);
        int maxMessages = Math.Clamp(Configuration.MaxMessages, 10, 10000);
        bool naturalDay = IsNaturalDayWindow();
        DateTime now = DateTime.Now;
        (DateTime windowStart, DateTime windowEnd) = HistoryWindow(now, analysisDays, naturalDay, reportDay);
        Dictionary<string, ChatMessageRecord> collected = new();
        long? anchor = null;
        long? lastOldestSeq = null;
        long lastOldestMessageId = 0;
        string firstRawSample = "";
        string failureNote = "";

        for (int page = 0; page < 25; page++)
        {
            List<JsonElement> pageMessages;
            try
            {
                pageMessages = await FetchHistoryPageWithRetryAsync(groupId, anchor, anchorField);
            }
            catch (Exception pageError)
            {
                // 已经拉到的消息比一个都没有强：别让第三页的一次抖动把整张日报作废
                result.Partial = true;
                failureNote = "第 " + (result.Pages + 1) + " 页拉取失败：" + pageError.Message + "（已用拉到的消息继续）";
                logger.LogWarning("日报历史第 {Page} 页拉取失败，用已拉到的 {Count} 条继续：{Message}",
                    result.Pages + 1, collected.Count, pageError.Message);
                if (collected.Count == 0)
                {
                    throw;
                }
                break;
            }
            bool hasOldest = TryFindOldest(pageMessages, out long oldestSeq, out long oldestMessageId);

            // 翻不动的两种表现：回空列表，或最旧一条没比上一页更旧（等于又给了最新那一页）
            bool anchorFailed = pageMessages.Count == 0
                || (hasOldest && lastOldestSeq.HasValue && oldestSeq >= lastOldestSeq.Value);

            // 认不出协议端时锚点字段靠试：换另一种字段，并用上一页最旧那条消息在该字段下的取值重发，
            // 只有真翻到更旧的消息才算猜对（历史见底时另一种锚点会回最新那一页，不能当成猜对）
            if (anchorFailed && anchor.HasValue && IsAnchorPinned() == false)
            {
                string alternate = anchorField == "message_seq" ? "message_id" : "message_seq";
                long? alternateAnchor = alternate == "message_seq"
                    ? lastOldestSeq
                    : (lastOldestMessageId != 0 ? lastOldestMessageId : null);
                if (alternateAnchor.HasValue)
                {
                    List<JsonElement> retried;
                    try
                    {
                        retried = await FetchHistoryPageWithRetryAsync(groupId, alternateAnchor, alternate);
                    }
                    catch (Exception)
                    {
                        retried = new List<JsonElement>();
                    }
                    if (retried.Count > 0 && TryFindOldest(retried, out long retriedOldest, out long retriedMessageId)
                        && (lastOldestSeq.HasValue == false || retriedOldest < lastOldestSeq.Value))
                    {
                        anchorField = alternate;
                        anchorFailed = false;
                        pageMessages = retried;
                        oldestSeq = retriedOldest;
                        oldestMessageId = retriedMessageId;
                        hasOldest = true;
                    }
                }
            }
            if (anchorFailed)
            {
                break;
            }
            result.Pages++;
            if (firstRawSample.Length == 0)
            {
                firstRawSample = pageMessages[0].GetRawText();
            }
            result.RawItemCount += pageMessages.Count;

            foreach (JsonElement item in pageMessages)
            {
                ChatMessageRecord record = ToChatMessageRecord(item);
                if (record == null)
                {
                    result.FilteredItemCount++;
                    continue;
                }
                if (record.Time > 0)
                {
                    DateTime messageTime = DateTimeOffset.FromUnixTimeSeconds(record.Time).LocalDateTime;
                    // 比窗口更老：后面只会更老，直接收工；比自然日窗口新（补发时第一页是“今天”的消息）：
                    // 跳过但继续往回翻，否则补发昨天的日报会一条都取不到
                    if (messageTime < windowStart)
                    {
                        result.WindowCut = true;
                        continue;
                    }
                    if (naturalDay && messageTime >= windowEnd)
                    {
                        continue;
                    }
                }
                // 三端源码核对过历史条目都带 time，真遇到缺时间字段的（段类型异常/未来版本）也不该整条丢掉，照收
                collected[record.Key] = record;
            }

            if (collected.Count >= maxMessages || result.WindowCut)
            {
                break;
            }
            if (hasOldest == false)
            {
                logger.LogWarning("[日报] 协议端历史消息里没有 message_seq，无法定位翻页锚点");
                break;
            }
            lastOldestSeq = oldestSeq;
            lastOldestMessageId = oldestMessageId;
            long nextAnchor = anchorField == "message_id" ? oldestMessageId : oldestSeq;
            if (nextAnchor == 0)
            {
                logger.LogWarning("[日报] 最旧消息缺少 {Anchor} 字段，无法继续翻页", anchorField);
                break;
            }
            anchor = nextAnchor;
        }

        result.Messages = collected.Values
            .OrderByDescending(record => record.Time)
            .Take(maxMessages)
            .ToList();

        string providerLabel = providerName.Length > 0
            ? providerName
            : (IsAnchorPinned() ? Configuration.ApiProvider.Trim() : "未识别");
        result.Diagnosis = "协议端 " + providerLabel + "，翻页锚点 " + anchorField
            + "，翻到 " + result.Pages + " 页，原始条目 " + result.RawItemCount + " 条，结构不符被过滤 " + result.FilteredItemCount + " 条";
        if (naturalDay)
        {
            result.Diagnosis += "，自然日窗口 " + windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + " 至 " + windowEnd.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if (failureNote.Length > 0)
        {
            result.Diagnosis += "，" + failureNote;
        }
        if (result.Pages == 1 && result.WindowCut == false && anchor.HasValue)
        {
            // 只拿到最新一页时，说清是锚点没被接受还是历史本来就到底了，否则看起来和“抓不到”一样
            result.Diagnosis += "，只翻到第一页（锚点没让协议端翻动）";
        }
        if (result.Pages == 0)
        {
            // 不带锚点的第一页就空，和翻页无关：SnowLuma 只认本地存过这个群的消息
            result.Diagnosis += "，第一页（不带锚点）就空——协议端没有该群的任何历史";
        }
        if (IsAnchorPinned() == false && result.Pages > 1)
        {
            // 试探出来且真翻得动的锚点才记下来，只翻到一页说明还没试对，别把错的字段固定住
            historyAnchorField = anchorField;
        }
        if (result.Messages.Count == 0)
        {
            // 抓不到又不报错最难定位；但群号和原始聊天内容不该进 Info 级日志，放 Debug 供排查
            logger.LogDebug("[日报] 群 {GroupId} 未取到可用历史消息：{Diagnosis}", groupId, result.Diagnosis);
            if (firstRawSample.Length > 0)
            {
                logger.LogDebug("[日报] 首条原始返回：{Sample}",
                    firstRawSample.Length > 120 ? firstRawSample[..120] : firstRawSample);
            }
        }
        return result;
    }

    bool IsNaturalDayWindow()
    {
        string mode = Configuration.AnalysisWindowMode.Trim().ToLowerInvariant();
        return mode == "naturalday" || mode == "natural";
    }

    // 滚动窗口：最近 N×24 小时；自然日窗口：目标日往前数 N 个完整自然日
    static (DateTime Start, DateTime End) HistoryWindow(DateTime now, int days, bool naturalDay, DateTime? reportDay)
    {
        int clampedDays = Math.Clamp(days, 1, 30);
        if (naturalDay == false)
        {
            return (now.AddDays(-clampedDays), DateTime.MaxValue);
        }
        DateTime day = (reportDay ?? now).Date;
        return (day.AddDays(-(clampedDays - 1)), day.AddDays(1));
    }

    async Task<List<JsonElement>> FetchHistoryPageWithRetryAsync(long groupId, long? anchor, string anchorField)
    {
        Exception lastError = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await FetchHistoryPageAsync(groupId, anchor, anchorField);
            }
            catch (OperationCanceledException) when (DestroyCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception pageError)
            {
                lastError = pageError;
                if (attempt == 0)
                {
                    await Task.Delay(200, DestroyCancellationToken);
                }
            }
        }
        throw lastError ?? new Exception("获取群消息历史失败");
    }

    async Task<List<JsonElement>> FetchHistoryPageAsync(long groupId, long? anchor, string anchorField)
    {
        JsonObject parameters = new()
        {
            ["group_id"] = groupId,
            ["count"] = 100
        };
        if (anchor.HasValue)
        {
            parameters[anchorField] = anchor.Value;
        }
        string response = await CallActionAsync("get_group_msg_history", parameters);
        JsonElement data = ParseActionResponse(response, "获取群消息历史");
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("messages", out JsonElement messagesElement)
            && messagesElement.ValueKind == JsonValueKind.Array)
        {
            return messagesElement.EnumerateArray().Select(item => item.Clone()).ToList();
        }
        if (data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().Select(item => item.Clone()).ToList();
        }
        return new List<JsonElement>();
    }

    // 用 message_seq 定位本页最旧的消息，并取出这条消息自己的 message_id。
    // 不能直接拿 message_id 比大小：SnowLuma 的 message_id 是带符号哈希，跟时间顺序无关。
    static bool TryFindOldest(List<JsonElement> items, out long oldestSeq, out long oldestMessageId)
    {
        oldestSeq = 0;
        oldestMessageId = 0;
        bool found = false;
        foreach (JsonElement item in items)
        {
            if (TryGetNumericField(item, "message_seq", out long itemSeq)
                && (found == false || itemSeq < oldestSeq))
            {
                found = true;
                oldestSeq = itemSeq;
                TryGetNumericField(item, "message_id", out oldestMessageId);
            }
        }
        return found;
    }

    // 历史翻页该发哪个锚点字段：配置写死的优先，其次按 get_version_info 认，都认不出先按 message_seq 试探
    async Task<string> ResolveHistoryAnchorFieldAsync()
    {
        string configured = Configuration.ApiProvider.Trim().ToLowerInvariant();
        if (configured != "auto" && configured.Length > 0)
        {
            return AnchorFieldOfProtocol(configured);
        }
        if (providerResolved == false)
        {
            providerResolved = true;
            try
            {
                string response = await CallActionAsync("get_version_info", new JsonObject());
                JsonElement data = ParseActionResponse(response, "获取协议端版本");
                // 实测 LLOneBot 只回 app_name，go-cqhttp 系才回 name/vendor，两种都取
                providerName = (GetStringField(data, "app_name") + " " + GetStringField(data, "name")
                    + " " + GetStringField(data, "vendor")).Trim();
                LogDetail("识别到协议端：" + providerName);
            }
            catch (Exception versionError)
            {
                LogDetail("识别协议端失败，翻页锚点改用试探：" + versionError.Message);
            }
        }
        string detected = AnchorFieldOfProtocol(providerName.ToLowerInvariant());
        return detected.Length > 0 ? detected : historyAnchorField;
    }

    static string AnchorFieldOfProtocol(string provider)
    {
        if (provider.Contains("snowluma") || provider.Contains("lagrange"))
        {
            return "message_id";
        }
        if (provider.Contains("napcat") || provider.Contains("llonebot"))
        {
            return "message_seq";
        }
        return "";
    }

    bool IsAnchorPinned()
    {
        string configured = Configuration.ApiProvider.Trim().ToLowerInvariant();
        return configured != "auto" && configured.Length > 0;
    }

    static ChatMessageRecord ToChatMessageRecord(JsonElement item)
    {
        // SnowLuma 等协议端的历史消息可能缺 post_type：只在明确非 message 时才跳过
        string postType = GetStringField(item, "post_type");
        if (postType.Length > 0 && postType != "message")
        {
            return null;
        }
        long userId = GetNumericField(item, "user_id");
        if (userId == 0)
        {
            return null;
        }
        string text = ExtractMessageText(item).Trim();
        JsonElement sender = item.TryGetProperty("sender", out JsonElement senderElement) && senderElement.ValueKind == JsonValueKind.Object
            ? senderElement
            : JsonDocument.Parse("{}").RootElement.Clone();
        string card = GetStringField(sender, "card");
        string nickname = GetStringField(sender, "nickname");
        long time = GetNumericField(item, "time");
        string key;
        if (TryGetNumericField(item, "message_seq", out long seq))
        {
            key = "s:" + seq.ToString(CultureInfo.InvariantCulture);
        }
        else if (TryGetNumericField(item, "message_id", out long messageId))
        {
            key = "m:" + messageId.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            // 没有稳定 id 时退回内容指纹；同一秒连发两条完全相同的消息会合并，属于可接受损失
            key = userId.ToString(CultureInfo.InvariantCulture) + "_" + time.ToString(CultureInfo.InvariantCulture)
                + "_" + text.GetHashCode();
        }
        return new ChatMessageRecord
        {
            Time = time,
            UserId = userId,
            Nickname = card.Length > 0 ? card : (nickname.Length > 0 ? nickname : userId.ToString()),
            Text = text,
            Key = key,
            HasText = text.Length > 0
        };
    }

    // 消息内容提取：段数组取text段；纯字符串剥掉CQ码
    static string ExtractMessageText(JsonElement item)
    {
        if (item.TryGetProperty("message", out JsonElement messageElement) == false)
        {
            return "";
        }
        if (messageElement.ValueKind == JsonValueKind.Array)
        {
            StringBuilder builder = new();
            foreach (JsonElement segment in messageElement.EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Object
                    && GetStringField(segment, "type") == "text"
                    && segment.TryGetProperty("data", out JsonElement segmentData)
                    && segmentData.ValueKind == JsonValueKind.Object)
                {
                    builder.Append(GetStringField(segmentData, "text"));
                }
            }
            return builder.ToString();
        }
        if (messageElement.ValueKind == JsonValueKind.String)
        {
            string raw = messageElement.GetString() ?? "";
            int cqIndex = raw.IndexOf("[CQ:", StringComparison.Ordinal);
            while (cqIndex >= 0)
            {
                int cqEnd = raw.IndexOf(']', cqIndex);
                if (cqEnd < 0)
                {
                    break;
                }
                raw = raw.Remove(cqIndex, cqEnd - cqIndex + 1);
                cqIndex = raw.IndexOf("[CQ:", StringComparison.Ordinal);
            }
            return raw;
        }
        return "";
    }

    // 滑动窗口找最活跃时段，输出 HH:mm-HH:mm
    static string ComputeActiveWindow(List<ChatMessageRecord> messages, int windowMinutes)
    {
        if (messages.Count == 0)
        {
            return "?";
        }
        List<long> sortedTimes = messages.Select(message => message.Time).OrderBy(time => time).ToList();
        long windowSeconds = Math.Clamp(windowMinutes, 1, 1440) * 60L;
        int bestCount = 0;
        long bestStart = sortedTimes[0];
        int left = 0;
        for (int right = 0; right < sortedTimes.Count; right++)
        {
            while (sortedTimes[right] - sortedTimes[left] > windowSeconds)
            {
                left++;
            }
            int windowCount = right - left + 1;
            if (windowCount > bestCount)
            {
                bestCount = windowCount;
                bestStart = sortedTimes[left];
            }
        }
        // 窗口结束时间不要越过最后一条消息；只有一条时至少给一分钟，避免显示成「23:50-23:50」
        long lastTime = sortedTimes[^1];
        long endSeconds = Math.Min(bestStart + windowSeconds, Math.Max(lastTime, bestStart + 60));
        DateTime startLocal = DateTimeOffset.FromUnixTimeSeconds(bestStart).LocalDateTime;
        DateTime endLocal = DateTimeOffset.FromUnixTimeSeconds(endSeconds).LocalDateTime;
        return startLocal.ToString("HH:mm", CultureInfo.InvariantCulture) + "-"
            + endLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    // ============================================================
}
