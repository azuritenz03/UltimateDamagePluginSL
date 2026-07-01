using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Exiled.API.Enums;
using Exiled.API.Features;

namespace UltimateDamagePlugin
{
    public partial class EventHandler
    {
        private readonly ConcurrentDictionary<string, BleedTickState> activeBleeds = new ConcurrentDictionary<string, BleedTickState>();
        private readonly ConcurrentDictionary<string, InjuryState> injuryStates = new ConcurrentDictionary<string, InjuryState>();

        private class BleedTickState
        {
            public float DamagePerSecond;
            public int RemainingTicks;
            public float TickIntervalSeconds;
            public string Severity;
            public System.Collections.Generic.HashSet<string> AffectedParts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private class BleedProfile
        {
            public int Duration;
            public float DamagePerSecond;
            public float TickIntervalSeconds;
            public string SourceName;
            public string Severity;
        }

        private class InjuryState
        {
            public string Severity;
            public float RemainingSeverity;
            public float MovementPenalty;
            public float AimPenalty;
        }

        private void ApplyBleedEffect(Player player, int durationSeconds, float tickIntervalSeconds, string bodyPart, string severity)
        {
            if (player == null || !player.IsAlive || durationSeconds <= 0 || string.IsNullOrEmpty(severity))
                return;

            var key = GetPlayerKey(player);
            if (string.IsNullOrEmpty(key))
                return;

            if (activeBleeds.TryGetValue(key, out var existing))
            {
                var existingRank = GetSeverityRank(existing.Severity);
                var newRank = GetSeverityRank(severity);
                var effectiveSeverity = existingRank >= newRank ? existing.Severity : severity;

                if (existingRank == newRank && string.Equals(existing.Severity, "moderate", StringComparison.OrdinalIgnoreCase) && string.Equals(severity, "moderate", StringComparison.OrdinalIgnoreCase))
                    effectiveSeverity = "critical";

                existing.Severity = effectiveSeverity;
                existing.DamagePerSecond = GetSeverityRate(effectiveSeverity);
                existing.RemainingTicks = Math.Max(existing.RemainingTicks, GetTickCount(durationSeconds, tickIntervalSeconds));
                existing.TickIntervalSeconds = Math.Min(existing.TickIntervalSeconds, tickIntervalSeconds);
                if (!string.IsNullOrEmpty(bodyPart))
                    existing.AffectedParts.Add(bodyPart);
                return;
            }

            var state = new BleedTickState
            {
                DamagePerSecond = GetSeverityRate(severity),
                RemainingTicks = GetTickCount(durationSeconds, tickIntervalSeconds),
                TickIntervalSeconds = tickIntervalSeconds,
                Severity = severity
            };

            if (!string.IsNullOrEmpty(bodyPart))
                state.AffectedParts.Add(bodyPart);

            activeBleeds[key] = state;
            StartBleedTick(player, key, state);
            UpdatePlayerHud(player);
            if (Plugin.Instance.Config.Debug || Plugin.Instance.Config.DebugMode)
                Log.Info($"[GunshotBleeding] Bleed started for {player.Nickname}: severity={severity} for {durationSeconds}s (part={bodyPart})");
        }

        private void StartBleedTick(Player player, string key, BleedTickState state)
        {
            if (player == null || string.IsNullOrEmpty(key) || state == null)
                return;

            _ = RunBleedTickAsync(player, key, state);
        }

        private async Task RunBleedTickAsync(Player player, string key, BleedTickState state)
        {
            while (!isDisposed && activeBleeds.TryGetValue(key, out var currentState) && currentState.RemainingTicks > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.25f, currentState.TickIntervalSeconds))).ConfigureAwait(false);

                if (player == null || !player.IsConnected || !player.IsAlive || player.Health <= 0f)
                    break;

                if (currentState.RemainingTicks <= 0)
                    break;

                try
                {
                    PlayBleedFeedback(player, "bleed");
                }
                catch
                {
                    // Ignore feedback errors.
                }

                try
                {
                    var perTickDamage = currentState.DamagePerSecond * currentState.TickIntervalSeconds;
                    if (player != null && player.IsAlive && perTickDamage > 0f)
                    {
                        skipNextHurting.TryAdd(key, 0);
                        ProcessDamage(player, perTickDamage, "Bleeding", Plugin.Instance.Config.DefaultCaliber, null, "BleedTick", 0, currentState.TickIntervalSeconds, "bleed", "bleed", true, false);
                    }
                }
                catch
                {
                    // Ignore failures applying tick damage.
                }

                try
                {
                    TriggerBleedVisuals(player, currentState.Severity);
                }
                catch
                {
                    // Ignore visual RPC failures.
                }

                currentState.RemainingTicks--;
                UpdatePlayerHud(player);
            }

            if (activeBleeds.ContainsKey(key))
            {
                activeBleeds.TryRemove(key, out _);
                if (Plugin.Instance.Config.Debug || Plugin.Instance.Config.DebugMode)
                    Log.Info($"[GunshotBleeding] Bleed ended for {player?.Nickname}");
                StartRecoveryLoop(player, key);
            }
        }

        private void StartRecoveryLoop(Player player, string key)
        {
            if (player == null || string.IsNullOrEmpty(key) || !player.IsAlive)
                return;

            _ = RunRecoveryLoopAsync(player, key);
        }

        private async Task RunRecoveryLoopAsync(Player player, string key)
        {
            while (!isDisposed && player != null && player.IsConnected && player.IsAlive && player.Health > 0f)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1f, Plugin.Instance.Config.RecoveryTickSeconds))).ConfigureAwait(false);

                if (player == null || !player.IsConnected || !player.IsAlive || player.Health <= 0f)
                    break;

                if (!injuryStates.TryGetValue(key, out var state))
                    break;

                state.RemainingSeverity = Math.Max(0f, state.RemainingSeverity - Plugin.Instance.Config.RecoveryAmountPerTick);
                if (state.RemainingSeverity <= 0f)
                {
                    injuryStates.TryRemove(key, out _);
                    ResetInjuryDebuffs(player);
                    UpdatePlayerHud(player);
                    break;
                }

                ApplyInjuryDebuffs(player, state);
            }
        }

        public void ClearBleedState()
        {
            try
            {
                activeBleeds.Clear();
                injuryStates.Clear();
                skipNextHurting.Clear();
                lastHudText.Clear();
            }
            catch
            {
                // ignore
            }
        }

        private void CleanupPlayerState(Player player, string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            activeBleeds.TryRemove(key, out _);
            injuryStates.TryRemove(key, out _);
            skipNextHurting.TryRemove(key, out _);
            lastHudText.TryRemove(key, out _);
            armorDurability.TryRemove(key, out _);

            try
            {
                ResetInjuryDebuffs(player);
            }
            catch
            {
            }

            try
            {
                RemoveBleedEffect(player);
            }
            catch
            {
            }
        }

        private void ApplyInjuryState(Player player, BleedProfile profile)
        {
            if (player == null || !player.IsAlive)
                return;

            var severity = profile.Severity ?? "light";
            var state = new InjuryState
            {
                Severity = severity,
                RemainingSeverity = severity == "critical" ? 10f : severity == "heavy" ? 6f : 3f,
                MovementPenalty = severity == "critical" ? Plugin.Instance.Config.SevereBleedMovementPenalty : severity == "moderate" ? 0.15f : 0.08f,
                AimPenalty = severity == "critical" ? Plugin.Instance.Config.SevereBleedAimPenalty : severity == "moderate" ? 0.12f : 0.05f
            };

            injuryStates[GetPlayerKey(player)] = state;
            ApplyInjuryDebuffs(player, state);
        }

        private void ApplyInjuryDebuffs(Player player, InjuryState state)
        {
            if (player == null || !Plugin.Instance.Config.EnableInjuryDebuffs || state == null)
                return;

            var effectDuration = Math.Max(2, (int)Math.Ceiling(state.RemainingSeverity));

            try
            {
                if (state.MovementPenalty > 0f)
                    player.EnableEffect(EffectType.Slowness, effectDuration);
            }
            catch
            {
                // Fallback if the effect API is unavailable.
            }

            try
            {
                if (state.AimPenalty > 0f)
                    player.EnableEffect(EffectType.Traumatized, effectDuration);
            }
            catch
            {
                // Ignore missing aim effect support.
            }

            try
            {
                if (string.Equals(state.Severity, "critical", StringComparison.OrdinalIgnoreCase))
                    player.EnableEffect(EffectType.Hemorrhage, effectDuration);
            }
            catch
            {
                // Ignore missing severe bleed effect support.
            }

            try
            {
                var cfg = Plugin.Instance?.Config;
                if (cfg != null && (cfg.Debug || cfg.DebugMode))
                    Log.Info($"[GunshotBleeding] ApplyInjuryDebuffs: applied debuffs for {player.Nickname} (severity={state?.Severity}, move={state.MovementPenalty}, aim={state.AimPenalty})");
            }
            catch
            {
                // Ignore logging failures.
            }
        }

        private void ResetInjuryDebuffs(Player player)
        {
            if (player == null)
                return;

            try
            {
                player.DisableEffect(EffectType.Slowness);
            }
            catch
            {
                // Ignore.
            }

            try
            {
                player.DisableEffect(EffectType.Traumatized);
            }
            catch
            {
                // Ignore.
            }

            try
            {
                player.DisableEffect(EffectType.Hemorrhage);
            }
            catch
            {
                // Ignore.
            }
        }

        private BleedProfile CreateBleedProfile(float amount, HitboxType hitboxType, Config cfg)
        {
            if (hitboxType == HitboxType.Limb)
            {
                return new BleedProfile
                {
                    Duration = cfg.LightBleedDuration,
                    DamagePerSecond = cfg.LightBleedDamagePerSecond,
                    TickIntervalSeconds = Math.Max(cfg.BleedTickIntervalSeconds, 1f),
                    SourceName = "gunshot",
                    Severity = "light"
                };
            }

            if (hitboxType == HitboxType.Body)
            {
                return new BleedProfile
                {
                    Duration = cfg.MediumBleedDuration,
                    DamagePerSecond = cfg.ModerateBleedDamagePerSecond,
                    TickIntervalSeconds = Math.Max(cfg.BleedTickIntervalSeconds, 1f),
                    SourceName = "gunshot",
                    Severity = "moderate"
                };
            }

            return new BleedProfile
            {
                Duration = 0,
                DamagePerSecond = 0f,
                TickIntervalSeconds = Math.Max(cfg.BleedTickIntervalSeconds, 1f),
                SourceName = "gunshot",
                Severity = "light"
            };
        }

        private float GetSeverityRate(string severity)
        {
            if (string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase))
                return Plugin.Instance.Config.CriticalBleedDamagePerSecond;
            if (string.Equals(severity, "moderate", StringComparison.OrdinalIgnoreCase))
                return Plugin.Instance.Config.ModerateBleedDamagePerSecond;
            return Plugin.Instance.Config.LightBleedDamagePerSecond;
        }

        private int GetSeverityRank(string severity)
        {
            if (string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase))
                return 3;
            if (string.Equals(severity, "moderate", StringComparison.OrdinalIgnoreCase))
                return 2;
            return 1;
        }

        private void PlayBleedFeedback(Player player, string sourceName)
        {
            if (player == null || !Plugin.Instance.Config.BleedFeedbackEnabled)
                return;

            try
            {
                var targetObjects = new object[] { player, player.ReferenceHub };
                foreach (var target in targetObjects)
                {
                    if (target == null)
                        continue;

                    foreach (var methodName in new[] { "PlayAmbientSound", "RpcPlaySound", "PlaySound", "SendAudio", "PlayPainSound", "PlayGunSound", "RpcShowDamageScreen", "RpcPlayScreenEffect", "RpcAddBlood", "RpcShowHitmarker", "RpcDisplayHitmarker" })
                    {
                        var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        if (method == null)
                            continue;

                        try
                        {
                            var parameters = method.GetParameters();
                            if (parameters.Length == 0)
                            {
                                method.Invoke(target, null);
                                return;
                            }

                            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                            {
                                method.Invoke(target, new object[] { sourceName });
                                return;
                            }

                            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                            {
                                method.Invoke(target, new object[] { 0 });
                                return;
                            }
                        }
                        catch
                        {
                            // Ignore missing or incompatible signatures and fall back to the next option.
                        }
                    }
                }
            }
            catch
            {
                // Ignore audio/runtime issues so bleeding still works.
            }
        }

        private int GetTickCount(int durationSeconds, float tickIntervalSeconds)
        {
            if (tickIntervalSeconds <= 0f)
                return Math.Max(1, durationSeconds);

            return Math.Max(1, (int)Math.Ceiling(durationSeconds / tickIntervalSeconds));
        }

        private void TriggerBleedVisuals(Player player, string severity)
        {
            if (player == null || !Plugin.Instance.Config.BleedFeedbackEnabled)
                return;

            var hub = player.ReferenceHub;
            if (hub == null)
                return;

            var paramSeverity = string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase) ? 3
                : string.Equals(severity, "heavy", StringComparison.OrdinalIgnoreCase) ? 2
                : 1;
            var targetObjects = new object[] { player, hub };
            foreach (var target in targetObjects)
            {
                if (target == null)
                    continue;

                foreach (var methodName in new[] { "RpcSpawnBloodDrip", "RpcAddBlood", "RpcShowDamageScreen", "RpcPlayScreenEffect", "RpcPlayHitmarker", "RpcDisplayHitmarker" })
                {
                    try
                    {
                        var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        if (method == null)
                            continue;

                        var parameters = method.GetParameters();
                        if (parameters.Length == 0)
                        {
                            method.Invoke(target, null);
                        }
                        else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                        {
                            method.Invoke(target, new object[] { paramSeverity });
                        }
                        else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                        {
                            method.Invoke(target, new object[] { severity });
                        }
                    }
                    catch
                    {
                        // Ignore any reflection/rpc invocation errors for visuals.
                    }
                }
            }
        }

        private void TryApplyCollapse(Player player, float chance)
        {
            if (player == null || !player.IsAlive || chance <= 0f)
                return;

            try
            {
                if (random.NextDouble() > chance)
                    return;

                var method = player.GetType().GetMethod("Ragdoll", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    method.Invoke(player, null);
                    return;
                }
            }
            catch
            {
                // Ignore missing collapse support and just continue.
            }

            try
            {
                player.EnableEffect(EffectType.Hemorrhage, 3);
            }
            catch
            {
                // Ignore effect application issues.
            }
        }

        private void RemoveBleedEffect(Player player)
        {
            if (player == null)
                return;

            var key = GetPlayerKey(player);
            if (!string.IsNullOrEmpty(key))
            {
                activeBleeds.TryRemove(key, out _);
                if (Plugin.Instance.Config.Debug || Plugin.Instance.Config.DebugMode)
                    Log.Info($"[GunshotBleeding] RemoveBleedEffect: removed bleed for {player.Nickname}");
            }

            if (player.IsAlive)
                UpdatePlayerHud(player);
        }
    }
}
