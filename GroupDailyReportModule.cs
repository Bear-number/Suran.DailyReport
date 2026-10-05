using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
using Microsoft.Win32;

namespace Suran.DailyReport;

public class DailyReportConfig
{
    // ---- 基础设置 ----
    [DisplayName("基础·协议端地址")]
    [Description("OneBot正向WebSocket地址，默认 ws://127.0.0.1:3001")]
    public string OneBotWsUrl { get; set; } = "ws://127.0.0.1:3001";

    [DisplayName("基础·指定机器人昵称")]
    [Description("锐评署名，留空自动获取Bot昵称")]
    public string BotNickname { get; set; } = "";

    [DisplayName("基础·可使用日报的群组")]
    [Description("群号英文逗号分隔，留空=所有群")]
    public string EnabledGroups { get; set; } = "";

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

    // ---- 报告状态 ----
    readonly ConcurrentDictionary<long, byte> generatingGroups = new();
    readonly Dictionary<long, DateTime> lastReportTimes = new();
    DateTime lastAutoFiredDate = DateTime.MinValue;
    string cachedBotNickname = "";

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
        List<long> scheduledGroupIds = ResolveScheduledGroupIds();
        foreach (long groupId in scheduledGroupIds)
        {
            if (generatingGroups.ContainsKey(groupId))
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
        }
        finally
        {
            sendLock.Release();
        }
        logger.LogInformation("群聊日报插件已卸载");
    }

    List<long> ResolveScheduledGroupIds()
    {
        string raw = Configuration.ScheduledGroups.Trim();
        if (raw.Length == 0)
        {
            raw = Configuration.EnabledGroups;
        }
        if (raw.Trim().Length == 0)
        {
            // 都未配置时按“所有群”没有意义（不知道群号），返回空集，日志提示
            logger.LogWarning("自动日报未配置定时群组或可使用群组，无法确定要生成日报的群");
            return new List<long>();
        }
        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => long.TryParse(text, out long parsed) ? parsed : 0)
            .Where(id => id > 0)
            .ToList();
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
            string detail = root.TryGetProperty("message", out JsonElement messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? ""
                : "未知错误";
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
        return SplitList(raw).Contains(groupId.ToString());
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
    }

    sealed class ReportQuote
    {
        public string Who { get; set; } = "";
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
            List<ChatMessageRecord> messages = await CollectHistoryAsync(groupId);
            if (messages.Count < Configuration.MinMessages)
            {
                interactor.Poke("📊 群聊日报：最近 " + Configuration.AnalysisDays + " 天消息数 (" + messages.Count + ") 不足 "
                    + Configuration.MinMessages + " 条，本次不生成");
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
                    activeRange, topUsers, analysis, botName, windowStart, windowEnd);
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

    // 分页拉取群历史消息（最多 MaxMessages 条、时间窗口 AnalysisDays）
    async Task<List<ChatMessageRecord>> CollectHistoryAsync(long groupId)
    {
        Dictionary<string, ChatMessageRecord> collected = new();
        long? oldestSeq = null;
        for (int page = 0; page < 25; page++)
        {
            JsonObject parameters = new() { ["group_id"] = groupId };
            if (oldestSeq.HasValue)
            {
                parameters["message_seq"] = oldestSeq.Value;
            }
            string response = await CallActionAsync("get_group_msg_history", parameters);
            JsonElement data = ParseActionResponse(response, "获取群消息历史");
            if (data.ValueKind != JsonValueKind.Object || data.TryGetProperty("messages", out JsonElement messagesElement) == false
                || messagesElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }
            List<JsonElement> pageMessages = messagesElement.EnumerateArray().ToList();
            if (pageMessages.Count == 0)
            {
                break;
            }

            long pageOldestSeq = long.MaxValue;
            bool reachedWindowEnd = false;
            foreach (JsonElement item in pageMessages)
            {
                long messageSeq = GetNumericField(item, "message_seq");
                if (messageSeq > 0 && messageSeq < pageOldestSeq)
                {
                    pageOldestSeq = messageSeq;
                }
                ChatMessageRecord? record = ToChatMessageRecord(item);
                if (record == null)
                {
                    continue;
                }
                DateTime messageTime = DateTimeOffset.FromUnixTimeSeconds(record.Time).LocalDateTime;
                if (messageTime < DateTime.Now.AddDays(-Configuration.AnalysisDays))
                {
                    reachedWindowEnd = true;
                    continue;
                }
                collected[record.UserId + "_" + record.Time + "_" + record.Text.GetHashCode()] = record;
            }

            if (collected.Count >= Configuration.MaxMessages || reachedWindowEnd)
            {
                break;
            }
            if (oldestSeq.HasValue && pageOldestSeq >= oldestSeq.Value)
            {
                // 没有更旧的消息了
                break;
            }
            oldestSeq = pageOldestSeq == long.MaxValue ? null : pageOldestSeq;
            if (oldestSeq.HasValue == false)
            {
                break;
            }
        }
        return collected.Values
            .OrderByDescending(record => record.Time)
            .Take(Configuration.MaxMessages)
            .ToList();
    }

    ChatMessageRecord? ToChatMessageRecord(JsonElement item)
    {
        if (GetStringField(item, "post_type") != "message")
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
    // LLM 分析（话题/金句/锐评一次完成，ChatAsync 回复自带角色人设）
    // ============================================================

    async Task<AnalysisResult> AnalyzeWithLLMAsync(long groupId, List<ChatMessageRecord> messages)
    {
        AnalysisResult result = new();
        StringBuilder transcript = new();
        int taken = 0;
        foreach (ChatMessageRecord message in messages.Take(Configuration.MaxMessages))
        {
            string text = message.Text.Length > 60 ? message.Text[..60] + "…" : message.Text;
            transcript.Append(message.Nickname).Append("(").Append(message.UserId).Append(")：").AppendLine(text);
            taken++;
        }

        int minTopics = Math.Max(1, Configuration.MinTopics);
        int maxTopics = Math.Max(minTopics, Configuration.MaxTopics);
        int quoteCount = Math.Max(0, Configuration.QuoteCount);
        StringBuilder prompt = new();
        prompt.Append("以下是群 " + groupId + " 最近的群聊记录（共 " + taken + " 条）。请基于这些记录生成日报内容。\n");
        prompt.Append("严格只返回一个JSON对象（不要任何解释或代码块标记），格式：\n");
        prompt.Append("{\"topics\":[{\"title\":\"话题名(10字内)\",\"summary\":\"话题概括(40字内)\"}],");
        prompt.Append("\"quotes\":[{\"who\":\"说话人\",\"text\":\"原话(50字内)\",\"reason\":\"入选理由(20字内)\"}],");
        prompt.Append("\"comment\":\"用人设口吻对今天群聊氛围的一句锐评(60字内)\"}\n");
        prompt.Append("要求：topics 选 ").Append(minTopics).Append(" 到 ").Append(maxTopics)
            .Append(" 个今天讨论最多的真实话题；quotes 选 ").Append(quoteCount)
            .Append(" 条最精彩的发言（0则不选）；comment 用你自己的口吻。\n\n群聊记录：\n");
        prompt.Append(transcript);

        ChatResult chatResult = await interactor.ChatAsync(prompt.ToString());
        if (chatResult.Exception != null)
        {
            throw new Exception("LLM 分析失败：" + chatResult.Exception.Message);
        }
        string reply = chatResult.AIMessage ?? "";
        ParseAnalysisReply(reply, result);
        return result;
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
                        Summary = GetStringField(topic, "summary")
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
        DateTime windowStart, DateTime windowEnd)
    {
        string? browserPath = FindBrowserExecutable();
        if (browserPath == null)
        {
            logger.LogWarning("未找到系统 Edge/Chrome，回退文字日报");
            return false;
        }

        currentReportGroup = groupId;
        string theme = ResolveTheme();
        string html = await BuildHtml(groupName, totalMessages, participants, activeRange, topUsers, analysis, botName,
            windowStart, windowEnd, theme);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string htmlPath = Path.Combine(reportDirectory, groupId + "_" + timestamp + ".html");
        string imagePath = Path.Combine(reportDirectory, groupId + "_" + timestamp + ".png");
        await File.WriteAllTextAsync(htmlPath, html, Encoding.UTF8);

        int estimatedHeight = 420
            + analysis.Topics.Count * 78
            + Math.Min(topUsers.Count, Configuration.TopUserCount) * 44
            + analysis.Quotes.Count * 86
            + 200;
        string windowSize = "--window-size=760," + Math.Clamp(estimatedHeight, 800, 3000) + " ";
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
            if (File.Exists(imagePath))
            {
                await SendReportImageAsync(groupId, imagePath);
                return true;
            }
            lastError = "无头渲染未产出截图：" + (browserStdError.Length > 300 ? browserStdError[..300] : browserStdError);
            LogDetail(lastError);
        }
        logger.LogWarning("日报渲染失败（浏览器: {Browser}）：{Error}", browserPath, lastError);
        return false;
    }

    async Task SendReportImageAsync(long groupId, string imagePath)
    {
        JsonObject imageSegment = new()
        {
            ["type"] = "image",
            ["data"] = new JsonObject { ["file"] = "file:///" + imagePath.Replace('\\', '/') }
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

    // 浏览器发现：注册表 App Paths（Windows 解析浏览器的标准途径）为主，常见安装路径兜底
    static string? FindBrowserExecutable()
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
        DateTime windowStart, DateTime windowEnd, string theme)
    {
        string themeName = theme switch
        {
            "sakura" => "樱花粉",
            "clover" => "幸运草绿",
            "diamond" => "钻石黑",
            "sky" => "天空蓝",
            _ => "暖黄手账"
        };

        StringBuilder topicsBuilder = new();
        if (analysis.Topics.Count == 0)
        {
            topicsBuilder.Append("<div class=\"topic\"><div class=\"t-name\">？</div></div>");
        }
        foreach (ReportTopic topic in analysis.Topics)
        {
            topicsBuilder.Append("<div class=\"topic\"><div class=\"t-name\">").Append(EscapeHtml(topic.Title))
                .Append("</div><div class=\"t-sum\">").Append(EscapeHtml(topic.Summary)).Append("</div></div>");
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
        foreach (ReportQuote quote in analysis.Quotes)
        {
            quotesBuilder.Append("<div class=\"quote\"><div class=\"q-text\">「").Append(EscapeHtml(quote.Text))
                .Append("」</div><div class=\"q-meta\">—— ").Append(EscapeHtml(quote.Who))
                .Append(" · ").Append(EscapeHtml(quote.Reason)).Append("</div></div>");
        }

        string dateRange = windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " ~ "
            + windowEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string groupAvatar = await LoadGroupAvatarDataUriAsync();

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
            .Replace("__COMMENT__", EscapeHtml(analysis.Comment.Length > 0 ? analysis.Comment : "今天大家聊得不错~"));
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

    async Task<string> LoadGroupAvatarDataUriAsync()
    {
        try
        {
            string cachePath = Path.Combine(avatarDirectory, "group_" + currentReportGroup + ".png");
            if (File.Exists(cachePath) == false
                || (DateTime.Now - File.GetLastWriteTime(cachePath)).TotalDays > 3)
            {
                using HttpResponseMessage avatarResponse = await sharedHttpClient.GetAsync(
                    "https://p.qlogo.cn/gh/" + currentReportGroup + "/" + currentReportGroup + "/0/");
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

    long currentReportGroup;

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
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }
        if (element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return 0;
        }
        return field.ValueKind switch
        {
            JsonValueKind.Number => field.GetInt64(),
            JsonValueKind.String => long.TryParse(field.GetString(), out long parsed) ? parsed : 0,
            _ => 0
        };
    }
}
