using System;
using System.Collections.Concurrent;
using Exiled.API.Features;
using System.Reflection;

namespace UltimateDamagePlugin
{
    public partial class EventHandler
    {
        private readonly ConcurrentDictionary<string, string> lastHudText = new ConcurrentDictionary<string, string>();
        private const int DefaultHudHintDurationSeconds = 4;
        private const int HudHintClearDurationSeconds = 1;

        private void UpdatePlayerHud(Player player)
        {
            if (player == null || Plugin.Instance == null || !player.IsAlive)
                return;

            var cfg = Plugin.Instance.Config;
            var key = GetPlayerKey(player);
            if (string.IsNullOrEmpty(key))
                return;

            var sb = new System.Text.StringBuilder();

            if (cfg.EnableArmorHud)
            {
                var dur = GetArmorDurability(player);
                if (dur > 0f)
                    sb.Append($"Armor: {Math.Round(dur)}% ");
            }

            if (activeBleeds.TryGetValue(key, out var bleed))
            {
                var severity = string.Equals(bleed.Severity, "critical", StringComparison.OrdinalIgnoreCase) ? "Critical"
                    : string.Equals(bleed.Severity, "moderate", StringComparison.OrdinalIgnoreCase) ? "Moderate"
                    : "Light";
                var remainingSec = Math.Ceiling(bleed.RemainingTicks * bleed.TickIntervalSeconds);
                sb.Append($"Bleed: {severity} {remainingSec}s");
            }

            var text = sb.ToString().Trim();
            if (text.Length == 0)
            {
                if (lastHudText.TryGetValue(key, out var prev) && !string.IsNullOrEmpty(prev))
                {
                    SendHudHint(player, string.Empty, HudHintClearDurationSeconds);
                    lastHudText[key] = string.Empty;
                }
                return;
            }

            if (text.Length > Plugin.Instance.Config.HudMaxLineLength)
                text = text.Substring(0, Plugin.Instance.Config.HudMaxLineLength);

            if (lastHudText.TryGetValue(key, out var last) && last == text)
                return;

            SendHudHint(player, text, DefaultHudHintDurationSeconds);
            lastHudText[key] = text;
        }

        private void SendHudHint(Player player, string text, int durationSeconds)
        {
            if (player == null)
                return;

            if (durationSeconds <= 0)
                durationSeconds = DefaultHudHintDurationSeconds;

            try
            {
                var method = player.GetType().GetMethod("ShowHint", new Type[] { typeof(string), typeof(int) });
                if (method != null)
                {
                    method.Invoke(player, new object[] { text, durationSeconds });
                    return;
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.Config != null && (Plugin.Instance.Config.Debug || Plugin.Instance.Config.DebugMode))
                    Log.Warn($"[GunshotBleeding] SendHudHint direct call failed: {ex.Message}");
            }

            try
            {
                var hub = player.ReferenceHub;
                if (hub != null)
                {
                    var methodNames = new[] { "ShowHint", "DisplayHint", "SendHint", "SendConsoleMessage" };
                    foreach (var name in methodNames)
                    {
                        try
                        {
                            var m = hub.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { typeof(string), typeof(int) }, null);
                            if (m != null)
                            {
                                m.Invoke(hub, new object[] { text, durationSeconds });
                                return;
                            }

                            m = hub.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { typeof(string) }, null);
                            if (m != null)
                            {
                                m.Invoke(hub, new object[] { text });
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            if (Plugin.Instance?.Config != null && (Plugin.Instance.Config.Debug || Plugin.Instance.Config.DebugMode))
                                Log.Warn($"[GunshotBleeding] SendHudHint reflection {name} failed: {ex.Message}");
                        }
                    }
                }
            }
            catch { }
        }
    }
}
