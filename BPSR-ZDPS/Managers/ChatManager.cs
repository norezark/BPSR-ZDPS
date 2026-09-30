using BPSR_ZDPS.DataTypes;
using BPSR_ZDPS.DataTypes.Chat;
using BPSR_ZDPS.Features.ChatHistory;
using BPSR_ZDPSLib;
using System.Collections.Concurrent;
using Zproto;
using static Zproto.ChitChatNtf.Types;

namespace BPSR_ZDPS.Managers
{
    public class ChatManager
    {
        public static ConcurrentDictionary<long, ChatMessage> Messages = [];
        public static ConcurrentDictionary<long, User> Senders = [];
        public static List<ChatTab> ChatTabs = [];
        private static long SessionMsgIdSrc;
        private static Guid captureRunId;
        private static ChatHistoryStore? history;
        // ChatTabs is the outer gate for the display projection and reload lifecycle.
        // SQLite access is always outside this gate, off packet/render threads.
        private static readonly Dictionary<long, ChatHistoryEntry> visibleEntries = [];
        private static readonly HashSet<Guid> rejectedDisplayEvents = [];
        private static readonly Dictionary<long, int> senderVisibleCounts = [];
        private static readonly Dictionary<ChitChatChannelType, Queue<long>> channelMessageIds = [];
        private static bool shuttingDown;
        private static bool reloadRunning;
        private static int reloadVersion;
        private static int reloadWorkerId;
        private static int displayCap = 200;
        private static string? restoreError;
        private static Task historyLoadTask = Task.CompletedTask;

        public static event Action<User, ChatMessage, NotifyNewestChitChatMsgs>? OnChatMessage;
        public static ChatHistoryStatus? HistoryStatus => history?.Status;
        public static string? HistoryRestoreError => Volatile.Read(ref restoreError);
        public static Task HistoryLoadTask { get { lock (ChatTabs) return historyLoadTask; } }

        public static void InitializeHistory(string? databasePath = null, int maxPendingMessages = 10000)
        {
            lock (ChatTabs)
            {
                if (history != null && !shuttingDown) return;
                if (history != null && (!history.Status.IsStopped || !historyLoadTask.IsCompleted))
                    throw new InvalidOperationException("Wait for chat history shutdown to finish before initializing another store.");
                history = new ChatHistoryStore(databasePath ?? ChatHistoryStore.DefaultDatabasePath, maxPendingMessages);
                captureRunId = Guid.NewGuid();
                shuttingDown = false;
                displayCap = Math.Max(1, Settings.Instance.Chat.MaxChatHistory);
                ClearDisplay();
                restoreError = null;
                StartReloadLocked();
            }
        }

        public static Task ReloadHistoryAsync()
        {
            lock (ChatTabs)
            {
                displayCap = Math.Max(1, Settings.Instance.Chat.MaxChatHistory);
                return StartReloadLocked();
            }
        }

        private static Task StartReloadLocked()
        {
            if (history == null || shuttingDown) return Task.CompletedTask;
            reloadVersion++;
            if (reloadRunning) return historyLoadTask;
            reloadRunning = true;
            int workerId = ++reloadWorkerId;
            historyLoadTask = Task.Run(() => ReloadLoop(workerId));
            return historyLoadTask;
        }

        private static void ReloadLoop(int workerId)
        {
            try
            {
                while (true)
                {
                    ChatHistoryStore store;
                    int version, cap;
                    long displayFence;
                    lock (ChatTabs)
                    {
                        if (shuttingDown || history == null) return;
                        store = history; version = reloadVersion; cap = displayCap;
                        displayFence = SessionMsgIdSrc;
                    }
                    bool readSucceeded = store.TryReadRecent(cap, out var entries);
                    var decoded = new List<(ChatHistoryEntry Entry, NotifyNewestChitChatMsgs Message)>();
                    int invalid = 0;
                    foreach (var entry in entries)
                    {
                        try
                        {
                            var message = NotifyNewestChitChatMsgs.Parser.ParseFrom(entry.Payload);
                            if (!IsValid(message)) { invalid++; continue; }
                            decoded.Add((entry, message));
                        }
                        catch (Google.Protobuf.InvalidProtocolBufferException) { invalid++; }
                    }
                    lock (ChatTabs)
                    {
                        if (shuttingDown || !ReferenceEquals(store, history)) return;
                        if (version != reloadVersion) continue;
                        if (readSucceeded)
                        {
                            // Live events accepted during the disk read remain in the display.
                            // Their event IDs are stable, including while a write is retried.
                            var merged = new LinkedList<(ChatHistoryEntry Entry, NotifyNewestChitChatMsgs Message)>(decoded);
                            var nodes = new Dictionary<Guid, LinkedListNode<(ChatHistoryEntry Entry, NotifyNewestChitChatMsgs Message)>>();
                            for (var node = merged.First; node != null; node = node.Next) nodes[node.Value.Entry.EventId] = node;
                            LinkedListNode<(ChatHistoryEntry Entry, NotifyNewestChitChatMsgs Message)>? nextRetained = null;
                            foreach (var item in visibleEntries.OrderByDescending(x => x.Key))
                            {
                                if (nodes.TryGetValue(item.Value.EventId, out var known)) { nextRetained = known; continue; }
                                if (item.Key <= displayFence && !rejectedDisplayEvents.Contains(item.Value.EventId)) continue;
                                // Insert before the next known live event, retaining capture order
                                // even when an older event was rejected during a storage outage.
                                var restored = (item.Value, NotifyNewestChitChatMsgs.Parser.ParseFrom(item.Value.Payload));
                                nextRetained = nextRetained == null ? merged.AddLast(restored) : merged.AddBefore(nextRetained, restored);
                                nodes[item.Value.EventId] = nextRetained;
                            }
                            decoded = merged.ToList();
                            var rejectedIds = rejectedDisplayEvents.ToHashSet();
                            ClearDisplay();
                            rejectedDisplayEvents.UnionWith(rejectedIds);
                            foreach (var item in decoded) AddToDisplay(item.Message, item.Entry);
                            foreach (var tab in ChatTabs) RefilterChatTab(tab);
                            restoreError = invalid == 0 ? null : $"{invalid} saved chat message(s) could not be restored. The database was kept unchanged.";
                        }
                        else restoreError = "Chat history could not be restored. Live messages remain visible; check the storage error.";
                        reloadRunning = false;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref restoreError, $"Chat history could not be restored: {ex.Message}");
            }
            finally { lock (ChatTabs) if (workerId == reloadWorkerId) reloadRunning = false; }
        }

        private static bool IsValid(NotifyNewestChitChatMsgs message)
        {
            var chat = message.VRequest?.ChatMsg;
            return chat?.MsgInfo != null && chat.SendCharInfo != null &&
                (chat.MsgInfo.MsgType != ChitChatMsgType.ChatMsgPictureEmoji || chat.MsgInfo.PictureEmoji != null) &&
                chat.Timestamp >= -62135596800L && chat.Timestamp <= 253402300799L;
        }

        public static void ProcessChatMessage(ReadOnlySpan<byte> span, ExtraPacketData extraData)
        {
            if (!AppState.IsChatEnabled) return;
            var message = NotifyNewestChitChatMsgs.Parser.ParseFrom(span);
            if (!IsValid(message)) return;
            User sender;
            ChatMessage chatMessage;
            lock (ChatTabs)
            {
                if (shuttingDown) return;
                var chat = message.VRequest.ChatMsg;
                // Preserve the unmodified protobuf, sender metadata, target and original game ID.
                // A new captured event is distinct even if the server reuses IDs or text.
                var entry = new ChatHistoryEntry(Guid.NewGuid(), captureRunId, chat.MsgId,
                    (int)message.VRequest.ChannelType, chat.SendCharInfo.CharID, chat.MsgInfo.TargetId,
                    chat.Timestamp, extraData.ArrivalTime.ToUniversalTime(), span.ToArray());
                if (history?.Append(entry) == false) rejectedDisplayEvents.Add(entry.EventId);
                (sender, chatMessage) = AddToDisplay(message, entry);
            }
            // Restoring/refiltering never passes through this live-only notification path.
            OnChatMessage?.Invoke(sender, chatMessage, message);
        }

        private static (User, ChatMessage) AddToDisplay(NotifyNewestChitChatMsgs message, ChatHistoryEntry entry)
        {
            var chat = message.VRequest.ChatMsg;
            chat.MsgId = Interlocked.Increment(ref SessionMsgIdSrc);
            var chatMessage = new ChatMessage(chat.MsgInfo, message.VRequest.ChannelType,
                chat.SendCharInfo.CharID, chat.Timestamp);
            var sender = Senders.GetOrAdd(chat.SendCharInfo.CharID, _ => new User(chat.SendCharInfo));
            sender.Info = chat.SendCharInfo;
            senderVisibleCounts.TryGetValue(chat.SendCharInfo.CharID, out int visibleCount);
            senderVisibleCounts[chat.SendCharInfo.CharID] = visibleCount + 1;
            sender.NumSentMessages = visibleCount + 1;
            Messages[chat.MsgId] = chatMessage;
            visibleEntries[chat.MsgId] = entry;
            if (!channelMessageIds.TryGetValue(message.VRequest.ChannelType, out var ids))
                channelMessageIds[message.VRequest.ChannelType] = ids = new Queue<long>();
            ids.Enqueue(chat.MsgId);
            while (ids.Count > displayCap) RemoveFromDisplay(ids.Dequeue());
            foreach (var tab in ChatTabs)
                if (IsFilteredForChatTab(tab, chatMessage))
                    lock (tab.MessageIds) tab.MessageIds.Add(chat.MsgId);
            return (sender, chatMessage);
        }

        private static void RemoveFromDisplay(long id)
        {
            if (Messages.TryRemove(id, out var message) && senderVisibleCounts.TryGetValue(message.SenderId, out int count))
            {
                if (count <= 1)
                {
                    senderVisibleCounts.Remove(message.SenderId);
                    Senders.TryRemove(message.SenderId, out _);
                }
                else senderVisibleCounts[message.SenderId] = count - 1;
            }
            if (visibleEntries.Remove(id, out var entry)) rejectedDisplayEvents.Remove(entry.EventId);
            foreach (var tab in ChatTabs)
                lock (tab.MessageIds) tab.MessageIds.Remove(id);
        }

        private static void ClearDisplay()
        {
            Messages.Clear();
            visibleEntries.Clear();
            rejectedDisplayEvents.Clear();
            channelMessageIds.Clear();
            foreach (var tab in ChatTabs)
                lock (tab.MessageIds) tab.MessageIds.Clear();
            senderVisibleCounts.Clear();
            Senders.Clear();
        }

        public static void RemoveMessagesOverCap(int cap)
        {
            lock (ChatTabs)
            {
                displayCap = Math.Max(1, cap);
                foreach (var ids in channelMessageIds.Values)
                    while (ids.Count > displayCap) RemoveFromDisplay(ids.Dequeue());
                // The display cap never deletes saved history. Raising it reloads older rows.
                StartReloadLocked();
            }
        }

        public static bool ShutdownHistory()
        {
            ChatHistoryStore? store;
            Task reload;
            lock (ChatTabs)
            {
                shuttingDown = true;
                reloadVersion++;
                store = history;
                reload = historyLoadTask;
            }
            reload.GetAwaiter().GetResult();
            if (store == null) return true;
            return store.ShutdownAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }

        public static void AddChatTab(ChatTabConfig config)
        {
            lock (ChatTabs)
            {
                var tab = new ChatTab(config);
                ChatTabs.Add(tab);
                RefilterChatTab(tab);
            }
        }

        public static void RemoveChatTab(ChatTabConfig config)
        {
            lock (ChatTabs) ChatTabs.RemoveAll(x => x.Config.Id == config.Id);
        }

        public static bool IsFilteredForChatTab(ChatTab tab, ChatMessage msg)
        {
            if (Senders.TryGetValue(msg.SenderId, out var sender))
            {
                bool isBlocked = IsUserBlocked(msg.SenderId);
                if (isBlocked)
                {
                    return false;
                }

                bool isTextMsg = msg.Msg.MsgType == ChitChatMsgType.ChatMsgTextMessage;
                bool isFromChannel = tab.Config.Channels.Contains(msg.Channel);
                bool isOverLevel = sender.Info.Level >= tab.Config.OverLevel;

                if (!isTextMsg)
                {
                    if (msg.Msg.MsgType == ChitChatMsgType.ChatMsgPictureEmoji && Settings.Instance.WindowSettings.ChatWindow.HideStickers)
                    {
                        return false;
                    }

                    return isFromChannel && isOverLevel;
                }

                bool matchesContains = string.IsNullOrWhiteSpace(tab.Config.Contains) || Utils.SafeRegexIsMatch(msg.Msg.MsgText, tab.Config.Contains);
                bool matchesDoesNotContain = string.IsNullOrWhiteSpace(tab.Config.DoesNotContain) || !Utils.SafeRegexIsMatch(msg.Msg.MsgText, tab.Config.DoesNotContain);

                return isFromChannel && isOverLevel && matchesContains && matchesDoesNotContain;
            }

            return false;
        }

        public static void RefilterChatTab(ChatTab tab)
        {
            lock (ChatTabs)
            lock (tab.MessageIds)
            {
                tab.MessageIds.Clear();
                foreach (var msg in Messages.OrderBy(x => x.Value.TimeStamp).ThenBy(x => x.Key))
                {
                    if (IsFilteredForChatTab(tab, msg.Value))
                    {
                        tab.MessageIds.Add(msg.Key);
                    }
                }
            }
        }

        public static void RefilterAllTabs()
        {
            lock (ChatTabs)
                foreach (var tab in ChatTabs) RefilterChatTab(tab);
        }

        public static void BlockUser(User user)
        {
            var blockedUser = new UserBlock()
            {
                ID = user.Info.CharID,
                Name = user.Info.Name,
                BlockedAt = DateTime.Now
            };

            Settings.Instance.Chat.BlockedUsers.TryAdd(user.Info.CharID, blockedUser);
            RefilterAllTabs();
        }

        public static void UnblockUser(long userId)
        {
            Settings.Instance.Chat.BlockedUsers.TryRemove(userId, out var blockedUser);
            RefilterAllTabs();
        }

        public static bool IsUserBlocked(long userId)
        {
            bool isBlocked = Settings.Instance.Chat.BlockedUsers.ContainsKey(userId);
            return isBlocked;
        }
    }
}
