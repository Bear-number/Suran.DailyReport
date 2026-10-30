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
    // ---- 基础设置 ----
    [DisplayName("基础·协议端地址")]
    [Description("OneBot正向WebSocket地址，默认 ws://127.0.0.1:3001")]
    public string OneBotWsUrl { get; set; } = "ws://127.0.0.1:3001";

    [DisplayName("基础·协议端类型")]
    [Description("auto=自动识别（默认）/ napcat / llonebot / snowluma。影响历史消息翻页锚点：NapCat与LLOneBot只认message_seq，SnowLuma只认message_id，认不出时两种锚点都会试")]
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

    [DisplayName("分析·最小消息数阈值")]
    [Description("少于该值不生成日报")]
    public int MinMessages { get; set; } = 10;

    [DisplayName("分析·启用自动日报")]
    [Description("开启后每天在指定时间自动生成")]
    public bool EnableAutoReport { get; set; } = true;

    [DisplayName("分析·自动分析时间")]
    [Description("每天自动生成日报的时间，格式 HH:mm，默认 23:59")]
    public string AutoReportTime { get; set; } = "23:59";

    [DisplayName("分析·活跃时段窗口(分钟)")]
    [Description("滑动窗口统计最活跃时段的窗口宽度")]
    public int ActiveWindowMinutes { get; set; } = 90;

    // ---- 内容设置 ----
    [DisplayName("内容·启用AI锐评")]
    [Description("日报末尾增加人设风格的点评")]
    public bool EnableComment { get; set; } = true;

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

    [DisplayName("输出·保留最近几份日报文件")]
    [Description("每次生成都往存储目录写一份 .html 与 .png。这里是最多保留的「份数」（一份=同批的 html+png），超出的按时间老的删；0=不清理")]
    public int ReportKeepCount { get; set; } = 30;
}

[Module("群聊日报",
    "图片式智能群聊日报：从协议端拉取群聊记录做统计分析（消息数/参与人数/活跃时段/活跃用户），LLM精选话题金句并带人设锐评，渲染成5套主题的精美图片发回群里",
    defaultCategory: "苏染的工具")]
public class GroupDailyReportModule(
    XmlFunctionCaller functionCaller,
    ILogger<GroupDailyReportModule> logger,
    Interactor<GroupDailyReportModule> interactor
) : ChatBehaviour, IConfigurable<DailyReportConfig>
{
    public DailyReportConfig Configuration { get; set; } = null!;

    // ---- 连接层 ----
    static readonly SemaphoreSlim sendLock = new(1, 1);
    static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pendingResponses = new();
    static ClientWebSocket? sharedConnection;
    static TaskCompletionSource<bool> connectionReady = CreateReadySource();
    static readonly HttpClient sharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    static bool receiveLoopStarted;

    // ---- 协议端适配 ----
    static string providerName = "";
    static bool providerResolved;
    static string historyAnchorField = "message_seq";

    // ---- 报告状态 ----
    readonly ConcurrentDictionary<long, byte> generatingGroups = new();
    // 每群后台任务都会写这张表，普通 Dictionary 并发写会坏（甚至丢条目），和上面那个引出人的问题是同一类
    readonly ConcurrentDictionary<long, DateTime> lastReportTimes = new();
    DateTime lastAutoFiredDate = DateTime.MinValue;
    string cachedBotNickname = "";
    long cachedBotId = 0;
    string personaStyle = "";
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
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        reportDirectory = Path.Combine(AlifePath.StorageFolderPath, "DailyReport", "reports");
        avatarDirectory = Path.Combine(AlifePath.StorageFolderPath, "DailyReport", "avatars");
        Directory.CreateDirectory(reportDirectory);
        Directory.CreateDirectory(avatarDirectory);
        PruneReportFiles();
        cachedBotNickname = Configuration.BotNickname.Trim();
        StartReceiveLoop();
        logger.LogInformation("群聊日报插件已启动，输出格式 {Format}，主题 {Theme}",
            Configuration.OutputFormat, Configuration.Theme);
        return Task.CompletedTask;
    }

    protected override async Task OnUpdate()
    {
        if (Configuration.EnableAutoReport == false)
        {
            return;
        }
        if (TimeSpan.TryParseExact(Configuration.AutoReportTime, "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan scheduledTime) == false)
        {
            return;
        }
        DateTime now = DateTime.Now;
        DateTime fireTime = now.Date + scheduledTime;
        // 到点后10分钟内触发一次，避免错过整点窗口
        if (now < fireTime || now > fireTime.AddMinutes(10) || lastAutoFiredDate == now.Date)
        {
            return;
        }
        lastAutoFiredDate = now.Date;
        List<long> scheduledGroupIds = await ResolveScheduledGroupIds();
        foreach (long groupId in scheduledGroupIds)
        {
            if (generatingGroups.ContainsKey(groupId))
            {
                continue;
            }
            // 定时排除名单：只拦定时生成，不影响手动
            if (SplitList(Configuration.DisabledGroups).Contains(groupId.ToString(CultureInfo.InvariantCulture)))
            {
                continue;
            }
            if (IsGroupAllowed(groupId) == false)
            {
                continue;
            }
            if (CheckCooldown(groupId, 0))
            {
                continue;
            }
            TryStartReport(groupId, "system");
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
                    await connection.ConnectAsync(new Uri(Configuration.OneBotWsUrl), connectTimeout.Token);
                    sharedConnection = connection;
                    connectionReady.TrySetResult(true);
                    LogDetail("协议端已连接：" + Configuration.OneBotWsUrl);
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

    static void CompletePendingResponse(string messageText)
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
            if (pendingResponses.TryRemove(echo, out TaskCompletionSource<string>? waiter))
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

        await sendLock.WaitAsync();
        try
        {
            if (sharedConnection == null || sharedConnection.State != WebSocketState.Open)
            {
                connectionReady = CreateReadySource();
                sharedConnection?.Dispose();
                ClientWebSocket connection = new();
                using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(10));
                await connection.ConnectAsync(new Uri(Configuration.OneBotWsUrl), connectTimeout.Token);
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
        catch
        {
            pendingResponses.TryRemove(echo, out _);
            throw;
        }
        finally
        {
            sendLock.Release();
        }

        string responseText = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(60));
        return responseText;
    }

    static JsonElement ParseActionResponse(string responseText, string operationName)
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

    // 检查冷却：返回 true 表示仍在冷却中（调用方应拒绝）
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

    bool TryStartReport(long groupId, string operatorId)
    {
        if (generatingGroups.TryAdd(groupId, 0) == false)
        {
            return false;
        }
        _ = Task.Run(() => RunReportAsync(groupId), DestroyCancellationToken);
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

    async Task RunReportAsync(long groupId)
    {
        try
        {
            // 1. 拉取群聊历史
            HistoryFetchResult fetch = await CollectHistoryAsync(groupId);
            List<ChatMessageRecord> messages = fetch.Messages;
            if (messages.Count < Configuration.MinMessages)
            {
                interactor.Poke("📊 群聊日报：最近 " + Configuration.AnalysisDays + " 天消息数 (" + messages.Count + ") 不足 "
                    + Configuration.MinMessages + " 条，本次不生成"
                    + (messages.Count == 0 ? "。拉取情况：" + fetch.Diagnosis : ""));
                return;
            }
            LogDetail("拉取到 " + messages.Count + " 条消息，开始统计与分析");

            // 2. 零Token统计
            DateTime windowStart = DateTimeOffset.FromUnixTimeSeconds(messages.Min(message => message.Time)).LocalDateTime;
            DateTime windowEnd = DateTimeOffset.FromUnixTimeSeconds(messages.Max(message => message.Time)).LocalDateTime;
            int participants = messages.Select(message => message.UserId).Distinct().Count();
            List<UserStat> topUsers = messages
                .GroupBy(message => message.UserId)
                .Select(group => new UserStat
                {
                    UserId = group.Key,
                    Nickname = group.First().Nickname,
                    Count = group.Count()
                })
                .OrderByDescending(stat => stat.Count)
                .Take(Configuration.TopUserCount)
                .ToList();
            string activeRange = ComputeActiveWindow(messages);
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
            if (Configuration.OutputFormat.Trim().ToLowerInvariant() == "text")
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

            lastReportTimes[groupId] = DateTime.Now;
            interactor.Poke("✅ 群 " + groupId + " 的群聊日报已生成并发送到群里");
        }
        catch (Exception reportError)
        {
            logger.LogError("日报生成失败：{Message}", reportError.Message);
            interactor.Poke("❌ 日报生成失败：" + reportError.Message);
        }
        finally
        {
            generatingGroups.TryRemove(groupId, out _);
        }
    }

    sealed class HistoryFetchResult
    {
        public List<ChatMessageRecord> Messages { get; set; } = new();
        public int RawItemCount { get; set; }
        public int FilteredItemCount { get; set; }
        public int Pages { get; set; }
        public bool WindowCut { get; set; }
        public string Diagnosis { get; set; } = "";
    }

    // 分页拉取群历史消息（最多 MaxMessages 条、时间窗口 AnalysisDays）。
    // 三端的翻页锚点不是同一个字段：NapCat 与 LLOneBot 只读 message_seq，SnowLuma 只读 message_id，
    // 且 SnowLuma 的 message_id 是带符号哈希（可为负、也不代表时间顺序）。
    // 所以每页都用 message_seq 定位最旧的那条消息，再把这条消息自己的 seq 或 message_id 发出去当下一页锚点，
    // 绝不能把两个字段的值混用。发错字段不报错，但有两种完全不同的表现（都在 SnowLuma/LLOneBot 源码里核对过）：
    // 认不出的锚点直接回空列表，被忽略的锚点回最新那一页——所以“空”和“没往更旧走”都得当成锚点失效。
    async Task<HistoryFetchResult> CollectHistoryAsync(long groupId)
    {
        HistoryFetchResult result = new();
        string anchorField = await ResolveHistoryAnchorFieldAsync();
        Dictionary<string, ChatMessageRecord> collected = new();
        long? anchor = null;
        long? lastOldestSeq = null;
        long lastOldestMessageId = 0;
        string firstRawSample = "";

        for (int page = 0; page < 25; page++)
        {
            List<JsonElement> pageMessages = await FetchHistoryPageAsync(groupId, anchor, anchorField);
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
                    List<JsonElement> retried = await FetchHistoryPageAsync(groupId, alternateAnchor, alternate);
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
                ChatMessageRecord? record = ToChatMessageRecord(item);
                if (record == null)
                {
                    result.FilteredItemCount++;
                    continue;
                }
                if (record.Time > 0)
                {
                    DateTime messageTime = DateTimeOffset.FromUnixTimeSeconds(record.Time).LocalDateTime;
                    if (messageTime < DateTime.Now.AddDays(-Configuration.AnalysisDays))
                    {
                        result.WindowCut = true;
                        continue;
                    }
                }
                // 三端源码核对过历史条目都带 time，真遇到缺时间字段的（段类型异常/未来版本）也不该整条丢掉，照收
                collected[record.UserId + "_" + record.Time + "_" + record.Text.GetHashCode()] = record;
            }

            if (collected.Count >= Configuration.MaxMessages || result.WindowCut)
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
            .Take(Configuration.MaxMessages)
            .ToList();

        string providerLabel = providerName.Length > 0
            ? providerName
            : (IsAnchorPinned() ? Configuration.ApiProvider.Trim() : "未识别");
        result.Diagnosis = "协议端 " + providerLabel + "，翻页锚点 " + anchorField
            + "，翻到 " + result.Pages + " 页，原始条目 " + result.RawItemCount + " 条，结构不符被过滤 " + result.FilteredItemCount + " 条";
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
            // 抓不到又不报错最难定位，判定依据直接写进普通日志，用户不用开详细日志也能回报
            logger.LogInformation("[日报] 群 {GroupId} 未取到可用历史消息：{Diagnosis}", groupId, result.Diagnosis);
            if (firstRawSample.Length > 0)
            {
                logger.LogInformation("[日报] 首条原始返回：{Sample}",
                    firstRawSample.Length > 500 ? firstRawSample[..500] : firstRawSample);
            }
        }
        return result;
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

    ChatMessageRecord? ToChatMessageRecord(JsonElement item)
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
        string text = ExtractMessageText(item);
        if (text.Trim().Length == 0)
        {
            return null;
        }
        JsonElement sender = item.TryGetProperty("sender", out JsonElement senderElement) && senderElement.ValueKind == JsonValueKind.Object
            ? senderElement
            : JsonDocument.Parse("{}").RootElement.Clone();
        string card = GetStringField(sender, "card");
        string nickname = GetStringField(sender, "nickname");
        return new ChatMessageRecord
        {
            Time = GetNumericField(item, "time"),
            UserId = userId,
            Nickname = card.Length > 0 ? card : (nickname.Length > 0 ? nickname : userId.ToString()),
            Text = text.Trim()
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
    string ComputeActiveWindow(List<ChatMessageRecord> messages)
    {
        if (messages.Count == 0)
        {
            return "?";
        }
        List<long> sortedTimes = messages.Select(message => message.Time).OrderBy(time => time).ToList();
        long windowSeconds = Configuration.ActiveWindowMinutes * 60L;
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
        DateTime startLocal = DateTimeOffset.FromUnixTimeSeconds(bestStart).LocalDateTime;
        DateTime endLocal = DateTimeOffset.FromUnixTimeSeconds(bestStart + windowSeconds).LocalDateTime;
        return startLocal.ToString("HH:mm", CultureInfo.InvariantCulture) + "-"
            + endLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    // ============================================================
    // LLM 分析（裸调语言模型，绕开人设约束；人设文本作为点评风格参考注入）
    // ============================================================

    async Task<AnalysisResult> AnalyzeWithLLMAsync(long groupId, List<ChatMessageRecord> messages)
    {
        AnalysisResult result = new();
        StringBuilder transcript = new();
        int taken = 0;
        foreach (ChatMessageRecord message in messages.OrderBy(message => message.Time).Take(Configuration.MaxMessages))
        {
            string text = message.Text.Length > 60 ? message.Text[..60] + "…" : message.Text;
            transcript.Append(message.Nickname).Append("(").Append(message.UserId).Append(")：").AppendLine(text);
            taken++;
        }
        // 参与者映射：用于校验LLM复制的QQ号，或按昵称回查
        Dictionary<string, long> uidByNickname = messages
            .GroupBy(message => message.Nickname)
            .ToDictionary(group => group.Key, group => group.First().UserId);

        int minTopics = Math.Max(1, Configuration.MinTopics);
        int maxTopics = Math.Max(minTopics, Configuration.MaxTopics);
        int quoteCount = Math.Max(0, Configuration.QuoteCount);
        personaStyle = ExtractPersonaStyle();
        StringBuilder prompt = new();
        prompt.Append("以下是群 " + groupId + " 最近的群聊记录（共 " + taken + " 条）。请基于这些记录生成日报内容。\n");
        prompt.Append("严格只返回一个JSON对象（不要任何解释或代码块标记），格式：\n");
        prompt.Append("{\"topics\":[{\"title\":\"话题名(10字内)\",\"summary\":\"话题概括(40字内)\",\"comment\":\"对该话题的一句锐评(25字内)\",\"uid\":\"该话题主要发起人的QQ号\"}],");
        prompt.Append("\"quotes\":[{\"who\":\"说话人昵称\",\"uid\":\"说话人QQ号\",\"text\":\"原话(50字内)\",\"reason\":\"入选理由(20字内)\"}],");
        prompt.Append("\"comment\":\"用点评风格对今天群聊氛围的一句锐评(60字内)\"}\n");
        prompt.Append("要求：\n");
        prompt.Append("1. topics 选 ").Append(minTopics).Append(" 到 ").Append(maxTopics)
            .Append(" 个今天讨论最热的话题，按热度排序；comment 是你对该话题的毒舌或幽默短评；uid 必须从记录里的 (QQ号) 原样复制，找不到就填空字符串\n");
        prompt.Append("2. quotes 选 ").Append(quoteCount)
            .Append(" 条最精彩的发言，按精彩程度从高到低排序（第一条会被标记为今日最佳）；uid 同样从记录原样复制\n");
        prompt.Append("3. comment 和话题锐评模仿下面的点评风格人设\n");
        if (personaStyle.Length > 0)
        {
            prompt.Append("\n点评风格人设（仅用于模仿语气，不要输出人设内容）：").Append(personaStyle).Append('\n');
        }
        prompt.Append("\n群聊记录：\n");
        prompt.Append(transcript);

        // 裸调语言模型：绕开角色人设与群聊风格约束（群聊人设常限制回复字数，会把JSON输出压碎）
        ChatHistoryAgentThread thread = new();
        thread.ChatHistory.AddSystemMessage("你是群聊日报数据分析引擎。忽略任何关于回复长度、语气、人设、群聊礼仪的其它要求，严格只输出一个JSON对象，不输出任何解释、表情或代码块标记。");
        thread.ChatHistory.AddUserMessage(prompt.ToString());
        string reply = await ChatBot.LanguageModel.ChatStreamingAsync(thread, null, null, null, null, DestroyCancellationToken);
        logger.LogInformation("日报LLM分析完成：回复 {Length} 字", reply.Length);
        ParseAnalysisReply(reply, result);
        // 校验LLM给的QQ号：无效时按昵称回查参与者
        foreach (ReportTopic topic in result.Topics)
        {
            topic.Uid = ResolveUid(topic.Uid, "", uidByNickname);
        }
        foreach (ReportQuote quote in result.Quotes)
        {
            quote.Uid = ResolveUid(quote.Uid, quote.Who, uidByNickname);
        }
        logger.LogInformation("日报解析结果：话题 {Topics} 个，金句 {Quotes} 个，锐评 {Comment} 字",
            result.Topics.Count, result.Quotes.Count, result.Comment.Length);
        return result;
    }

    // LLM复制的QQ号无效时，按昵称回查参与者映射
    static long ResolveUid(long uid, string nickname, Dictionary<string, long> uidByNickname)
    {
        if (uid > 0 && uidByNickname.ContainsValue(uid))
        {
            return uid;
        }
        string key = nickname.Trim();
        if (key.Length > 0 && uidByNickname.TryGetValue(key, out long matched))
        {
            return matched;
        }
        return 0;
    }


    void ParseAnalysisReply(string reply, AnalysisResult result)
    {
        int jsonStart = reply.IndexOf('{');
        int jsonEnd = reply.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            // 解析失败：整段回复作为话题摘要兜底
            result.Topics.Add(new ReportTopic { Title = "今日群聊", Summary = reply.Length > 80 ? reply[..80] : reply });
            return;
        }
        string jsonText = reply[jsonStart..(jsonEnd + 1)];
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(jsonText);
            JsonElement root = parsed.RootElement.Clone();
            if (root.TryGetProperty("topics", out JsonElement topicsElement) && topicsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement topic in topicsElement.EnumerateArray().Take(Math.Max(1, Configuration.MaxTopics)))
                {
                    result.Topics.Add(new ReportTopic
                    {
                        Title = GetStringField(topic, "title"),
                        Summary = GetStringField(topic, "summary"),
                        Comment = GetStringField(topic, "comment"),
                        Uid = GetNumericField(topic, "uid")
                    });
                }
            }
            if (root.TryGetProperty("quotes", out JsonElement quotesElement) && quotesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement quote in quotesElement.EnumerateArray().Take(Math.Max(0, Configuration.QuoteCount)))
                {
                    result.Quotes.Add(new ReportQuote
                    {
                        Who = GetStringField(quote, "who"),
                        Uid = GetNumericField(quote, "uid"),
                        Text = GetStringField(quote, "text"),
                        Reason = GetStringField(quote, "reason")
                    });
                }
            }
            result.Comment = GetStringField(root, "comment");
        }
        catch
        {
            result.Topics.Add(new ReportTopic { Title = "今日群聊", Summary = jsonText.Length > 80 ? jsonText[..80] : jsonText });
        }
    }

    // ============================================================
    // 渲染与发送
    // ============================================================

    async Task<bool> RenderAndSendImageAsync(long groupId, string groupName, int totalMessages, int participants,
        string activeRange, List<UserStat> topUsers, AnalysisResult analysis, string botName,
        DateTime windowStart, DateTime windowEnd, Dictionary<long, string> userNames)
    {
        string? browserPath = FindBrowserExecutable();
        if (browserPath == null)
        {
            logger.LogWarning("未找到系统 Edge/Chrome，回退文字日报");
            return false;
        }

        string theme = ResolveTheme();
        string html = await BuildHtml(groupName, totalMessages, participants, activeRange, topUsers, analysis, botName,
            windowStart, windowEnd, theme, groupId, userNames);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string htmlPath = Path.Combine(reportDirectory, groupId + "_" + timestamp + ".html");
        string imagePath = Path.Combine(reportDirectory, groupId + "_" + timestamp + ".png");
        await File.WriteAllTextAsync(htmlPath, html, Encoding.UTF8);

        // 高度按文字换行逐段估算并放宽：无头截图只截视口，估矮了内容会被切掉；富余交给裁图，所以宁可估高。
        // 模板整体 zoom 1.18（所有字号一起放大），故每行的字符数按变窄的逻辑行宽重算、每行高度乘回去
        int estimatedHeight = 550;
        foreach (ReportTopic topic in analysis.Topics)
        {
            estimatedHeight += 56 + (int)Math.Ceiling(topic.Summary.Length / 40.0) * 24;
            if (topic.Comment.Length > 0)
            {
                estimatedHeight += (int)Math.Ceiling(topic.Comment.Length / 37.0) * 21 + 7;
            }
        }
        estimatedHeight += Math.Min(topUsers.Count, Configuration.TopUserCount) * 52 + 70;
        int quoteSlot = 0;
        foreach (ReportQuote quote in analysis.Quotes)
        {
            quoteSlot++;
            estimatedHeight += 62 + (int)Math.Ceiling(quote.Text.Length / 32.0) * 25;
            if (quoteSlot == 1 && quote.Reason.Length > 0)
            {
                estimatedHeight += (int)Math.Ceiling(quote.Reason.Length / 34.0) * 21 + 7;
            }
        }
        estimatedHeight += 105 + (int)Math.Ceiling(analysis.Comment.Length / 35.0) * 26;
        estimatedHeight = (int)(estimatedHeight * 1.2) + 160;
        string windowSize = "--window-size=760," + Math.Clamp(estimatedHeight, 1000, 6000) + " ";
        string pageUrl = new Uri(htmlPath).AbsoluteUri;
        // 独立 user-data-dir：避免 Edge/Chrome 因已有实例或策略拒绝无头启动
        string userDataDir = "--user-data-dir=\"" + Path.Combine(Path.GetTempPath(), "suran_daily_report_profile") + "\" ";
        string edgeFlag = browserPath.Contains("msedge", StringComparison.OrdinalIgnoreCase)
            ? "--edge-skip-compat-layer-relaunch "
            : "";

        // 新旧两版无头模式依次尝试
        string[] attempts = new[]
        {
            "--headless=new " + edgeFlag,
            "--headless " + edgeFlag
        };
        string lastError = "浏览器未产出截图";
        foreach (string headlessFlag in attempts)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = browserPath,
                Arguments = headlessFlag + "--disable-gpu --no-first-run --no-default-browser-check --hide-scrollbars "
                    + userDataDir + windowSize
                    + "--screenshot=\"" + imagePath + "\" "
                    + "\"" + pageUrl + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using Process? browserProcess = Process.Start(startInfo);
            if (browserProcess == null)
            {
                lastError = "浏览器进程启动失败";
                continue;
            }
            string browserStdError = await browserProcess.StandardError.ReadToEndAsync();
            await browserProcess.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(120)).Token);
            // Chrome 退出后截图可能由子进程延迟落盘，轮询等待最多6秒
            bool screenshotReady = false;
            for (int wait = 0; wait < 20; wait++)
            {
                if (File.Exists(imagePath))
                {
                    screenshotReady = true;
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            if (screenshotReady)
            {
                if (Configuration.CropBottomBlank)
                {
                    CropBottomBlank(imagePath);
                }
                PruneReportFiles();
                try
                {
                    await SendReportImageAsync(groupId, imagePath);
                    return true;
                }
                catch (Exception sendError)
                {
                    // 部分协议端对本地文件图片段支持不佳：转文字日报而不是整单失败
                    logger.LogWarning("日报图片发送失败，转文字日报：{Message}", sendError.Message);
                    return false;
                }
            }
            lastError = "无头渲染未产出截图（进程退出后6秒内未出现文件）：" + (browserStdError.Length > 300 ? browserStdError[..300] : browserStdError);
            LogDetail(lastError);
        }
        logger.LogWarning("日报渲染失败（浏览器: {Browser}）：{Error}", browserPath, lastError);
        return false;
    }

    // 无头浏览器只能按窗口高度截图，估算再准也总有富余，图底部就留一片空白。
    // 裁法：从最后一行往上找第一条「横向不是纯色」的行，下面只留正文自带的下内边距。
    // 为什么比的是这一行自己匀不匀、而不是跟左上角底色比：视口比文档高时文档下方画的是白，跟主题底色根本不是一回事。
    // 裁失败（没有 GDI+、整张都是纯色等）就原样发出去，绝不为裁图把日报搞没了。
    void CropBottomBlank(string imagePath)
    {
        int keepHeight = 0;
        int originalHeight = 0;
        string croppedPath = imagePath + ".crop";
        try
        {
            // GDI+ 会锁住打开的图片文件，所以读完先关，再决定要不要覆盖
            using (Bitmap bitmap = new(imagePath))
            {
                originalHeight = bitmap.Height;
                int contentBottom = -1;
                for (int y = bitmap.Height - 1; y >= 0; y--)
                {
                    if (RowHasContent(bitmap, y))
                    {
                        contentBottom = y;
                        break;
                    }
                }
                if (contentBottom < 0 || bitmap.Height - contentBottom <= 32)
                {
                    return;
                }
                keepHeight = Math.Clamp(contentBottom + 1 + 24, 200, bitmap.Height);
                using Bitmap cropped = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, keepHeight), bitmap.PixelFormat);
                cropped.Save(croppedPath, ImageFormat.Png);
            }
            File.Move(croppedPath, imagePath, true);
            LogDetail("日报图底部空白已裁掉 " + (originalHeight - keepHeight) + " px");
        }
        catch (Exception cropError)
        {
            try
            {
                if (File.Exists(croppedPath))
                {
                    File.Delete(croppedPath);
                }
            }
            catch
            {
            }
            logger.LogWarning("日报裁底部空白失败（用原图发送）：{Message}", cropError.Message);
        }
    }

    // 这一行有没有内容：横向每隔 6 px 取样，看它跟行首差多少；容差 6 留给极淡的渐变与阴影
    static bool RowHasContent(Bitmap bitmap, int y)
    {
        Color first = bitmap.GetPixel(0, y);
        for (int x = 2; x < bitmap.Width; x += 6)
        {
            Color pixel = bitmap.GetPixel(x, y);
            if (Math.Abs(pixel.R - first.R) > 6 || Math.Abs(pixel.G - first.G) > 6 || Math.Abs(pixel.B - first.B) > 6)
            {
                return true;
            }
        }
        return false;
    }

    // 每次生成都会留一份 html + png，从不清理会一直涨。按「份」删：同一时间戳的两个文件算一份。
    void PruneReportFiles()
    {
        if (Configuration.ReportKeepCount <= 0 || reportDirectory.Length == 0)
        {
            return;
        }
        try
        {
            List<IGrouping<string, string>> batches = Directory.GetFiles(reportDirectory)
                .Where(file => file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                .GroupBy(Path.GetFileNameWithoutExtension)
                .OrderByDescending(batch => batch.Max(File.GetLastWriteTimeUtc))
                .ToList();
            int removed = 0;
            foreach (IGrouping<string, string> batch in batches.Skip(Configuration.ReportKeepCount))
            {
                foreach (string file in batch)
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch
                    {
                        // 协议端可能还攥着刚发出去的那张图，删不掉就下一轮再删
                    }
                }
            }
            if (removed > 0)
            {
                LogDetail("日报文件已清理 " + removed + " 个，保留最近 " + Configuration.ReportKeepCount + " 份");
            }
        }
        catch (Exception pruneError)
        {
            logger.LogWarning("清理日报文件失败：{Message}", pruneError.Message);
        }
    }

    // 本地图片段三端支持度不一样：NapCat 与 LLOneBot 走 url→file 顺序（不给 url 时才读 file），
    // SnowLuma 偏好在本地能直接加载的 file，但它对 Windows 盘符路径的解析没有公开保证。
    // 所以 file:/// 失败时同一张图改走 base64://（三端都支持），再失败才降级成文字日报。
    async Task SendReportImageAsync(long groupId, string imagePath)
    {
        try
        {
            await SendImageSegmentAsync(groupId, "file:///" + imagePath.Replace('\\', '/'));
            return;
        }
        catch (Exception fileError)
        {
            logger.LogInformation("[日报] 本地路径图片段未被 {Provider} 接受，改用 base64：{Message}",
                providerName.Length > 0 ? providerName : "协议端", fileError.Message);
        }
        string base64Image = Convert.ToBase64String(File.ReadAllBytes(imagePath));
        await SendImageSegmentAsync(groupId, "base64://" + base64Image);
    }

    async Task SendImageSegmentAsync(long groupId, string fileReference)
    {
        JsonObject imageSegment = new()
        {
            ["type"] = "image",
            ["data"] = new JsonObject { ["file"] = fileReference }
        };
        JsonArray messageArray = new();
        messageArray.Add(imageSegment);
        JsonObject sendParameters = new()
        {
            ["group_id"] = groupId,
            ["message"] = messageArray
        };
        string sendResponse = await CallActionAsync("send_group_msg", sendParameters);
        ParseActionResponse(sendResponse, "发送日报图片");
    }

#if !ANDROID
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string? FindBrowserFromRegistry()
    {
        string[] browserNames = new[] { "msedge.exe", "chrome.exe", "chromium.exe", "brave.exe" };
        foreach (string browserName in browserNames)
        {
            string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + browserName;
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using RegistryKey? baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using RegistryKey? appKey = baseKey.OpenSubKey(keyPath);
                        if (appKey?.GetValue(null) is string registeredPath == false || string.IsNullOrWhiteSpace(registeredPath))
                        {
                            continue;
                        }
                        string cleanPath = registeredPath.Trim().Trim('"');
                        string expandedPath = Environment.ExpandEnvironmentVariables(cleanPath);
                        if (File.Exists(expandedPath))
                        {
                            return expandedPath;
                        }
                    }
                    catch
                    {
                        // 单个注册表位置读不到不影响其余探测
                    }
                }
            }
        }
        return null;
    }
#endif

    // 浏览器发现：Windows 走注册表 App Paths（解析浏览器的标准途径）+ 常见安装路径兜底；其余平台只在 PATH 上找同名可执行文件
    static string? FindBrowserExecutable()
    {
#if !ANDROID
        if (OperatingSystem.IsWindows())
        {
            string? registered = FindBrowserFromRegistry();
            if (registered != null)
            {
                return registered;
            }
        }
#endif

        if (OperatingSystem.IsWindows() == false)
        {
            foreach (string name in new[] { "chrome", "chromium", "microsoft-edge", "brave" })
            {
                foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    if (directory.Trim().Length == 0)
                    {
                        continue;
                    }
                    string candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            return null;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates = new[]
        {
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Chromium", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    string ResolveTheme()
    {
        string theme = Configuration.Theme.Trim().ToLowerInvariant();
        if (theme == "random")
        {
            string[] themes = new[] { "warm", "sakura", "clover", "diamond", "sky" };
            theme = themes[Random.Shared.Next(themes.Length)];
        }
        return theme;
    }

    async Task<string> BuildHtml(string groupName, int totalMessages, int participants, string activeRange,
        List<UserStat> topUsers, AnalysisResult analysis, string botName,
        DateTime windowStart, DateTime windowEnd, string theme, long groupId, Dictionary<long, string> userNames)
    {
        string themeName = theme switch
        {
            "sakura" => "樱花粉",
            "clover" => "幸运草绿",
            "diamond" => "钻石黑",
            "sky" => "天空蓝",
            _ => "暖黄手账"
        };

        // 话题的「评：」是机器人写的点评，要带机器人头像，所以机器人名与头像在拼话题前先取好
        string botAvatar = await LoadBotAvatarDataUriAsync();
        StringBuilder topicsBuilder = new();
        if (analysis.Topics.Count == 0)
        {
            topicsBuilder.Append("<div class=\"topic\"><div class=\"t-name\">？</div></div>");
        }
        foreach (ReportTopic topic in analysis.Topics)
        {
            string topicAvatar = topic.Uid > 0 ? await LoadAvatarDataUriAsync(topic.Uid) : "";
            string initiator = topic.Uid > 0 && userNames.TryGetValue(topic.Uid, out string? name) ? name : "";
            topicsBuilder.Append("<div class=\"topic\">")
                .Append("<div class=\"topic-body\"><div class=\"t-name\">").Append(EscapeHtml(topic.Title))
                .Append("</div><div class=\"t-sum\">").Append(EscapeHtml(topic.Summary)).Append("</div>");
            if (topic.Comment.Length > 0)
            {
                topicsBuilder.Append("<div class=\"t-cmt\">").Append(AvatarMarkup(botAvatar, botName, "c-face"))
                    .Append("<span>评：").Append(EscapeHtml(topic.Comment)).Append("</span></div>");
            }
            topicsBuilder.Append("</div>");
            if (initiator.Length > 0)
            {
                topicsBuilder.Append("<div class=\"t-initiator\">")
                    .Append("<span class=\"t-starter\">由 ").Append(EscapeHtml(initiator)).Append(" 引出</span>")
                    .Append(AvatarMarkup(topicAvatar, initiator, "t-avatar"))
                    .Append("</div>");
            }
            topicsBuilder.Append("</div>");
        }

        StringBuilder usersBuilder = new();
        foreach ((UserStat user, int index) in topUsers.Select((user, index) => (user, index)))
        {
            string avatarDataUri = await LoadAvatarDataUriAsync(user.UserId);
            string displayName = user.Nickname.Length > 0 ? user.Nickname : user.UserId.ToString();
            usersBuilder.Append("<div class=\"user-row\"><div class=\"rank\">").Append(index + 1)
                .Append("</div><img src=\"").Append(avatarDataUri)
                .Append("\"><div class=\"u-name\">").Append(EscapeHtml(displayName))
                .Append("</div><div class=\"u-count\">").Append(user.Count).Append(" 条</div></div>");
        }

        StringBuilder quotesBuilder = new();
        if (analysis.Quotes.Count == 0)
        {
            quotesBuilder.Append("<div class=\"quote\"><div class=\"q-text\">今天没有值得记录的金句</div></div>");
        }
        int quoteIndex = 0;
        foreach (ReportQuote quote in analysis.Quotes)
        {
            quoteIndex++;
            string quoteAvatar = quote.Uid > 0 ? await LoadAvatarDataUriAsync(quote.Uid) : "";
            string who = quote.Who.Length > 0 ? quote.Who
                : (quote.Uid > 0 && userNames.TryGetValue(quote.Uid, out string? uname) ? uname : "");
            quotesBuilder.Append("<div class=\"quote\">").Append(AvatarMarkup(quoteAvatar, who, "q-avatar"))
                .Append("<div class=\"quote-body\"><div class=\"q-text\">「").Append(EscapeHtml(quote.Text)).Append("」");
            if (quoteIndex == 1)
            {
                quotesBuilder.Append("<span class=\"q-best\">今日最佳</span>");
            }
            quotesBuilder.Append("</div>");
            if (quoteIndex == 1 && quote.Reason.Length > 0)
            {
                // 今日最佳单独给一行点评，普通金句不带（带了会把版面挤乱）
                // 今日最佳的点评也是机器人写的，跟话题那句「评：」一样带它的头像
                quotesBuilder.Append("<div class=\"q-reason\">").Append(AvatarMarkup(botAvatar, botName, "c-face"))
                    .Append("<span>评：").Append(EscapeHtml(quote.Reason)).Append("</span></div>");
            }
            quotesBuilder.Append("</div>");
            // 谁说的挪到这一行的右边，跟话题卡「由 XX 引出」同一侧，读起来视线不用来回跳
            if (who.Length > 0)
            {
                quotesBuilder.Append("<div class=\"q-who\">—— ").Append(EscapeHtml(who)).Append("</div>");
            }
            quotesBuilder.Append("</div>");
        }

        string dateRange = windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " ~ "
            + windowEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string groupAvatar = await LoadGroupAvatarDataUriAsync(groupId);
        string commentText = analysis.Comment.Length > 0 ? analysis.Comment : "今天大家聊得不错~";

        return ReportTemplate.Html
            .Replace("__THEME__", theme == "warm" ? "warm" : theme)
            .Replace("__GROUP_NAME__", EscapeHtml(groupName))
            .Replace("__DATE_RANGE__", EscapeHtml(dateRange))
            .Replace("__THEME_NAME__", themeName)
            .Replace("__GROUP_AVATAR__", groupAvatar)
            .Replace("__TOTAL__", totalMessages.ToString(CultureInfo.InvariantCulture))
            .Replace("__PARTICIPANTS__", participants.ToString(CultureInfo.InvariantCulture))
            .Replace("__ACTIVE_RANGE__", activeRange)
            .Replace("__TOPICS__", topicsBuilder.ToString())
            .Replace("__TOPN__", Configuration.TopUserCount.ToString(CultureInfo.InvariantCulture))
            .Replace("__USERS__", usersBuilder.ToString())
            .Replace("__QUOTES__", quotesBuilder.ToString())
            .Replace("__BOT_NAME__", EscapeHtml(botName))
            .Replace("__BOT_AVATAR__", AvatarMarkup(botAvatar, botName, "c-avatar"))
            .Replace("__COMMENT__", EscapeHtml(commentText));
    }

    // 头像取不到时用昵称首字的占位圆块代替，避免裂图
    static string AvatarMarkup(string dataUri, string name, string cssClass)
    {
        if (dataUri.Length > 0)
        {
            return "<img class=\"" + cssClass + "\" src=\"" + dataUri + "\">";
        }
        string initial = name.Length > 0 ? EscapeHtml(name[..1]) : "?";
        return "<div class=\"" + cssClass + " ph\"><span>" + initial + "</span></div>";
    }

    string BuildTextReport(string groupName, int totalMessages, int participants, string activeRange,
        List<UserStat> topUsers, AnalysisResult analysis, string botName, DateTime windowStart, DateTime windowEnd)
    {
        StringBuilder builder = new();
        builder.AppendLine("══════ " + groupName + " 群聊日报 ══════");
        builder.AppendLine("📅 " + windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " ~ "
            + windowEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        builder.AppendLine("💬 消息总数：" + totalMessages + " | 👥 参与人数：" + participants
            + " | ⏰ 最活跃：" + activeRange);
        builder.AppendLine();
        builder.AppendLine("🔥 热门话题：");
        if (analysis.Topics.Count == 0)
        {
            builder.AppendLine("  ？");
        }
        foreach (ReportTopic topic in analysis.Topics)
        {
            builder.AppendLine("  · " + topic.Title + " — " + topic.Summary);
        }
        builder.AppendLine();
        builder.AppendLine("👤 活跃用户：");
        foreach ((UserStat user, int index) in topUsers.Select((user, index) => (user, index)))
        {
            builder.AppendLine("  " + (index + 1) + ". " + (user.Nickname.Length > 0 ? user.Nickname : user.UserId.ToString())
                + "（" + user.Count + " 条）");
        }
        if (analysis.Quotes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("💡 今日金句：");
            foreach (ReportQuote quote in analysis.Quotes)
            {
                builder.AppendLine("  「" + quote.Text + "」—— " + quote.Who + "（" + quote.Reason + "）");
            }
        }
        builder.AppendLine();
        builder.AppendLine("🤖 " + botName + "锐评：" + (analysis.Comment.Length > 0 ? analysis.Comment : "今天大家聊得不错~"));
        return builder.ToString();
    }

    // ============================================================
    // 头像与群信息
    // ============================================================

    // 用户头像缓存3天，读不到返回空占位
    async Task<string> LoadAvatarDataUriAsync(long userId)
    {
        try
        {
            string cachePath = Path.Combine(avatarDirectory, userId + ".png");
            if (File.Exists(cachePath) == false
                || (DateTime.Now - File.GetLastWriteTime(cachePath)).TotalDays > 3)
            {
                using HttpResponseMessage avatarResponse = await sharedHttpClient.GetAsync(
                    "https://q.qlogo.cn/g?b=qq&nk=" + userId + "&s=100");
                avatarResponse.EnsureSuccessStatusCode();
                byte[] imageBytes = await avatarResponse.Content.ReadAsByteArrayAsync();
                File.WriteAllBytes(cachePath, imageBytes);
            }
            return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(cachePath));
        }
        catch (Exception avatarError)
        {
            LogDetail("头像获取失败 " + userId + "：" + avatarError.Message);
            return "";
        }
    }

    async Task<string> LoadGroupAvatarDataUriAsync(long groupId)
    {
        try
        {
            string cachePath = Path.Combine(avatarDirectory, "group_" + groupId + ".png");
            if (File.Exists(cachePath) == false
                || (DateTime.Now - File.GetLastWriteTime(cachePath)).TotalDays > 3)
            {
                using HttpResponseMessage avatarResponse = await sharedHttpClient.GetAsync(
                    "https://p.qlogo.cn/gh/" + groupId + "/" + groupId + "/0/");
                avatarResponse.EnsureSuccessStatusCode();
                byte[] imageBytes = await avatarResponse.Content.ReadAsByteArrayAsync();
                File.WriteAllBytes(cachePath, imageBytes);
            }
            return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(cachePath));
        }
        catch
        {
            return "";
        }
    }


    async Task<string> GetGroupNameAsync(long groupId)
    {
        try
        {
            JsonObject parameters = new() { ["group_id"] = groupId };
            string response = await CallActionAsync("get_group_info", parameters);
            JsonElement data = ParseActionResponse(response, "获取群信息");
            string groupName = GetStringField(data, "group_name");
            return groupName.Length > 0 ? groupName : groupId.ToString();
        }
        catch
        {
            return groupId.ToString();
        }
    }

    async Task<string> ResolveBotNicknameAsync()
    {
        if (cachedBotNickname.Length > 0)
        {
            return cachedBotNickname;
        }
        try
        {
            string response = await CallActionAsync("get_login_info", new JsonObject());
            JsonElement data = ParseActionResponse(response, "获取登录信息");
            string nickname = GetStringField(data, "nickname");
            cachedBotId = GetNumericField(data, "user_id");
            if (nickname.Length > 0)
            {
                cachedBotNickname = nickname;
                return nickname;
            }
        }
        catch
        {
            // 拿不到就用默认署名
        }
        return "AI";
    }

    // 从对话历史提取角色人设（系统提示）作为点评风格参考
    string ExtractPersonaStyle()
    {
        try
        {
            foreach (ChatMessageContent message in ChatBot.ChatHistory)
            {
                if (message.Role.Label == "system" && string.IsNullOrWhiteSpace(message.Content) == false)
                {
                    string content = message.Content.Trim();
                    return content.Length > 600 ? content[..600] + "…" : content;
                }
            }
        }
        catch
        {
            // 拿不到人设就走默认风格
        }
        return "";
    }

    // 机器人头像（大图，用于锐评区）
    async Task<string> LoadBotAvatarDataUriAsync()
    {
        try
        {
            if (cachedBotId == 0)
            {
                await ResolveBotNicknameAsync();
            }
            if (cachedBotId == 0)
            {
                return "";
            }
            string cachePath = Path.Combine(avatarDirectory, "bot_" + cachedBotId + ".png");
            if (File.Exists(cachePath) == false
                || (DateTime.Now - File.GetLastWriteTime(cachePath)).TotalDays > 3)
            {
                using HttpResponseMessage avatarResponse = await sharedHttpClient.GetAsync(
                    "https://q1.qlogo.cn/g?b=qq&nk=" + cachedBotId + "&s=640");
                avatarResponse.EnsureSuccessStatusCode();
                byte[] imageBytes = await avatarResponse.Content.ReadAsByteArrayAsync();
                File.WriteAllBytes(cachePath, imageBytes);
            }
            return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(cachePath));
        }
        catch
        {
            return "";
        }
    }

    async Task SendGroupTextAsync(long groupId, string text)
    {
        JsonObject messageSegment = new()
        {
            ["type"] = "text",
            ["data"] = new JsonObject { ["text"] = text }
        };
        JsonArray messageArray = new();
        messageArray.Add(messageSegment);
        JsonObject parameters = new()
        {
            ["group_id"] = groupId,
            ["message"] = messageArray
        };
        string response = await CallActionAsync("send_group_msg", parameters);
        ParseActionResponse(response, "发送群消息");
    }

    // ============================================================
    // 辅助
    // ============================================================

    void LogDetail(string message)
    {
        if (Configuration.DetailLog)
        {
            logger.LogInformation("[日报] 🔍 {Message}", message);
        }
    }

    static List<string> SplitList(string raw)
    {
        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    static string GetStringField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        if (element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return "";
        }
        return field.ValueKind switch
        {
            JsonValueKind.String => field.GetString() ?? "",
            JsonValueKind.Number => field.GetRawText(),
            _ => ""
        };
    }

    static long GetNumericField(JsonElement element, string fieldName)
    {
        if (TryGetNumericField(element, fieldName, out long value))
        {
            return value;
        }
        return 0;
    }

    // 翻页锚点必须区分“字段没有”和“字段是0/负数”：SnowLuma 的 message_id 是带符号哈希，负数是正常值
    static bool TryGetNumericField(JsonElement element, string fieldName, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return false;
        }
        return field.ValueKind switch
        {
            JsonValueKind.Number => field.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(field.GetString(), out value),
            _ => false
        };
    }
}
