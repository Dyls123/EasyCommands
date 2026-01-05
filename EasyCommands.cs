using System;
using System.Collections.Generic;
using System.Globalization;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Easy Commands", "Dyls123", "3.0.0")]
    [Description("General utility commands: in-game time, skip night vote, wipe info, admin whisper, timers, and manual/broadcast wipe + skip vote.")]
    public class EasyCommands : CovalencePlugin
    {
        #region Configuration

        private PluginConfig config;

        private class GlobalSettings
        {
            public bool UsePrefix = false;
            public string Prefix = "[EasyCommands]";
            public string PrefixColor = "#00FFFF";
            public string MessageColor = "#FFFFFF";
        }

        private class TimeCommandSettings
        {
            public bool Enabled = true;
            public string Command = "!time";
            public string Message = "The current in-game time is: {Time}";
            public bool Broadcast = false;
            public string TimeFormat = "HH:mm";
            public string DateFormat = "yyyy-MM-dd";
            public bool ShowDate = true;
        }

        private class PopCommandSettings
        {
            public bool Enabled = true;
            public string Command = "!pop";
            public string Message = "There are {Count} players currently online.";
            public bool Broadcast = true;
        }

        private class SkipNightSettings
        {
            public bool Enabled = true;

            // What players type to vote, e.g. "!skip"
            public string Command = "!skip";

            // In-game HOUR when the vote automatically starts (0–23)
            public int VoteStartHour = 18;

            // Percentage of online players required to pass (1–100)
            public int RequiredPercentage = 100;

            // Hour to skip to when vote passes (e.g. 8 = 08:00 / 8 AM)
            public float SkipToHour = 8f;

            // Console/RCON/admin command to force start a vote
            public string ForceStartCommand = "ec.startskipvote";

            // Whether to auto-start a vote each night
            public bool AutoStart = true;

            // Chat messages (all editable)
            public string MsgVoteStarted = "A vote to skip the night has started! Type {Command} in chat to vote.";
            public string MsgNoActiveVote = "There is no active skip vote right now.";
            public string MsgAlreadyVoted = "You have already voted to skip the night.";
            public string MsgVoteProgress = "Skip vote: {Votes}/{Required} players ({Percentage}%) have voted to skip.";
            public string MsgSkipSuccess = "Vote passed! Skipping to day...";
            public string MsgSkipFail = "Vote failed. Not enough votes to skip the night.";
        }

        private class WhisperSettings
        {
            public bool Enabled = true;

            // Console/RCON command name (used in RustAdmin console)
            public string ConsoleCommand = "ec.whisper";

            // Message shown to the target player
            // {SenderName}, {TargetName}, {Message}
            public string MsgToTarget = "{SenderName}: {Message}";

            // Message shown back to the sender (admin/console player)
            public string MsgToSender = "To {TargetName}: {Message}";
        }

        private class TimerSettings
        {
            public bool Enabled = true;

            // Chat command, e.g. "!timer"
            public string Command = "!timer";

            // Maximum active timers per player
            public int MaxTimersPerPlayer = 3;

            // Messages
            public string MsgTimerCreated = "Timer {Label} set for {Duration}.";
            public string MsgTimerExpired = "Timer {Label} is up!";
            public string MsgTooManyTimers = "You already have the maximum number of active timers.";
            public string MsgInvalidSyntax = "Usage: !timer <duration> <label>. Example: !timer 10m Create";
            public string MsgInvalidDuration = "Invalid duration. Use formats like 30s, 5m, 1h.";
        }

        private class WipeSettings
        {
            public bool Enabled = true;

            // Chat command, e.g. "!wipe"
            public string Command = "!wipe";

            // Message template; {WipeTime} will be replaced
            public string Message = "Next wipe: {WipeTime}";
            public bool Broadcast = false;

            // How we calculate wipe:
            // "Static", "Weekly" (with WeeksCount), "MonthlyByDate", "MonthlyByDay"
            public string Mode = "Weekly";
            public int WeeksCount = 2; // number of weeks for Weekly mode

            // STATIC mode
            public string StaticDateTime = "2026-01-01 18:00";
            public string StaticInputFormat = "yyyy-MM-dd HH:mm";

            // WEEKLY / FORTNIGHTLY modes
            // Known wipe date that follows the pattern
            public string AnchorDateTime = "2026-01-01 18:00";
            public string AnchorInputFormat = "yyyy-MM-dd HH:mm";

            // MONTHLY BY DATE mode
            public int DayOfMonth = 1;          // 1–31
            public string TimeOfDay = "18:00";  // HH:mm

            // MONTHLY BY DAY mode (e.g. last Thursday)
            public string DayOfWeek = "Thursday";       // Monday..Sunday
            public string Position = "First";            // First, Second, Third, Fourth, Last
            public string TimeOfDayMonthly = "18:00";   // HH:mm

            // Display format for players
            public string OutputFormat = "dddd, dd MMM yyyy HH:mm";

            // Console/RCON/admin command to broadcast wipe info
            public string BroadcastCommand = "ec.broadcastwipe";
        }

        private class PluginConfig
        {
            public GlobalSettings Global = new GlobalSettings();
            public TimeCommandSettings Time = new TimeCommandSettings();
            public PopCommandSettings Pop = new PopCommandSettings();
            public SkipNightSettings SkipNight = new SkipNightSettings();
            public WhisperSettings Whisper = new WhisperSettings();
            public TimerSettings Timer = new TimerSettings();
            public WipeSettings Wipe = new WipeSettings();
            public WipeSettings BPWipe = new WipeSettings
            {
                Command = "!bpwipe",
                Message = "Next BP wipe: {WipeTime}",
                Broadcast = false,
                Mode = "Weekly",
                WeeksCount = 2,
                StaticDateTime = "2026-01-01 18:00",
                AnchorDateTime = "2026-01-01 18:00",
                BroadcastCommand = "ec.broadcastbpwipe"
            };
        }

        protected override void LoadDefaultConfig()
        {
            config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<PluginConfig>();
                if (config == null)
                {
                    PrintWarning("Config empty, creating a new one.");
                    LoadDefaultConfig();
                }
            }
            catch
            {
                PrintError("Error loading config, creating default file.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        #endregion

        #region Vote state

        private bool voteInProgress;
        private bool voteScheduledThisDay; // have we started today's vote yet?
        private readonly HashSet<string> skipVoters = new HashSet<string>();

        #endregion

        #region Timer state

        private class PlayerTimerInfo
        {
            public string Label;
            public DateTime EndTime;
            public double DurationSeconds;
        }

        private readonly Dictionary<string, List<PlayerTimerInfo>> activeTimers =
            new Dictionary<string, List<PlayerTimerInfo>>();

        #endregion

        #region Hooks

        private void Init()
        {
            ValidateConfig();

            PrintWarning(
                $"EasyCommands loaded. " +
                $"Time=\"{config.Time.Command}\" (enabled={config.Time.Enabled}), " +
                $"Skip=\"{config.SkipNight.Command}\" (enabled={config.SkipNight.Enabled}, scheduled at {config.SkipNight.VoteStartHour}:00, auto={config.SkipNight.AutoStart}), " +
                $"ForceSkip=\"{config.SkipNight.ForceStartCommand}\", " +
                $"Timer=\"{config.Timer.Command}\" (enabled={config.Timer.Enabled}, max per player={config.Timer.MaxTimersPerPlayer}), " +
                $"Wipe=\"{config.Wipe.Command}\" (enabled={config.Wipe.Enabled}), " +
                $"BPWipe=\"{config.BPWipe.Command}\" (enabled={config.BPWipe.Enabled}), " +
                $"WipeBroadcast=\"{config.Wipe.BroadcastCommand}\", " +
                $"BPWipeBroadcast=\"{config.BPWipe.BroadcastCommand}\", " +
                $"Whisper=\"{config.Whisper.ConsoleCommand}\" (enabled={config.Whisper.Enabled})"
            );

            // Register whisper console/command if enabled
            if (config.Whisper.Enabled && !string.IsNullOrWhiteSpace(config.Whisper.ConsoleCommand))
            {
                AddCovalenceCommand(config.Whisper.ConsoleCommand, "CmdWhisper");
            }

            // Register force-start skip-night command if enabled
            if (config.SkipNight.Enabled && !string.IsNullOrWhiteSpace(config.SkipNight.ForceStartCommand))
            {
                AddCovalenceCommand(config.SkipNight.ForceStartCommand, "CmdForceStartSkipVote");
            }

            // Register wipe broadcast command if wipe is enabled
            if (config.Wipe.Enabled && !string.IsNullOrWhiteSpace(config.Wipe.BroadcastCommand))
            {
                AddCovalenceCommand(config.Wipe.BroadcastCommand, "CmdBroadcastWipe");
            }
            if (config.BPWipe.Enabled && !string.IsNullOrWhiteSpace(config.BPWipe.BroadcastCommand))
            {
                AddCovalenceCommand(config.BPWipe.BroadcastCommand, "CmdBroadcastBPWipe");
            }

            // Start the scheduled skip-night checker (only if AutoStart)
            StartSkipNightTimer();
        }

        private void ValidateConfig()
        {
            if (config.Wipe == null) config.Wipe = new WipeSettings();
            if (config.BPWipe == null) config.BPWipe = new WipeSettings { Command = "!bpwipe", Message = "Next BP wipe: {WipeTime}", BroadcastCommand = "ec.broadcastbpwipe" };
            NormalizeWipeSettings(config.Wipe, "!wipe", "ec.broadcastwipe", "Next wipe: {WipeTime}");
            NormalizeWipeSettings(config.BPWipe, "!bpwipe", "ec.broadcastbpwipe", "Next BP wipe: {WipeTime}");

            if (string.IsNullOrWhiteSpace(config.Time.Command))
                config.Time.Command = "!time";
            if (config.Pop == null) config.Pop = new PopCommandSettings();
            if (string.IsNullOrWhiteSpace(config.Pop.Command))
                config.Pop.Command = "!pop";

            if (string.IsNullOrWhiteSpace(config.SkipNight.Command))
                config.SkipNight.Command = "!skip";

            if (string.IsNullOrWhiteSpace(config.SkipNight.ForceStartCommand))
                config.SkipNight.ForceStartCommand = "ec.startskipvote";

            if (string.IsNullOrWhiteSpace(config.Whisper.ConsoleCommand))
                config.Whisper.ConsoleCommand = "ec.whisper";

            if (string.IsNullOrWhiteSpace(config.Timer.Command))
                config.Timer.Command = "!timer";

            if (config.Timer.MaxTimersPerPlayer < 1)
                config.Timer.MaxTimersPerPlayer = 1;

            if (string.IsNullOrWhiteSpace(config.Timer.MsgTimerCreated))
                config.Timer.MsgTimerCreated = "Timer {Label} set for {Duration}.";

            if (string.IsNullOrWhiteSpace(config.Timer.MsgTimerExpired))
                config.Timer.MsgTimerExpired = "Timer {Label} is up!";

            if (string.IsNullOrWhiteSpace(config.Timer.MsgTooManyTimers))
                config.Timer.MsgTooManyTimers = "You already have the maximum number of active timers.";

            if (string.IsNullOrWhiteSpace(config.Timer.MsgInvalidSyntax))
                config.Timer.MsgInvalidSyntax = "Usage: !timer <duration> <label>. Example: !timer 10m Create";

            if (string.IsNullOrWhiteSpace(config.Timer.MsgInvalidDuration))
                config.Timer.MsgInvalidDuration = "Invalid duration. Use formats like 30s, 5m, 1h.";

            if (config.SkipNight.RequiredPercentage < 1)
                config.SkipNight.RequiredPercentage = 1;
            if (config.SkipNight.RequiredPercentage > 100)
                config.SkipNight.RequiredPercentage = 100;

            if (config.SkipNight.VoteStartHour < 0)
                config.SkipNight.VoteStartHour = 0;
            if (config.SkipNight.VoteStartHour > 23)
                config.SkipNight.VoteStartHour = 23;

            if (config.SkipNight.SkipToHour < 0f)
                config.SkipNight.SkipToHour = 0f;
            if (config.SkipNight.SkipToHour > 24f)
                config.SkipNight.SkipToHour = 24f;

            SaveConfig();
        }

        private void NormalizeWipeSettings(WipeSettings s, string defaultCommand, string defaultBroadcast, string defaultMessage)
        {
            if (string.IsNullOrWhiteSpace(s.Command))
                s.Command = defaultCommand;
            if (string.IsNullOrWhiteSpace(s.Message))
                s.Message = defaultMessage;
            if (string.IsNullOrWhiteSpace(s.Mode))
                s.Mode = "Static";
            if (s.WeeksCount < 1) s.WeeksCount = 1;
            if (string.IsNullOrWhiteSpace(s.StaticInputFormat))
                s.StaticInputFormat = "yyyy-MM-dd HH:mm";
            if (string.IsNullOrWhiteSpace(s.AnchorInputFormat))
                s.AnchorInputFormat = "yyyy-MM-dd HH:mm";
            if (string.IsNullOrWhiteSpace(s.OutputFormat))
                s.OutputFormat = "dddd, dd MMM yyyy HH:mm";
            if (string.IsNullOrWhiteSpace(s.TimeOfDay))
                s.TimeOfDay = "18:00";
            if (string.IsNullOrWhiteSpace(s.TimeOfDayMonthly))
                s.TimeOfDayMonthly = "18:00";
            if (string.IsNullOrWhiteSpace(s.DayOfWeek))
                s.DayOfWeek = "Thursday";
            if (string.IsNullOrWhiteSpace(s.Position))
                s.Position = "Last";
            if (string.IsNullOrWhiteSpace(s.BroadcastCommand))
                s.BroadcastCommand = defaultBroadcast;
        }

        private void StartSkipNightTimer()
        {
            if (!config.SkipNight.Enabled || !config.SkipNight.AutoStart)
            {
                Puts("[EasyCommands] SkipNight auto-start timer is disabled.");
                return;
            }

            // Clear any old state
            voteInProgress = false;
            voteScheduledThisDay = false;
            skipVoters.Clear();

            // Check every 10 seconds if it's time to start the scheduled vote
            timer.Every(10f, CheckSkipVoteSchedule);
            Puts("[EasyCommands] SkipNight auto-start timer initialized.");
        }

        /// <summary>
        /// Called whenever a user sends a chat message.
        /// We match against the configured chat commands (time, skip vote, wipe, timer).
        /// </summary>
        private object OnUserChat(IPlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message))
                return null;

            var msg = message.Trim();

            // Skip Night vote command: ONLY casts a vote now
            if (config.SkipNight.Enabled &&
                msg.Equals(config.SkipNight.Command, StringComparison.OrdinalIgnoreCase))
            {
                HandleSkipNightVote(player);
                return true; // block original message
            }

            // Time command
            if (config.Time.Enabled &&
                msg.Equals(config.Time.Command, StringComparison.OrdinalIgnoreCase))
            {
                SendTimeMessage(player);
                return true; // block original message
            }

            // Pop command (online count)
            if (config.Pop.Enabled &&
                msg.Equals(config.Pop.Command, StringComparison.OrdinalIgnoreCase))
            {
                SendPopMessage(player);
                return true; // block original message
            }

            // Wipe command
            if (config.Wipe.Enabled &&
                msg.Equals(config.Wipe.Command, StringComparison.OrdinalIgnoreCase))
            {
                SendWipeMessage(player, config.Wipe);
                return true; // block original message
            }

            // BP Wipe command
            if (config.BPWipe.Enabled &&
                msg.Equals(config.BPWipe.Command, StringComparison.OrdinalIgnoreCase))
            {
                SendWipeMessage(player, config.BPWipe);
                return true; // block original message
            }

            // Timer command (supports args: !timer 10m Create)
            if (config.Timer.Enabled &&
                msg.Length >= config.Timer.Command.Length &&
                msg.StartsWith(config.Timer.Command, StringComparison.OrdinalIgnoreCase) &&
                (msg.Length == config.Timer.Command.Length || char.IsWhiteSpace(msg[config.Timer.Command.Length])))
            {
                string argsText = msg.Length == config.Timer.Command.Length
                    ? string.Empty
                    : msg.Substring(config.Timer.Command.Length).TrimStart();

                HandleTimerCommand(player, argsText);
                return true; // block original message
            }

            return null;
        }

        #endregion

        #region Scheduled skip vote

        private void CheckSkipVoteSchedule()
        {
            if (!config.SkipNight.Enabled || !config.SkipNight.AutoStart)
                return;

            var sky = TOD_Sky.Instance;
            if (sky == null)
                return;

            float hour = sky.Cycle.Hour;

            // 1) If we're before the vote start hour, treat it as a "new day"
            if (hour < config.SkipNight.VoteStartHour - 0.01f)
            {
                if (voteScheduledThisDay || voteInProgress)
                {
                    voteScheduledThisDay = false;
                    voteInProgress = false;
                    skipVoters.Clear();
                }
                return;
            }

            // 2) If we've already scheduled today's vote, do nothing
            if (voteScheduledThisDay)
                return;

            // 3) First time we hit or pass the start hour → start the vote
            StartSkipVote(sky);
        }

        private void StartSkipVote(TOD_Sky sky)
        {
            voteInProgress = true;
            voteScheduledThisDay = true;
            skipVoters.Clear();

            var dt = sky.Cycle.DateTime;
            Puts($"[EasyCommands] Starting skip-night vote at in-game time {dt:yyyy-MM-dd HH:mm}.");

            string started = config.SkipNight.MsgVoteStarted
                .Replace("{Command}", config.SkipNight.Command);

            BroadcastMessage(started);
        }

        private void HandleSkipNightVote(IPlayer player)
        {
            var sky = TOD_Sky.Instance;
            if (sky == null)
            {
                player.Message(FormatMessage("Time system not available."));
                return;
            }

            if (!voteInProgress)
            {
                player.Message(FormatMessage(config.SkipNight.MsgNoActiveVote));
                return;
            }

            // Already voted?
            if (skipVoters.Contains(player.Id))
            {
                player.Message(FormatMessage(config.SkipNight.MsgAlreadyVoted));
                return;
            }

            skipVoters.Add(player.Id);

            int onlineCount = GetOnlinePlayerCount();
            if (onlineCount <= 0)
                return;

            int requiredVotes = CalculateRequiredVotes(onlineCount);
            int votes = skipVoters.Count;
            int percent = (int)Math.Round((double)votes / onlineCount * 100.0);

            string progress = config.SkipNight.MsgVoteProgress
                .Replace("{Votes}", votes.ToString())
                .Replace("{Required}", requiredVotes.ToString())
                .Replace("{Percentage}", percent.ToString());

            BroadcastMessage(progress);

            if (votes >= requiredVotes)
            {
                BroadcastMessage(config.SkipNight.MsgSkipSuccess);
                SkipToDay(sky);
                voteInProgress = false;
                skipVoters.Clear();
            }
        }

        private int GetOnlinePlayerCount()
        {
            int count = 0;
            foreach (var p in players.Connected)
                count++;
            return count;
        }

        private int CalculateRequiredVotes(int onlineCount)
        {
            if (onlineCount <= 0)
                return 1;

            double required = onlineCount * (config.SkipNight.RequiredPercentage / 100.0);
            return Math.Max(1, (int)Math.Ceiling(required));
        }

        /// <summary>
        /// Safely skip ahead to the configured day hour by advancing DateTime,
        /// rather than bluntly setting Hour, which can confuse systems that track full in-game time.
        /// </summary>
        private void SkipToDay(TOD_Sky sky)
        {
            double targetHour = config.SkipNight.SkipToHour;
            double currentHour = sky.Cycle.Hour;

            // Move forward to the next occurrence of targetHour (wrap around 24h)
            double deltaHours = (24d + targetHour - currentHour) % 24d;

            // If we're already basically there, don't bother
            if (deltaHours < 0.01d)
            {
                Puts("[EasyCommands] SkipToDay: already at target hour, no time skip needed.");
                return;
            }

            DateTime before = sky.Cycle.DateTime;
            DateTime after = before.AddHours(deltaHours);
            sky.Cycle.DateTime = after;

            Puts($"[EasyCommands] Skipped night safely by advancing DateTime {deltaHours:0.00} hours ({before:yyyy-MM-dd HH:mm} -> {after:yyyy-MM-dd HH:mm}).");
        }

        #endregion

        #region Time command

        private void SendTimeMessage(IPlayer player)
        {
            string timeOutput = GetInGameTimeString();

            string message = config.Time.Message.Replace("{Time}", timeOutput);

            SendPerScopeMessage(message, config.Time.Broadcast, player);
        }

        private void SendPopMessage(IPlayer player)
        {
            int online = GetOnlinePlayerCount();
            string message = config.Pop.Message.Replace("{Count}", online.ToString());
            SendPerScopeMessage(message, config.Pop.Broadcast, player);
        }

        private string GetInGameTimeString()
        {
            var sky = TOD_Sky.Instance;

            DateTime time = sky != null
                ? sky.Cycle.DateTime
                : DateTime.Now;

            // If ShowDate is false, ONLY show time
            if (!config.Time.ShowDate)
                return time.ToString(config.Time.TimeFormat);

            // If ShowDate is true, include date + time
            return time.ToString($"{config.Time.DateFormat} {config.Time.TimeFormat}");
        }

        #endregion

        #region Timer command

        private void HandleTimerCommand(IPlayer player, string argsText)
        {
            if (string.IsNullOrWhiteSpace(argsText))
            {
                player.Message(FormatMessage(config.Timer.MsgInvalidSyntax));
                return;
            }

            // Split "<duration> <label...>"
            var parts = argsText.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                player.Message(FormatMessage(config.Timer.MsgInvalidSyntax));
                return;
            }

            string durationToken = parts[0];
            string label = parts.Length > 1 ? parts[1].Trim() : "Timer";

            if (!TryParseDuration(durationToken, out double seconds, out string niceDuration))
            {
                player.Message(FormatMessage(config.Timer.MsgInvalidDuration));
                return;
            }

            if (seconds <= 0)
            {
                player.Message(FormatMessage(config.Timer.MsgInvalidDuration));
                return;
            }

            if (!activeTimers.TryGetValue(player.Id, out var list))
            {
                list = new List<PlayerTimerInfo>();
                activeTimers[player.Id] = list;
            }

            if (list.Count >= config.Timer.MaxTimersPerPlayer)
            {
                player.Message(FormatMessage(config.Timer.MsgTooManyTimers));
                return;
            }

            var info = new PlayerTimerInfo
            {
                Label = label,
                EndTime = DateTime.UtcNow.AddSeconds(seconds),
                DurationSeconds = seconds
            };

            list.Add(info);

            string createdMsg = config.Timer.MsgTimerCreated
                .Replace("{Label}", label)
                .Replace("{Duration}", niceDuration);

            player.Message(FormatMessage(createdMsg));

            // Schedule the timer
            timer.Once((float)seconds, () =>
            {
                // Remove from active list
                if (activeTimers.TryGetValue(player.Id, out var timersForPlayer))
                {
                    timersForPlayer.Remove(info);
                    if (timersForPlayer.Count == 0)
                        activeTimers.Remove(player.Id);
                }

                var target = players.FindPlayer(player.Id);
                if (target == null)
                    return;

                string expiredMsg = config.Timer.MsgTimerExpired
                    .Replace("{Label}", label);

                target.Message(FormatMessage(expiredMsg));
            });
        }

        // Parses "10m", "30s", "1h", or bare "10" as minutes.
        private bool TryParseDuration(string input, out double seconds, out string pretty)
        {
            seconds = 0;
            pretty = string.Empty;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            string s = input.Trim().ToLowerInvariant();
            if (s.Length == 0)
                return false;

            double multiplier;
            string numberPart;
            char last = s[s.Length - 1];

            switch (last)
            {
                case 's':
                    multiplier = 1;
                    numberPart = s.Substring(0, s.Length - 1);
                    break;
                case 'm':
                    multiplier = 60;
                    numberPart = s.Substring(0, s.Length - 1);
                    break;
                case 'h':
                    multiplier = 3600;
                    numberPart = s.Substring(0, s.Length - 1);
                    break;
                default:
                    // No suffix -> treat as minutes
                    multiplier = 60;
                    numberPart = s;
                    break;
            }

            if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return false;

            if (value <= 0)
                return false;

            seconds = value * multiplier;

            if (multiplier == 1)
                pretty = $"{value:0} seconds";
            else if (multiplier == 60)
                pretty = $"{value:0} minutes";
            else if (multiplier == 3600)
                pretty = $"{value:0} hours";
            else
                pretty = $"{value:0} units";

            return true;
        }

        #endregion

        #region Wipe command

        private void SendWipeMessage(IPlayer player, WipeSettings settings)
        {
            string wipeTime = GetWipeTimeString(settings);

            string message = settings.Message.Replace("{WipeTime}", wipeTime);

            SendPerScopeMessage(message, settings.Broadcast, player);
        }

        private string GetWipeTimeString(WipeSettings settings)
        {
            try
            {
                DateTime now = DateTime.Now;
                DateTime next;

                switch (settings.Mode)
                {
                    case "Weekly":
                        next = GetNextFromAnchor(now, settings.WeeksCount * 7, settings);
                        break;
                    case "Fortnightly": // legacy; treat as 2-week schedule
                        next = GetNextFromAnchor(now, 14, settings);
                        break;
                    case "MonthlyByDate":
                        next = GetNextMonthlyByDate(now, settings);
                        break;
                    case "MonthlyByDay":
                        next = GetNextMonthlyByDay(now, settings);
                        break;
                    case "Static":
                    default:
                        next = ParseStaticDate(settings);
                        break;
                }

                return next.ToString(settings.OutputFormat);
            }
            catch (Exception ex)
            {
                Puts($"[EasyCommands] Failed to calculate wipe time: {ex.Message}");
                return "not configured";
            }
        }

        private DateTime ParseStaticDate(WipeSettings settings)
        {
            return DateTime.ParseExact(
                settings.StaticDateTime,
                settings.StaticInputFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal
            );
        }

        private DateTime GetNextFromAnchor(DateTime now, int periodDays, WipeSettings settings)
        {
            if (periodDays < 1) periodDays = 1;
            DateTime anchor = DateTime.ParseExact(
                settings.AnchorDateTime,
                settings.AnchorInputFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal
            );

            if (anchor >= now)
                return anchor;

            double daysDiff = (now - anchor).TotalDays;
            int periodsPassed = (int)Math.Floor(daysDiff / periodDays);
            DateTime candidate = anchor.AddDays((periodsPassed + 1) * periodDays);
            return candidate;
        }

        private DateTime GetNextMonthlyByDate(DateTime now, WipeSettings settings)
        {
            int year = now.Year;
            int month = now.Month;

            DateTime candidate = BuildMonthlyDate(year, month, settings.DayOfMonth, settings.TimeOfDay);

            if (candidate <= now)
            {
                // Move to next month
                if (month == 12)
                {
                    year++;
                    month = 1;
                }
                else
                {
                    month++;
                }
                candidate = BuildMonthlyDate(year, month, settings.DayOfMonth, settings.TimeOfDay);
            }

            return candidate;
        }

        private DateTime BuildMonthlyDate(int year, int month, int dayOfMonth, string timeOfDay)
        {
            int daysInMonth = DateTime.DaysInMonth(year, month);
            int day = Math.Min(Math.Max(dayOfMonth, 1), daysInMonth);

            TimeSpan time = TimeSpan.ParseExact(timeOfDay, @"hh\:mm", CultureInfo.InvariantCulture);

            return new DateTime(year, month, day, time.Hours, time.Minutes, 0);
        }

        private DateTime GetNextMonthlyByDay(DateTime now, WipeSettings settings)
        {
            int year = now.Year;
            int month = now.Month;

            DateTime candidate = BuildMonthlyByDay(year, month, settings);

            if (candidate <= now)
            {
                // Next month
                if (month == 12)
                {
                    year++;
                    month = 1;
                }
                else
                {
                    month++;
                }
                candidate = BuildMonthlyByDay(year, month, settings);
            }

            return candidate;
        }

        private DateTime BuildMonthlyByDay(int year, int month, WipeSettings settings)
        {
            DayOfWeek targetDay = ParseDayOfWeek(settings.DayOfWeek);
            TimeSpan time = TimeSpan.ParseExact(
                settings.TimeOfDayMonthly,
                @"hh\:mm",
                CultureInfo.InvariantCulture
            );

            // Find all occurrences of that weekday in the month
            List<DateTime> matches = new List<DateTime>();

            DateTime d = new DateTime(year, month, 1);
            int daysInMonth = DateTime.DaysInMonth(year, month);

            for (int day = 1; day <= daysInMonth; day++)
            {
                if (d.DayOfWeek == targetDay)
                    matches.Add(d);

                d = d.AddDays(1);
            }

            if (matches.Count == 0)
                throw new Exception("No matching weekday found in month (this should not happen).");

            DateTime selected;

            switch (settings.Position)
            {
                case "First":
                    selected = matches[0];
                    break;
                case "Second":
                    selected = matches.Count >= 2 ? matches[1] : matches[matches.Count - 1];
                    break;
                case "Third":
                    selected = matches.Count >= 3 ? matches[2] : matches[matches.Count - 1];
                    break;
                case "Fourth":
                    selected = matches.Count >= 4 ? matches[3] : matches[matches.Count - 1];
                    break;
                case "Last":
                default:
                    selected = matches[matches.Count - 1];
                    break;
            }

            return new DateTime(
                selected.Year,
                selected.Month,
                selected.Day,
                time.Hours,
                time.Minutes,
                0
            );
        }

        private DayOfWeek ParseDayOfWeek(string name)
        {
            return (DayOfWeek)Enum.Parse(typeof(DayOfWeek), name, ignoreCase: true);
        }

        #endregion

        #region Whisper command (console/admin)

        // Covalence command: works from server console, RCON (RustAdmin), and in-game chat (/ec.whisper ...)
        private void CmdWhisper(IPlayer caller, string command, string[] args)
        {
            if (!config.Whisper.Enabled)
            {
                caller?.Message(FormatMessage("Whisper command is disabled."));
                return;
            }

            if (args == null || args.Length < 2)
            {
                string usage = $"Usage: {config.Whisper.ConsoleCommand} <playerNameOrId> <message>";
                if (caller != null)
                    caller.Message(FormatMessage(usage));
                else
                    Puts(usage);
                return;
            }

            string targetSearch = args[0];
            string messageText = string.Join(" ", args, 1, args.Length - 1);

            var target = players.FindPlayer(targetSearch);
            if (target == null)
            {
                string notFound = $"No player found matching '{targetSearch}'.";
                if (caller != null)
                    caller.Message(FormatMessage(notFound));
                else
                    Puts("[Whisper] " + notFound);
                return;
            }

            string senderName = caller?.Name ?? "Server";

            string toTarget = config.Whisper.MsgToTarget
                .Replace("{SenderName}", senderName)
                .Replace("{TargetName}", target.Name)
                .Replace("{Message}", messageText);

            string toSender = config.Whisper.MsgToSender
                .Replace("{SenderName}", senderName)
                .Replace("{TargetName}", target.Name)
                .Replace("{Message}", messageText);

            target.Message(FormatMessage(toTarget));

            if (caller != null)
                caller.Message(FormatMessage(toSender));
            else
                Puts($"[Whisper to {target.Name}] {messageText}");
        }

        #endregion

        #region Force start skip vote command (console/admin)

        // Covalence command: force start a skip-night vote manually
        private void CmdForceStartSkipVote(IPlayer caller, string command, string[] args)
        {
            if (!config.SkipNight.Enabled)
            {
                caller?.Message(FormatMessage("Skip night voting is disabled."));
                return;
            }

            var sky = TOD_Sky.Instance;
            if (sky == null)
            {
                caller?.Message(FormatMessage("Time system not available."));
                return;
            }

            if (voteInProgress)
            {
                caller?.Message(FormatMessage("A skip-night vote is already in progress."));
                return;
            }

            StartSkipVote(sky);

            caller?.Message(FormatMessage("Skip-night vote force started."));
        }

        #endregion

        #region Broadcast wipe command (console/admin)

        // Console/RCON/admin command: broadcast the next wipe to everyone
        private void CmdBroadcastWipe(IPlayer caller, string command, string[] args)
        {
            if (!config.Wipe.Enabled)
            {
                caller?.Message(FormatMessage("Wipe system is disabled."));
                return;
            }

            string wipeTime = GetWipeTimeString(config.Wipe);
            string message = config.Wipe.Message.Replace("{WipeTime}", wipeTime);

            BroadcastMessage(message);

            caller?.Message(FormatMessage("Broadcasted wipe time to all players."));
        }

        // Console/RCON/admin command: broadcast the next BP wipe to everyone
        private void CmdBroadcastBPWipe(IPlayer caller, string command, string[] args)
        {
            if (!config.BPWipe.Enabled)
            {
                caller?.Message(FormatMessage("BP wipe system is disabled."));
                return;
            }

            string wipeTime = GetWipeTimeString(config.BPWipe);
            string message = config.BPWipe.Message.Replace("{WipeTime}", wipeTime);

            BroadcastMessage(message);

            caller?.Message(FormatMessage("Broadcasted BP wipe time to all players."));
        }

        #endregion

        #region Helpers

        private string FormatMessage(string message)
        {
            if (!config.Global.UsePrefix)
                return $"<color={config.Global.MessageColor}>{message}</color>";

            return $"<color={config.Global.PrefixColor}>{config.Global.Prefix}</color> " +
                   $"<color={config.Global.MessageColor}>{message}</color>";
        }

        private void BroadcastMessage(string message)
        {
            string final = FormatMessage(message);
            server.Broadcast(final);
        }

        private void SendPerScopeMessage(string message, bool broadcast, IPlayer player)
        {
            string final = FormatMessage(message);
            if (broadcast)
                server.Broadcast(final);
            else
                player.Message(final);
        }

        #endregion
    }
}
