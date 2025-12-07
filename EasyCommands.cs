using System;
using System.Collections.Generic;
using System.Globalization;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Easy Commands", "Dyls123", "2.9.0")]
    [Description("General utility commands: in-game time, scheduled/optional skip night, wipe info, admin whisper, and manual/broadcast wipe + skip vote.")]
    public class EasyCommands : CovalencePlugin
    {
        #region Configuration

        private PluginConfig config;

        private class GlobalSettings
        {
            public bool UsePrefix = true;
            public string Prefix = "[EasyCommands]";
            public string PrefixColor = "#00FFFF";
            public string MessageColor = "#FFFFFF";
        }

        private class TimeCommandSettings
        {
            public bool Enabled = true;
            public string Command = "!time";
            public string Message = "The current in-game time is: {Time}";
            public string TimeFormat = "HH:mm";
            public string DateFormat = "yyyy-MM-dd";
            public bool ShowDate = true;
        }

        private class SkipNightSettings
        {
            public bool Enabled = true;

            // What players type to vote, e.g. "!skip"
            public string Command = "!skip";

            // In-game HOUR when the vote automatically starts (0–23)
            public int VoteStartHour = 18;

            // Percentage of online players required to pass (1–100)
            public int RequiredPercentage = 60;

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

        private class WipeSettings
        {
            public bool Enabled = true;

            // Chat command, e.g. "!wipe"
            public string Command = "!wipe";

            // Message template; {WipeTime} will be replaced
            public string Message = "Next wipe: {WipeTime}";

            // How we calculate wipe:
            // "Static", "Weekly", "Fortnightly", "MonthlyByDate", "MonthlyByDay"
            public string Mode = "Static";

            // STATIC mode
            public string StaticDateTime = "2025-01-01 18:00";
            public string StaticInputFormat = "yyyy-MM-dd HH:mm";

            // WEEKLY / FORTNIGHTLY modes
            // Known wipe date that follows the pattern
            public string AnchorDateTime = "2025-01-02 18:00";
            public string AnchorInputFormat = "yyyy-MM-dd HH:mm";

            // MONTHLY BY DATE mode
            public int DayOfMonth = 1;          // 1–31
            public string TimeOfDay = "18:00";  // HH:mm

            // MONTHLY BY DAY mode (e.g. last Thursday)
            public string DayOfWeek = "Thursday";       // Monday..Sunday
            public string Position = "Last";            // First, Second, Third, Fourth, Last
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
            public SkipNightSettings SkipNight = new SkipNightSettings();
            public WhisperSettings Whisper = new WhisperSettings();
            public WipeSettings Wipe = new WipeSettings();
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

        #region Hooks

        private void Init()
        {
            ValidateConfig();

            PrintWarning(
                $"EasyCommands loaded. " +
                $"Time=\"{config.Time.Command}\" (enabled={config.Time.Enabled}), " +
                $"Skip=\"{config.SkipNight.Command}\" (enabled={config.SkipNight.Enabled}, scheduled at {config.SkipNight.VoteStartHour}:00, auto={config.SkipNight.AutoStart}), " +
                $"ForceSkip=\"{config.SkipNight.ForceStartCommand}\", " +
                $"Wipe=\"{config.Wipe.Command}\" (enabled={config.Wipe.Enabled}), " +
                $"WipeBroadcast=\"{config.Wipe.BroadcastCommand}\", " +
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

            // Start the scheduled skip-night checker (only if AutoStart)
            StartSkipNightTimer();
        }

        private void ValidateConfig()
        {
            if (string.IsNullOrWhiteSpace(config.Time.Command))
                config.Time.Command = "!time";

            if (string.IsNullOrWhiteSpace(config.SkipNight.Command))
                config.SkipNight.Command = "!skip";

            if (string.IsNullOrWhiteSpace(config.SkipNight.ForceStartCommand))
                config.SkipNight.ForceStartCommand = "ec.startskipvote";

            if (string.IsNullOrWhiteSpace(config.Whisper.ConsoleCommand))
                config.Whisper.ConsoleCommand = "ec.whisper";

            if (string.IsNullOrWhiteSpace(config.Wipe.Command))
                config.Wipe.Command = "!wipe";

            if (string.IsNullOrWhiteSpace(config.Wipe.Mode))
                config.Wipe.Mode = "Static";

            if (string.IsNullOrWhiteSpace(config.Wipe.StaticInputFormat))
                config.Wipe.StaticInputFormat = "yyyy-MM-dd HH:mm";

            if (string.IsNullOrWhiteSpace(config.Wipe.AnchorInputFormat))
                config.Wipe.AnchorInputFormat = "yyyy-MM-dd HH:mm";

            if (string.IsNullOrWhiteSpace(config.Wipe.OutputFormat))
                config.Wipe.OutputFormat = "dddd, dd MMM yyyy HH:mm";

            if (string.IsNullOrWhiteSpace(config.Wipe.TimeOfDay))
                config.Wipe.TimeOfDay = "18:00";

            if (string.IsNullOrWhiteSpace(config.Wipe.TimeOfDayMonthly))
                config.Wipe.TimeOfDayMonthly = "18:00";

            if (string.IsNullOrWhiteSpace(config.Wipe.DayOfWeek))
                config.Wipe.DayOfWeek = "Thursday";

            if (string.IsNullOrWhiteSpace(config.Wipe.Position))
                config.Wipe.Position = "Last";

            if (string.IsNullOrWhiteSpace(config.Wipe.BroadcastCommand))
                config.Wipe.BroadcastCommand = "ec.broadcastwipe";

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
        /// We match against the configured chat commands (time, skip vote, wipe).
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

            // Wipe command
            if (config.Wipe.Enabled &&
                msg.Equals(config.Wipe.Command, StringComparison.OrdinalIgnoreCase))
            {
                SendWipeMessage(player);
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

            string final = FormatMessage(message);
            player.Message(final);
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

        #region Wipe command

        private void SendWipeMessage(IPlayer player)
        {
            string wipeTime = GetWipeTimeString();

            string message = config.Wipe.Message.Replace("{WipeTime}", wipeTime);

            string final = FormatMessage(message);
            player.Message(final);
        }

        private string GetWipeTimeString()
        {
            try
            {
                DateTime now = DateTime.Now;
                DateTime next;

                switch (config.Wipe.Mode)
                {
                    case "Weekly":
                        next = GetNextFromAnchor(now, 7);
                        break;
                    case "Fortnightly":
                        next = GetNextFromAnchor(now, 14);
                        break;
                    case "MonthlyByDate":
                        next = GetNextMonthlyByDate(now);
                        break;
                    case "MonthlyByDay":
                        next = GetNextMonthlyByDay(now);
                        break;
                    case "Static":
                    default:
                        next = ParseStaticDate();
                        break;
                }

                return next.ToString(config.Wipe.OutputFormat);
            }
            catch (Exception ex)
            {
                Puts($"[EasyCommands] Failed to calculate wipe time: {ex.Message}");
                return "not configured";
            }
        }

        private DateTime ParseStaticDate()
        {
            return DateTime.ParseExact(
                config.Wipe.StaticDateTime,
                config.Wipe.StaticInputFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal
            );
        }

        private DateTime GetNextFromAnchor(DateTime now, int periodDays)
        {
            DateTime anchor = DateTime.ParseExact(
                config.Wipe.AnchorDateTime,
                config.Wipe.AnchorInputFormat,
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

        private DateTime GetNextMonthlyByDate(DateTime now)
        {
            int year = now.Year;
            int month = now.Month;

            DateTime candidate = BuildMonthlyDate(year, month, config.Wipe.DayOfMonth, config.Wipe.TimeOfDay);

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
                candidate = BuildMonthlyDate(year, month, config.Wipe.DayOfMonth, config.Wipe.TimeOfDay);
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

        private DateTime GetNextMonthlyByDay(DateTime now)
        {
            int year = now.Year;
            int month = now.Month;

            DateTime candidate = BuildMonthlyByDay(year, month);

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
                candidate = BuildMonthlyByDay(year, month);
            }

            return candidate;
        }

        private DateTime BuildMonthlyByDay(int year, int month)
        {
            DayOfWeek targetDay = ParseDayOfWeek(config.Wipe.DayOfWeek);
            TimeSpan time = TimeSpan.ParseExact(
                config.Wipe.TimeOfDayMonthly,
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

            switch (config.Wipe.Position)
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

            string wipeTime = GetWipeTimeString();
            string message = config.Wipe.Message.Replace("{WipeTime}", wipeTime);

            BroadcastMessage(message);

            caller?.Message(FormatMessage("Broadcasted wipe time to all players."));
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

        #endregion
    }
}

