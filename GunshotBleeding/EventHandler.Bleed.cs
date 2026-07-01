using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Exiled.API.Enums;
using Exiled.API.Features;
using MEC;

namespace UltimateDamagePlugin
{
    public partial class EventHandler
    {
        private readonly ConcurrentDictionary<string, BleedTickState> activeBleeds = new ConcurrentDictionary<string, BleedTickState>();
        private readonly ConcurrentDictionary<string, InjuryState> injuryStates = new ConcurrentDictionary<string, InjuryState>();
        private readonly ConcurrentDictionary<string, CoroutineHandle> bleedCoroutines = new ConcurrentDictionary<string, CoroutineHandle>();
        private readonly ConcurrentDictionary<string, CoroutineHandle> recoveryCoroutines = new ConcurrentDictionary<string, CoroutineHandle>();
        private static readonly string[] FeedbackMethodNames = { "PlayAmbientSound", "RpcPlaySound", "PlaySound", "SendAudio", "PlayPainSound", "PlayGunSound", "RpcShowDamageScreen", "RpcPlayScreenEffect", "RpcAddBlood", "RpcShowHitmarker", "RpcDisplayHitmarker" };
        private static readonly string[] VisualMethodNames = { "RpcSpawnBloodDrip", "RpcAddBlood", "RpcShowDamageScreen", "RpcPlayScreenEffect", "RpcPlayHitmarker", "RpcDisplayHitmarker" };
        private static readonly Dictionary<Type, Dictionary<string, MethodInfo>> CachedMethodLookup = new Dictionary<Type, Dictionary<string, MethodInfo>>();
        private static readonly Dictionary<Type, MethodInfo> CachedRagdollMethods = new Dictionary<Type, MethodInfo>();

        static EventHandler()
        {
            var targetTypes = new List<Type> { typeof(Player) };
            var referenceHubType = typeof(Player).Assembly.GetType("ReferenceHub");
            if (referenceHubType != null)
                targetTypes.Add(referenceHubType);

            foreach (var targetType in targetTypes.Distinct())
            {
                var methods = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var methodName in FeedbackMethodNames.Concat(VisualMethodNames))
                {
                    var method = targetType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (method != null)
                        methods[methodName] = method;
                }

                CachedMethodLookup[targetType] = methods;

                var ragdollMethod = targetType.GetMethod("Ragdoll", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (ragdollMethod != null)
                    CachedRagdollMethods[targetType] = ragdollMethod;
            }
        }

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

        private void CancelBleedForPlayer(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (bleedCoroutines.TryGetValue(key, out var handle) && handle.IsValid)
            {
                try
                {
                    Timing.KillCoroutines(handle);
                }
                catch
                {
                }
            }

            bleedCoroutines.TryRemove(key, out _);
        }

        private void StartBleedTick(Player player, string key, BleedTickState state)
        {
            if (player == null || string.IsNullOrEmpty(key) || state == null)
                return;

            CancelBleedForPlayer(key);
            var handle = Timing.RunCoroutine(RunBleedTickCoroutine(key, state), Segment.Update);
            bleedCoroutines[key] = handle;
        }

        private IEnumerator<float> RunBleedTickCoroutine(string key, BleedTickState state)
        {
            try
            {
                while (!isDisposed
                    && activeBleeds.TryGetValue(key, out var currentState)
                    && currentState.RemainingTicks > 0)
                {
                    Player currentPlayer = null;
                    try
                    {
                        currentPlayer = Player.Get(key);
                    }
                    catch
                    {
                        currentPlayer = null;
                    }

                    if (currentPlayer == null || !currentPlayer.IsConnected || !currentPlayer.IsAlive || currentPlayer.Health <= 0f)
                        break;

                    if (!activeBleeds.TryGetValue(key, out currentState) || currentState.RemainingTicks <= 0)
                        break;

                    yield return Timing.WaitForSeconds(Math.Max(0.25f, currentState.TickIntervalSeconds));

                    if (isDisposed)
                        break;

                    try
                    {
                        currentPlayer = Player.Get(key);
                    }
                    catch
                    {
                        currentPlayer = null;
                    }

                    if (currentPlayer == null || !currentPlayer.IsConnected || !currentPlayer.IsAlive || currentPlayer.Health <= 0f)
                        break;

                    if (!activeBleeds.TryGetValue(key, out currentState) || currentState.RemainingTicks <= 0)
                        break;

                    PlayBleedFeedback(currentPlayer, "bleed");

                    var perTickDamage = currentState.DamagePerSecond * currentState.TickIntervalSeconds;
                    if (currentPlayer != null && currentPlayer.IsAlive && perTickDamage > 0f)
                    {
                        skipNextHurting.TryAdd(key, 0);
                        ProcessDamage(currentPlayer, perTickDamage, "Bleeding", Plugin.Instance.Config.DefaultCaliber, null, "BleedTick", 0, currentState.TickIntervalSeconds, "bleed", "bleed", true, false);
                    }

                    TriggerBleedVisuals(currentPlayer, currentState.Severity);
                    currentState.RemainingTicks = Math.Max(0, currentState.RemainingTicks - 1);
                    UpdatePlayerHud(currentPlayer);
                }
            }
            finally
            {
                bleedCoroutines.TryRemove(key, out _);

                if (!isDisposed && activeBleeds.ContainsKey(key))
                {
                    activeBleeds.TryRemove(key, out _);
                    if (Plugin.Instance.Config.Debug || Plugin.Instance.Config.DebugMode)
                        Log.Info($"[GunshotBleeding] Bleed ended for {key}");
                    StartRecoveryLoop(key);
                }
            }
        }

        private void CancelRecoveryForPlayer(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (recoveryCoroutines.TryGetValue(key, out var handle) && handle.IsValid)
            {
                try
                {
                    Timing.KillCoroutines(handle);
                }
                catch
                {
                }
            }

            recoveryCoroutines.TryRemove(key, out _);
        }

        private void StartRecoveryLoop(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            CancelRecoveryForPlayer(key);
            var handle = Timing.RunCoroutine(RunRecoveryLoopCoroutine(key), Segment.Update);
            recoveryCoroutines[key] = handle;
        }

        private IEnumerator<float> RunRecoveryLoopCoroutine(string key)
        {
            try
            {
                while (!isDisposed)
                {
                    Player currentPlayer = null;
                    try
                    {
                        currentPlayer = Player.Get(key);
                    }
                    catch
                    {
                        currentPlayer = null;
                    }

                    if (currentPlayer == null || !currentPlayer.IsConnected || !currentPlayer.IsAlive || currentPlayer.Health <= 0f)
                        break;

                    if (!injuryStates.ContainsKey(key))
                        break;

                    yield return Timing.WaitForSeconds(Math.Max(1f, Plugin.Instance.Config.RecoveryTickSeconds));

                    if (isDisposed)
                        break;

                    currentPlayer = null;
                    try
                    {
                        currentPlayer = Player.Get(key);
                    }
                    catch
                    {
                        currentPlayer = null;
                    }

                    if (currentPlayer == null || !currentPlayer.IsConnected || !currentPlayer.IsAlive || currentPlayer.Health <= 0f)
                        break;

                    if (!injuryStates.TryGetValue(key, out var state))
                        break;

                    state.RemainingSeverity = Math.Max(0f, state.RemainingSeverity - Plugin.Instance.Config.RecoveryAmountPerTick);
                    if (state.RemainingSeverity <= 0f)
                    {
                        injuryStates.TryRemove(key, out _);
                        ResetInjuryDebuffs(currentPlayer);
                        UpdatePlayerHud(currentPlayer);
                        break;
                    }

                    ApplyInjuryDebuffs(currentPlayer, state);
                }
            }
            finally
            {
                recoveryCoroutines.TryRemove(key, out _);
            }
        }

        public void ClearBleedState()
        {
            try
            {
                activeBleeds.Clear();
                injuryStates.Clear();
                foreach (var entry in bleedCoroutines.ToArray())
                    CancelBleedForPlayer(entry.Key);
                foreach (var entry in recoveryCoroutines.ToArray())
                    CancelRecoveryForPlayer(entry.Key);
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

            CancelBleedForPlayer(key);
            CancelRecoveryForPlayer(key);
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

        private static MethodInfo GetCachedMethod(Type targetType, string methodName)
        {
            if (targetType == null || string.IsNullOrEmpty(methodName))
                return null;

            if (CachedMethodLookup.TryGetValue(targetType, out var methods) && methods.TryGetValue(methodName, out var method))
                return method;

            var resolvedMethod = targetType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (resolvedMethod != null)
            {
                if (!CachedMethodLookup.TryGetValue(targetType, out methods))
                {
                    methods = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
                    CachedMethodLookup[targetType] = methods;
                }

                methods[methodName] = resolvedMethod;
            }

            return resolvedMethod;
        }

        private static MethodInfo GetCachedRagdollMethod(Type targetType)
        {
            if (targetType == null)
                return null;

            if (CachedRagdollMethods.TryGetValue(targetType, out var method))
                return method;

            var resolvedMethod = targetType.GetMethod("Ragdoll", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (resolvedMethod != null)
                CachedRagdollMethods[targetType] = resolvedMethod;

            return resolvedMethod;
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

                    foreach (var methodName in FeedbackMethodNames)
                    {
                        var method = GetCachedMethod(target.GetType(), methodName);
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

                foreach (var methodName in VisualMethodNames)
                {
                    try
                    {
                        var method = GetCachedMethod(target.GetType(), methodName);
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

                var method = GetCachedRagdollMethod(player.GetType());
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
                CancelBleedForPlayer(key);
                CancelRecoveryForPlayer(key);
                activeBleeds.TryRemove(key, out _);
                if (Plugin.Instance.Config.Debug || Plugin.Instance.Config.DebugMode)
                    Log.Info($"[GunshotBleeding] RemoveBleedEffect: removed bleed for {player.Nickname}");
            }

            if (player.IsAlive)
                UpdatePlayerHud(player);
        }
    }
}