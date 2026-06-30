using System;
using System.Collections.Concurrent;
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

        private void ApplyBleedEffect(Player player, int durationSeconds, float damagePerSecond, float tickIntervalSeconds, string bodyPart = null)
        {
            if (player == null || durationSeconds <= 0 || damagePerSecond <= 0f)
                return;

            var key = GetPlayerKey(player);
            if (string.IsNullOrEmpty(key))
                return;

            if (activeBleeds.TryGetValue(key, out var existing))
            {
                existing.DamagePerSecond = Math.Max(existing.DamagePerSecond, damagePerSecond);
                existing.RemainingTicks = Math.Max(existing.RemainingTicks, GetTickCount(durationSeconds, tickIntervalSeconds));
                existing.TickIntervalSeconds = Math.Min(existing.TickIntervalSeconds, tickIntervalSeconds);
                if (!string.IsNullOrEmpty(bodyPart))
                    existing.AffectedParts.Add(bodyPart);
                return;
            }

            var state = new BleedTickState
            {
                DamagePerSecond = damagePerSecond,
                RemainingTicks = GetTickCount(durationSeconds, tickIntervalSeconds),
                TickIntervalSeconds = tickIntervalSeconds
            };

            if (!string.IsNullOrEmpty(bodyPart))
                state.AffectedParts.Add(bodyPart);

            activeBleeds[key] = state;
            StartBleedTick(player, key, state);
            UpdatePlayerHud(player);
            if (Plugin.Instance.Config.Debug || Plugin.Instance.Config.DebugMode)
                Log.Info($"[GunshotBleeding] Bleed started for {player.Nickname}: {damagePerSecond} DPS for {durationSeconds}s (part={bodyPart})");
        }

        private void StartBleedTick(Player player, string key, BleedTickState state)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!isDisposed && activeBleeds.TryGetValue(key, out var currentState) && currentState.RemainingTicks > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.25f, currentState.TickIntervalSeconds)));

                        if (player == null || !player.IsConnected || !player.IsAlive || player.Health <= 0f)
                            break;

                        if (currentState.RemainingTicks <= 0)
                            break;

                        var perTickDamage = Math.Max(1f, currentState.DamagePerSecond * currentState.TickIntervalSeconds);
                        skipNextHurting.TryAdd(key, 0);
                        try
                        {
                            ProcessDamage(player, perTickDamage, "Bleeding", Plugin.Instance.Config.DefaultCaliber, null, "BleedTick", 0, 0f, currentState.TickIntervalSeconds, "bleed", true, false);
                        }
                        catch
                        {
                            // Ignore failures applying tick damage.
                        }

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
                            TriggerBleedVisuals(player, currentState.DamagePerSecond);
                        }
                        catch
                        {
                            // Ignore visual RPC failures.
                        }

                        currentState.RemainingTicks--;
                        UpdatePlayerHud(player);
                    }
                }
                catch
                {
                    // Swallow exceptions from the bleed task to avoid crashing the server.
                }

                if (activeBleeds.ContainsKey(key))
                {
                    activeBleeds.TryRemove(key, out _);
                    if (Plugin.Instance.Config.Debug || Plugin.Instance.Config.DebugMode)
                        Log.Info($"[GunshotBleeding] Bleed ended for {player?.Nickname}");
                    StartRecoveryLoop(player, key);
                }
            });
        }

        private void StartRecoveryLoop(Player player, string key)
        {
            _ = Task.Run(async () =>
            {
                while (!isDisposed && player != null && player.IsConnected && player.IsAlive && player.Health > 0f)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1f, Plugin.Instance.Config.RecoveryTickSeconds)));

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
            });
        }

        public void ClearBleedState()
        {
            try
            {
                activeBleeds.Clear();
                injuryStates.Clear();
            }
            catch
            {
                // ignore
            }
        }

        private void ApplyInjuryState(Player player, BleedProfile profile)
        {
            if (player == null)
                return;

            var severity = profile.Severity ?? "light";
            var state = new InjuryState
            {
                Severity = severity,
                RemainingSeverity = severity == "severe" ? 10f : severity == "moderate" ? 6f : 3f,
                MovementPenalty = severity == "severe" ? Plugin.Instance.Config.SevereBleedMovementPenalty : severity == "moderate" ? 0.15f : 0.08f,
                AimPenalty = severity == "severe" ? Plugin.Instance.Config.SevereBleedAimPenalty : severity == "moderate" ? 0.12f : 0.05f
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
                if (string.Equals(state.Severity, "severe", StringComparison.OrdinalIgnoreCase))
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
            var duration = 0;
            if (amount >= cfg.HeavyDamageThreshold)
                duration = cfg.HeavyBleedDuration;
            else if (amount >= cfg.MediumDamageThreshold)
                duration = cfg.MediumBleedDuration;
            else if (amount >= cfg.LightDamageThreshold)
                duration = cfg.LightBleedDuration;

            if (duration > 0 && hitboxType == HitboxType.Body && duration == cfg.LightBleedDuration)
                duration = cfg.MediumBleedDuration;

            return new BleedProfile
            {
                Duration = duration,
                DamagePerSecond = Math.Max(cfg.BleedDamagePerSecond, 1.5f),
                TickIntervalSeconds = Math.Max(cfg.BleedTickIntervalSeconds, 1f),
                SourceName = "gunshot",
                Severity = duration >= cfg.HeavyBleedDuration ? "severe" : duration >= cfg.MediumBleedDuration ? "moderate" : "light"
            };
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

        private void TriggerBleedVisuals(Player player, float severity)
        {
            if (player == null || !Plugin.Instance.Config.BleedFeedbackEnabled)
                return;

            var hub = player.ReferenceHub;
            if (hub == null)
                return;

            var paramSeverity = severity <= 0f ? 1 : (int)Math.Ceiling(severity);
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
                            method.Invoke(target, new object[] { severity.ToString() });
                        }
                    }
                    catch
                    {
                        // Ignore any reflection/rpc invocation errors for visuals.
                    }
                }
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

            UpdatePlayerHud(player);
        }
    }
}
