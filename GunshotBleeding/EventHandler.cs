using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Exiled.Events.EventArgs.Player;
using Exiled.Events.EventArgs.Scp914;
using Exiled.API.Enums;
using Exiled.API.Features;
using InventorySystem.Items;
using InventorySystem;
using InventorySystem.Items.Armor;

namespace UltimateDamagePlugin
{
    public class EventHandler
    {
        private readonly Random random = new Random();
        private readonly ConcurrentDictionary<string, ChestHitState> chestHitHistory = new ConcurrentDictionary<string, ChestHitState>();
        private readonly ConcurrentDictionary<string, BleedTickState> activeBleeds = new ConcurrentDictionary<string, BleedTickState>();
        private readonly ConcurrentDictionary<string, InjuryState> injuryStates = new ConcurrentDictionary<string, InjuryState>();
        private readonly ConcurrentDictionary<string, byte> skipNextHurting = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, float> armorDurability = new ConcurrentDictionary<string, float>();
        private volatile bool isDisposed = false;

        private class ChestHitState
        {
            public int Count;
            public DateTime LastHitUtc;
        }

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

        public void OnHurting(HurtingEventArgs ev)
        {
            if (ev.Player == null)
                return;

            var cfg = Plugin.Instance.Config;
            var key = GetPlayerKey(ev.Player);

            if (ev.Attacker == null || !ev.Attacker.IsHuman)
            {
                if (ev.Amount > 0f)
                {
                    var profile = CreateNonHumanBleedProfile(ev.Attacker, ev.Amount, cfg);
                    if (profile != null && profile.Duration > 0)
                    {
                        // Centralized handling: use descriptive damageType from profile.SourceName when available
                        var damageType = string.IsNullOrEmpty(profile.SourceName) ? "Environmental" : profile.SourceName;
                        ProcessDamage(ev.Player, ev.Amount, damageType, cfg.DefaultCaliber, ev.Attacker, null, profile.Duration, profile.DamagePerSecond, profile.TickIntervalSeconds, profile.SourceName, false);
                    }
                }

                return;
            }

            if (!string.IsNullOrEmpty(key) && skipNextHurting.ContainsKey(key))
            {
                skipNextHurting.TryRemove(key, out _);
                return;
            }

            if (!ev.Player.IsHuman || !ev.Attacker.IsHuman)
                return;

            if (cfg.BleedChance < 100f && random.NextDouble() * 100f > cfg.BleedChance)
                return;

            ev.Amount *= cfg.HumanDamageMultiplier;

            int duration;
            if (ev.Amount >= cfg.HeavyDamageThreshold)
                duration = cfg.HeavyBleedDuration;
            else if (ev.Amount >= cfg.MediumDamageThreshold)
                duration = cfg.MediumBleedDuration;
            else if (ev.Amount >= cfg.LightDamageThreshold)
                duration = cfg.LightBleedDuration;
            else
                return;

            ProcessDamage(ev.Player, ev.Amount, "Gunshot", cfg.DefaultCaliber, ev.Attacker, null, duration, cfg.BleedDamagePerSecond, cfg.BleedTickIntervalSeconds, "gunshot", false);

            if (cfg.Debug || cfg.DebugMode)
                Log.Info($"[GunshotBleeding] {ev.Player.Nickname} took {ev.Amount} damage from {ev.Attacker.Nickname} → bleed {duration}s");
        }

        public void OnShot(ShotEventArgs ev)
        {
            try
            {
                if (ev.Player == null || ev.Target == null || ev.Hitbox == null)
                    return;

                if (!ev.Player.IsHuman || !ev.Target.IsHuman)
                    return;

            var cfg = Plugin.Instance.Config;
            var victim = ev.Target;
            var hitboxType = ev.Hitbox.HitboxType;
            var damage = cfg.LimbShotDamage;
            var bloodTimer = 0;
            var bleed = false;
            var hasArmor = false;
            var key = GetPlayerKey(victim);
            var distanceFactor = 1f;

            if (ev.Distance > cfg.RangeDamageFalloffStart)
            {
                var falloffDistance = Math.Max(1f, cfg.RangeDamageFalloffEnd - cfg.RangeDamageFalloffStart);
                distanceFactor = 1f - ((ev.Distance - cfg.RangeDamageFalloffStart) / falloffDistance);
                distanceFactor = Math.Max(cfg.RangeMinimumDamagePercent, Math.Min(1f, distanceFactor));
            }

            if (hitboxType == HitboxType.Headshot)
            {
                ev.CanHurt = false;
                skipNextHurting.TryAdd(key, 0);

                DoKill(victim, DamageType.Firearm, "Headshot", cfg.DefaultCaliber);

                if (cfg.Debug)
                    Log.Info($"[GunshotBleeding] {ev.Player.Nickname} scored headshot on {victim.Nickname}");

                chestHitHistory.TryRemove(key, out _);
                return;
            }

            if (hitboxType == HitboxType.Body)
            {
                damage = cfg.ChestShotDamage;
                bloodTimer = cfg.ChestBleedDuration;
                bleed = cfg.ChestBleedChance >= 100f || random.NextDouble() * 100f <= cfg.ChestBleedChance;
            }
            else
            {
                damage = cfg.LimbShotDamage;
                bloodTimer = cfg.LimbBleedDuration;
                bleed = cfg.LimbBleedChance >= 100f || random.NextDouble() * 100f <= cfg.LimbBleedChance;
            }

            try
            {
                if (victim != null && victim.Inventory != null)
                    hasArmor = BodyArmorUtils.TryGetBodyArmor(victim.Inventory, out var armorItem);
                    if (hasArmor)
                    {
                        var prior = armorDurability.GetOrAdd(key, k => Plugin.Instance.Config.ArmorDurability);
                        if (prior <= 0f)
                        {
                            hasArmor = false; // previously broken
                        }
                    }
            }
            catch
            {
                hasArmor = false;
            }

            damage *= cfg.HumanDamageMultiplier;
            damage *= distanceFactor;

            if (hasArmor)
            {
                var caliber = GetCaliberFromShot(ev) ?? cfg.DefaultCaliber;
                var isPenetrating = IsCaliberPenetrating(caliber, cfg);
                var isResisted = IsCaliberResisted(caliber, cfg);

                // Reduce damage for resisted calibers
                if (isResisted)
                {
                    damage = damage * (1f - cfg.ArmorDamageReduction);
                }

                // Apply durability loss
                try
                {
                    var loss = cfg.ArmorDurabilityLossPerHit * (isPenetrating ? cfg.ArmorPenetrationDurabilityLossMultiplier : 1f);
                    armorDurability.AddOrUpdate(key, Plugin.Instance.Config.ArmorDurability - loss, (k, v) => Math.Max(0f, v - loss));
                    if (armorDurability.TryGetValue(key, out var remaining) && remaining <= 0f)
                    {
                        // Armor broken
                        PlayBleedFeedback(victim, "armorbreak");
                        if (cfg.Debug)
                            Log.Info($"[GunshotBleeding] Armor broken for {victim.Nickname}");
                    }
                }
                catch
                {
                    // ignore durability errors
                }

                bloodTimer = (int)Math.Ceiling(bloodTimer * cfg.ArmorBleedDurationModifier);
                bleed = bleed && random.NextDouble() * 100f <= cfg.ArmorBleedChanceModifier * 100f;
            }

            if (hitboxType == HitboxType.Body)
            {
                var now = DateTime.UtcNow;
                if (!chestHitHistory.TryGetValue(key, out var state))
                {
                    state = new ChestHitState();
                    chestHitHistory[key] = state;
                }

                if (state.Count > 0 && (now - state.LastHitUtc).TotalSeconds > cfg.ChestShotWindowSeconds)
                {
                    state.Count = 0;
                }

                state.Count++;
                state.LastHitUtc = now;

                if (state.Count >= cfg.ChestShotHitCount)
                {
                    ev.CanHurt = false;
                    skipNextHurting.TryAdd(key, 0);
                    DoKill(victim, DamageType.Firearm, "Chest trauma", cfg.DefaultCaliber);
                    chestHitHistory.TryRemove(key, out _);

                    if (cfg.Debug || cfg.DebugMode)
                        Log.Info($"[GunshotBleeding] {ev.Player.Nickname} finished {victim.Nickname} with a second chest shot");

                    return;
                }
            }

            damage = Math.Max(1f, damage);
            // Let the engine apply the actual shot damage. OnHurting will be triggered and handle bleed/durability.
            // Avoid calling Hurt manually here to prevent native crashes; do only non-damaging effects.
            try
            {
                if (bleed && bloodTimer > 0)
                {
                    // Apply visual and feedback effects only; do not change health here.
                    ApplyBleedEffect(victim, bloodTimer, cfg.BleedDamagePerSecond, cfg.BleedTickIntervalSeconds, hitboxType.ToString());
                    ApplyInjuryState(victim, new BleedProfile { Duration = bloodTimer, DamagePerSecond = cfg.BleedDamagePerSecond, TickIntervalSeconds = cfg.BleedTickIntervalSeconds, Severity = bloodTimer >= cfg.HeavyBleedDuration ? "severe" : bloodTimer >= cfg.MediumBleedDuration ? "moderate" : "light", SourceName = "gunshot" });
                    PlayBleedFeedback(victim, "gunshot");
                    try { TriggerBleedVisuals(victim, cfg.BleedDamagePerSecond); } catch { }
                }
            }
            catch (Exception ex)
            {
                if (cfg.Debug || cfg.DebugMode)
                    Log.Warn($"[GunshotBleeding] OnShot visual/bleed handling failed: {ex.Message}");
            }

            if (cfg.Debug || cfg.DebugMode)
                Log.Info($"[GunshotBleeding] {ev.Player.Nickname} hit {victim.Nickname} in {hitboxType}, damage {damage}, bleed {bloodTimer}s");
        }
            catch (Exception ex)
            {
                var cfg2 = Plugin.Instance?.Config;
                if (cfg2 != null && (cfg2.Debug || cfg2.DebugMode))
                    Log.Error($"[GunshotBleeding] OnShot exception: {ex}");
            }
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
            // Update HUD when bleed starts
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
                            ProcessDamage(player, perTickDamage, "Bleeding", Plugin.Instance.Config.DefaultCaliber, null, "BleedTick", 0, 0f, currentState.TickIntervalSeconds, "bleed");
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

        public void Dispose()
        {
            try
            {
                isDisposed = true;
                activeBleeds.Clear();
                injuryStates.Clear();
                chestHitHistory.Clear();
                skipNextHurting.Clear();
                armorDurability.Clear();
                lastHudText.Clear();
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
                RemainingSeverity = severity == "severe" ? 8f : severity == "moderate" ? 5f : 3f,
                MovementPenalty = severity == "severe" ? Plugin.Instance.Config.SevereBleedMovementPenalty : severity == "moderate" ? 0.1f : 0f,
                AimPenalty = severity == "severe" ? Plugin.Instance.Config.SevereBleedAimPenalty : severity == "moderate" ? 0.08f : 0f
            };

            injuryStates[GetPlayerKey(player)] = state;
            ApplyInjuryDebuffs(player, state);
        }

        private void ApplyInjuryDebuffs(Player player, InjuryState state)
        {
            if (player == null || !Plugin.Instance.Config.EnableInjuryDebuffs || state == null)
                return;

            try
            {
                player.ReferenceHub.playerEffectsController.EnableEffect<CustomPlayerEffects.Slowness>(1f);
            }
            catch
            {
                // Fallback if the effect API is unavailable.
            }

            try
            {
                var cfg = Plugin.Instance?.Config;
                if (cfg != null && (cfg.Debug || cfg.DebugMode))
                    Log.Info($"[GunshotBleeding] ApplyInjuryDebuffs: applied debuffs for {player.Nickname} (severity={state?.Severity})");
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
                player.ReferenceHub.playerEffectsController.DisableEffect<CustomPlayerEffects.Slowness>();
            }
            catch
            {
                // Ignore.
            }
        }

        private BleedProfile CreateNonHumanBleedProfile(Player attacker, float amount, Config cfg)
        {
            if (attacker != null)
            {
                var roleName = attacker.Role?.Type.ToString() ?? string.Empty;
                if (roleName.IndexOf("Scp939", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new BleedProfile
                    {
                        Duration = Math.Max(4, (int)Math.Ceiling(amount / 2f)),
                        DamagePerSecond = 3f,
                        TickIntervalSeconds = 0.75f,
                        SourceName = "SCP-939"
                    };
                }

                if (roleName.IndexOf("Scp", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new BleedProfile
                    {
                        Duration = Math.Max(3, (int)Math.Ceiling(amount / 2.5f)),
                        DamagePerSecond = 2f,
                        TickIntervalSeconds = 1f,
                        SourceName = roleName
                    };
                }
            }

            var duration = 0;
            if (amount >= cfg.HeavyDamageThreshold)
                duration = cfg.HeavyBleedDuration;
            else if (amount >= cfg.MediumDamageThreshold)
                duration = cfg.MediumBleedDuration;
            else if (amount >= cfg.LightDamageThreshold)
                duration = cfg.LightBleedDuration;

            return new BleedProfile
            {
                Duration = duration,
                DamagePerSecond = Math.Max(cfg.BleedDamagePerSecond, 1.5f),
                TickIntervalSeconds = Math.Max(cfg.BleedTickIntervalSeconds, 1f),
                SourceName = "hazard",
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

        private void DoKill(Player victim, DamageType type, string reason, string caliber)
        {
            if (victim == null)
                return;

            try
            {
                var killMethod = victim.GetType().GetMethod("Kill", new Type[] { typeof(DamageType), typeof(string), typeof(string) });
                if (killMethod != null)
                {
                    killMethod.Invoke(victim, new object[] { type, reason, caliber });
                    return;
                }
            }
            catch
            {
                // Ignore reflection failures and fallback.
            }

            try
            {
                victim.Kill(type, reason);
            }
            catch
            {
                // Ignore runtime failures when attempting to kill.
            }
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

        private readonly ConcurrentDictionary<string, string> lastHudText = new ConcurrentDictionary<string, string>();

        private void UpdatePlayerHud(Player player)
        {
            if (player == null || Plugin.Instance == null)
                return;

            var cfg = Plugin.Instance.Config;
            var key = GetPlayerKey(player);
            if (string.IsNullOrEmpty(key))
                return;

            var sb = new System.Text.StringBuilder();

            // Armor HUD
            if (cfg.EnableArmorHud)
            {
                if (armorDurability.TryGetValue(key, out var dur))
                {
                    sb.Append($"Armor: {Math.Round(dur)}% ");
                }
            }

            // Bleed HUD
            if (cfg.EnableBleedHud)
            {
                if (activeBleeds.TryGetValue(key, out var bleed))
                {
                    var severity = bleed.DamagePerSecond >= 3f ? "Heavy" : bleed.DamagePerSecond >= 1.5f ? "Moderate" : "Light";
                    var remainingSec = Math.Ceiling(bleed.RemainingTicks * bleed.TickIntervalSeconds);
                    sb.Append($"| Bleed: {severity} ({bleed.DamagePerSecond} dmg/s) {remainingSec}s");

                    if (bleed.AffectedParts != null && bleed.AffectedParts.Count > 0)
                    {
                        sb.Append(" Parts:");
                        foreach (var p in bleed.AffectedParts)
                            sb.Append($" {p}");
                    }
                }
            }

            var text = sb.ToString().Trim();
            if (text.Length == 0)
            {
                // Clear HUD if previously set
                if (lastHudText.TryGetValue(key, out var prev) && !string.IsNullOrEmpty(prev))
                {
                    SendHudHint(player, string.Empty, 1);
                    lastHudText[key] = string.Empty;
                }
                return;
            }

            if (text.Length > cfg.HudMaxLineLength)
                text = text.Substring(0, cfg.HudMaxLineLength);

            if (lastHudText.TryGetValue(key, out var last) && last == text)
                return; // no change

            SendHudHint(player, text, 8);
            lastHudText[key] = text;
        }

        private void SendHudHint(Player player, string text, int durationSeconds)
        {
            if (player == null)
                return;

            try
            {
                // Prefer direct API if available
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

            // Fallback to reflection on ReferenceHub
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

        private void ProcessDamage(Player victim, float damage, string damageType, string caliber, Player attacker, string bodyPart, int bleedDuration, float bleedDamagePerSecond, float bleedTickIntervalSeconds, string sourceName, bool applyHurt = true)
        {
            if (isDisposed)
                return;

            if (victim == null || damage <= 0f)
                return;

            var cfg = Plugin.Instance.Config;
            var key = GetPlayerKey(victim);

            try
            {
                if (cfg.Debug || cfg.DebugMode)
                    Log.Info($"[GunshotBleeding] ProcessDamage: {victim.Nickname} takes {damage} ({damageType}) from {(attacker?.Nickname ?? "env")}, part={bodyPart}");

                // Armor handling
                var hasArmor = false;
                try
                {
                    if (victim != null && victim.Inventory != null)
                        hasArmor = BodyArmorUtils.TryGetBodyArmor(victim.Inventory, out var armorItem);
                }
                catch
                {
                    hasArmor = false;
                    if (cfg.Debug || cfg.DebugMode)
                        Log.Warn("[GunshotBleeding] Failed to query armor via BodyArmorUtils.");
                }

                var isPenetrating = IsCaliberPenetrating(caliber, cfg);
                var isResisted = IsCaliberResisted(caliber, cfg);

                if (hasArmor)
                {
                    // If armor has a recorded durability and it's zero, treat as no armor
                    var prior = armorDurability.GetOrAdd(key, k => cfg.ArmorDurability);
                    if (prior <= 0f)
                        hasArmor = false;
                }

                if (hasArmor && isResisted)
                {
                    var reduced = damage * (1f - cfg.ArmorDamageReduction);
                    if (cfg.Debug || cfg.DebugMode)
                        Log.Info($"[GunshotBleeding] Armor resisted: damage {damage}->{reduced}");
                    damage = reduced;
                }

                if (hasArmor)
                {
                    try
                    {
                        var loss = cfg.ArmorDurabilityLossPerHit * (isPenetrating ? cfg.ArmorPenetrationDurabilityLossMultiplier : 1f);
                        armorDurability.AddOrUpdate(key, cfg.ArmorDurability - loss, (k, v) => Math.Max(0f, v - loss));
                        if (cfg.Debug || cfg.DebugMode)
                            Log.Info($"[GunshotBleeding] Armor durability for {victim.Nickname} now {armorDurability[key]}");
                        if (armorDurability.TryGetValue(key, out var remaining) && remaining <= 0f)
                        {
                            PlayBleedFeedback(victim, "armorbreak");
                            if (cfg.Debug || cfg.DebugMode)
                                Log.Info($"[GunshotBleeding] Armor broken for {victim.Nickname}");
                        }
                    }
                    catch
                    {
                        if (cfg.Debug || cfg.DebugMode)
                            Log.Warn("[GunshotBleeding] Failed to update armor durability.");
                    }
                }

                // Prevent non-human attackers (SCPs, hazards) from instantly killing via overly large damage values.
                try
                {
                    if ((attacker == null || !attacker.IsHuman) && damage >= victim.Health)
                    {
                        var capped = Math.Max(1f, victim.Health - 1f);
                        if (cfg.Debug || cfg.DebugMode)
                            Log.Info($"[GunshotBleeding] Capping non-human damage {damage} -> {capped} for {victim.Nickname}");
                        damage = capped;
                    }
                }
                catch { }

                // Apply damage via game API (use reflection to tolerate multiple EXILED signatures)
                if (applyHurt)
                try
                {
                    var hurtCalled = false;
                    var vType = victim.GetType();
                    // Try (float, string, string)
                    var m = vType.GetMethod("Hurt", new Type[] { typeof(float), typeof(string), typeof(string) });
                    if (m != null)
                    {
                        m.Invoke(victim, new object[] { damage, damageType, caliber });
                        hurtCalled = true;
                    }

                    if (!hurtCalled)
                    {
                        // Try (float, string)
                        m = vType.GetMethod("Hurt", new Type[] { typeof(float), typeof(string) });
                        if (m != null)
                        {
                            m.Invoke(victim, new object[] { damage, damageType });
                            hurtCalled = true;
                        }
                    }

                    if (!hurtCalled)
                    {
                        // Try (float)
                        m = vType.GetMethod("Hurt", new Type[] { typeof(float) });
                        if (m != null)
                        {
                            m.Invoke(victim, new object[] { damage });
                            hurtCalled = true;
                        }
                    }

                    if (!hurtCalled)
                    {
                        // As a last resort, try Victim.Kill when damage >= health
                        if (victim.Health <= damage)
                        {
                            DoKill(victim, DamageType.Unknown, damageType ?? "unknown", caliber);
                        }
                        else if (cfg.Debug || cfg.DebugMode)
                        {
                            Log.Warn($"[GunshotBleeding] No suitable Hurt overload found for victim; damage skipped.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (cfg.Debug || cfg.DebugMode)
                        Log.Warn($"[GunshotBleeding] Hurt call reflection failed for {victim.Nickname}: {ex.Message}");
                }

                // Apply bleeding if requested
                if (bleedDuration > 0)
                {
                    if (cfg.UseBuiltInBleedingEffect)
                        victim.EnableEffect(EffectType.Bleeding, bleedDuration);

                    ApplyBleedEffect(victim, bleedDuration, bleedDamagePerSecond, bleedTickIntervalSeconds, bodyPart);
                    ApplyInjuryState(victim, new BleedProfile { Duration = bleedDuration, DamagePerSecond = bleedDamagePerSecond, TickIntervalSeconds = bleedTickIntervalSeconds, Severity = bleedDuration >= cfg.HeavyBleedDuration ? "severe" : bleedDuration >= cfg.MediumBleedDuration ? "moderate" : "light", SourceName = sourceName });
                    PlayBleedFeedback(victim, sourceName);
                }

                UpdatePlayerHud(victim);

                if (cfg.Debug || cfg.DebugMode)
                    Log.Info($"[GunshotBleeding] ProcessDamage completed for {victim.Nickname}: final damage {damage}");
            }
            catch (Exception ex)
            {
                if (cfg.Debug || cfg.DebugMode)
                    Log.Error($"[GunshotBleeding] ProcessDamage exception: {ex}");
            }
        }

        private string GetPlayerKey(Player player)
        {
            if (player == null)
                return string.Empty;

            if (!string.IsNullOrEmpty(player.UserId))
                return player.UserId;

            return player.Id.ToString();
        }

        private string GetCaliberFromShot(ShotEventArgs ev)
        {
            if (ev == null)
                return null;

            try
            {
                var t = ev.GetType();
                foreach (var name in new[] { "Caliber", "Calibre", "AmmoType", "ProjectileCaliber", "WeaponName", "WeaponId" })
                {
                    var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (prop != null)
                    {
                        var val = prop.GetValue(ev);
                        if (val != null)
                            return val.ToString();
                    }
                }
            }
            catch
            {
                // ignore
            }

            // Try attacker-held weapon info via reflection
            try
            {
                var shooter = ev.Player;
                if (shooter != null)
                {
                    var hub = shooter.ReferenceHub;
                    if (hub != null)
                    {
                        var weaponProp = hub.GetType().GetProperty("CurItem", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        if (weaponProp != null)
                        {
                            var cur = weaponProp.GetValue(hub);
                            if (cur != null)
                                return cur.ToString();
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private bool IsCaliberPenetrating(string caliber, Config cfg)
        {
            if (string.IsNullOrEmpty(caliber) || cfg == null)
                return false;

            foreach (var p in cfg.ArmorPenetratingCalibers)
            {
                if (caliber.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private bool IsCaliberResisted(string caliber, Config cfg)
        {
            if (string.IsNullOrEmpty(caliber) || cfg == null)
                return false;

            foreach (var p in cfg.ArmorResistantCalibers)
            {
                if (caliber.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        public void OnUpgradingPlayer(UpgradingPlayerEventArgs ev)
        {
            if (ev.Player == null || ev.Player.IsHost || !ev.Player.IsHuman)
                return;

            if (ev.KnobSetting != Scp914.Scp914KnobSetting.Rough && ev.KnobSetting != Scp914.Scp914KnobSetting.Coarse)
                return;

            var cfg = Plugin.Instance.Config;
            var duration = ev.KnobSetting == Scp914.Scp914KnobSetting.Rough ? 6 : 8;
            var damagePerSecond = ev.KnobSetting == Scp914.Scp914KnobSetting.Rough ? 2.5f : 3f;

            if (cfg.UseBuiltInBleedingEffect)
                ev.Player.EnableEffect(EffectType.Bleeding, duration);
            ApplyBleedEffect(ev.Player, duration, damagePerSecond, 1f);
            ApplyInjuryState(ev.Player, new BleedProfile { Duration = duration, DamagePerSecond = damagePerSecond, TickIntervalSeconds = 1f, Severity = duration >= 8 ? "severe" : "moderate", SourceName = "scp914" });
            PlayBleedFeedback(ev.Player, "scp914");

            if (cfg.Debug)
                Log.Info($"[GunshotBleeding] {ev.Player.Nickname} suffered SCP-914 {ev.KnobSetting} injury → bleed {duration}s");
        }

        public void OnDied(Exiled.Events.EventArgs.Player.DiedEventArgs ev)
        {
            if (ev == null || ev.Player == null)
                return;

            var cfg = Plugin.Instance.Config;

            try
            {
                // Attempt to retrieve a Ragdoll/GameObject from the event via reflection
                object ragdollObj = null;
                var t = ev.GetType();
                foreach (var name in new[] { "Ragdoll", "RagDoll", "RagdollObject", "DeadRagdoll", "RagdollGameObject" })
                {
                    var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (prop != null)
                    {
                        ragdollObj = prop.GetValue(ev);
                        break;
                    }
                }

                // Some EXILED versions expose a 'Ragdoll' on the Player object
                if (ragdollObj == null)
                {
                    try
                    {
                        var playerType = ev.Player.GetType();
                        var prop = playerType.GetProperty("Ragdoll", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        if (prop != null)
                            ragdollObj = prop.GetValue(ev.Player);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                // Do not adjust ragdoll physics — heavy-body adjustments caused server lag.
                // Ragdoll tuning removed to avoid server performance issues.
            }
            catch
            {
                // Do not let ragdoll adjustments crash the server.
            }
        }

        private void AdjustRagdollPhysics(object ragdollObj, Config cfg)
        {
            if (ragdollObj == null || cfg == null)
                return;

            try
            {
                UnityEngine.GameObject go = null;

                if (ragdollObj is UnityEngine.GameObject)
                    go = (UnityEngine.GameObject)ragdollObj;
                else if (ragdollObj is UnityEngine.Component)
                    go = ((UnityEngine.Component)ragdollObj).gameObject;
                else
                {
                    var prop = ragdollObj.GetType().GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (prop != null)
                        go = prop.GetValue(ragdollObj) as UnityEngine.GameObject;
                }

                if (go == null)
                    return;

                var comps = go.GetComponentsInChildren<UnityEngine.Component>(true);
                if (comps == null || comps.Length == 0)
                    return;

                var rigidbodies = new System.Collections.Generic.List<object>();
                foreach (var comp in comps)
                {
                    if (comp == null)
                        continue;

                    var typeName = comp.GetType().Name;
                    if (string.Equals(typeName, "Rigidbody", StringComparison.OrdinalIgnoreCase) || string.Equals(typeName, "Rigidbody2D", StringComparison.OrdinalIgnoreCase))
                    {
                        rigidbodies.Add(comp);
                        try
                        {
                            var velProp = comp.GetType().GetProperty("velocity");
                            var angVelProp = comp.GetType().GetProperty("angularVelocity");
                            if (velProp != null)
                                velProp.SetValue(comp, Activator.CreateInstance(velProp.PropertyType));
                            if (angVelProp != null)
                                angVelProp.SetValue(comp, Activator.CreateInstance(angVelProp.PropertyType));
                        }
                        catch
                        {
                        }
                    }
                }

                if (rigidbodies.Count == 0)
                    return;

                if (cfg.Debug || cfg.DebugMode)
                    Log.Info($"[GunshotBleeding] AdjustRagdollPhysics: found {rigidbodies.Count} rigidbodies for ragdoll.");

                foreach (var rb in rigidbodies)
                {
                    try
                    {
                        var tRb = rb.GetType();
                        var massProp = tRb.GetProperty("mass");
                        var dragProp = tRb.GetProperty("drag");
                        var aDragProp = tRb.GetProperty("angularDrag");
                        if (massProp != null && massProp.CanWrite && massProp.CanRead)
                        {
                            var cur = Convert.ToSingle(massProp.GetValue(rb));
                            massProp.SetValue(rb, cur * cfg.RagdollMassMultiplier);
                        }

                        if (dragProp != null && dragProp.CanWrite && dragProp.CanRead)
                        {
                            var cur = Convert.ToSingle(dragProp.GetValue(rb));
                            dragProp.SetValue(rb, Math.Max(cur, cfg.RagdollDrag));
                        }

                        if (aDragProp != null && aDragProp.CanWrite && aDragProp.CanRead)
                        {
                            var cur = Convert.ToSingle(aDragProp.GetValue(rb));
                            aDragProp.SetValue(rb, Math.Max(cur, cfg.RagdollAngularDrag));
                        }
                    }
                    catch (Exception ex)
                    {
                        if (cfg.Debug || cfg.DebugMode)
                            Log.Warn($"[GunshotBleeding] AdjustRagdollPhysics reflection failure: {ex.Message}");
                    }
                }

                if (cfg.RagdollFreezeDuration > 0f)
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            foreach (var rb in rigidbodies)
                            {
                                try
                                {
                                    var isKin = rb.GetType().GetProperty("isKinematic");
                                    if (isKin != null && isKin.CanWrite)
                                        isKin.SetValue(rb, true);
                                }
                                catch { }
                            }

                            await Task.Delay(TimeSpan.FromSeconds(cfg.RagdollFreezeDuration));

                            foreach (var rb in rigidbodies)
                            {
                                try
                                {
                                    var isKin = rb.GetType().GetProperty("isKinematic");
                                    if (isKin != null && isKin.CanWrite)
                                        isKin.SetValue(rb, false);
                                }
                                catch { }
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    });
                }
            }
            catch
            {
                // swallow
            }
        }

        public void OnUsedItem(UsedItemEventArgs ev)
        {
            if (ev.Player == null || ev.Item == null)
                return;

            if (ev.Item.Type != ItemType.Medkit)
                return;

            ev.Player.DisableEffect(EffectType.Bleeding);
            RemoveBleedEffect(ev.Player);
            ResetInjuryDebuffs(ev.Player);
            injuryStates.TryRemove(GetPlayerKey(ev.Player), out _);

            // Update HUD when medkit stops bleeding
            UpdatePlayerHud(ev.Player);

            if (Plugin.Instance.Config.Debug)
                Log.Info($"[GunshotBleeding] {ev.Player.Nickname} used Medkit and stopped bleeding");
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

            // Update HUD when bleeding removed
            UpdatePlayerHud(player);
        }
    }
}
