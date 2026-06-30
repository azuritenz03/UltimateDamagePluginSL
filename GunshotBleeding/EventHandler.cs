using System;
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

namespace GunshotBleeding
{
    public class EventHandler
    {
        private readonly Random random = new Random();
        private readonly Dictionary<string, ChestHitState> chestHitHistory = new Dictionary<string, ChestHitState>();
        private readonly Dictionary<string, BleedTickState> activeBleeds = new Dictionary<string, BleedTickState>();
        private readonly Dictionary<string, InjuryState> injuryStates = new Dictionary<string, InjuryState>();
        private readonly HashSet<string> skipNextHurting = new HashSet<string>();

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
                        ev.Player.EnableEffect(EffectType.Bleeding, profile.Duration);
                        ApplyBleedEffect(ev.Player, profile.Duration, profile.DamagePerSecond, profile.TickIntervalSeconds);
                        ApplyInjuryState(ev.Player, profile);
                        PlayBleedFeedback(ev.Player, profile.SourceName);
                    }
                }

                return;
            }

            if (!string.IsNullOrEmpty(key) && skipNextHurting.Contains(key))
            {
                skipNextHurting.Remove(key);
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

            ev.Player.EnableEffect(EffectType.Bleeding, duration);
            ApplyBleedEffect(ev.Player, duration, cfg.BleedDamagePerSecond, cfg.BleedTickIntervalSeconds);
            ApplyInjuryState(ev.Player, new BleedProfile { Duration = duration, DamagePerSecond = cfg.BleedDamagePerSecond, TickIntervalSeconds = cfg.BleedTickIntervalSeconds, Severity = duration >= cfg.HeavyBleedDuration ? "severe" : duration >= cfg.MediumBleedDuration ? "moderate" : "light", SourceName = "gunshot" });
            PlayBleedFeedback(ev.Player, "gunshot");

            if (cfg.Debug)
                Log.Info($"[GunshotBleeding] {ev.Player.Nickname} took {ev.Amount} damage from {ev.Attacker.Nickname} → bleed {duration}s");
        }

        public void OnShot(ShotEventArgs ev)
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
                skipNextHurting.Add(key);

                victim.Kill(DamageType.Firearm, "Headshot");

                if (cfg.Debug)
                    Log.Info($"[GunshotBleeding] {ev.Player.Nickname} scored headshot on {victim.Nickname}");

                chestHitHistory.Remove(key);
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
                    hasArmor = BodyArmorUtils.TryGetBodyArmor(victim.Inventory, out var _);
            }
            catch
            {
                hasArmor = false;
            }

            damage *= cfg.HumanDamageMultiplier;
            damage *= distanceFactor;

            if (hasArmor)
            {
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
                    skipNextHurting.Add(key);
                    victim.Kill(DamageType.Firearm, "Chest trauma");
                    chestHitHistory.Remove(key);

                    if (cfg.Debug)
                        Log.Info($"[GunshotBleeding] {ev.Player.Nickname} finished {victim.Nickname} with a second chest shot");

                    return;
                }
            }

            damage = Math.Max(1f, damage);
            ev.CanHurt = false;
            skipNextHurting.Add(key);
            victim.Hurt(damage, "Gunshot", string.Empty);

            if (bleed && bloodTimer > 0)
            {
                victim.EnableEffect(EffectType.Bleeding, bloodTimer);
                ApplyBleedEffect(victim, bloodTimer, cfg.BleedDamagePerSecond, cfg.BleedTickIntervalSeconds);
                ApplyInjuryState(victim, new BleedProfile { Duration = bloodTimer, DamagePerSecond = cfg.BleedDamagePerSecond, TickIntervalSeconds = cfg.BleedTickIntervalSeconds, Severity = bloodTimer >= cfg.HeavyBleedDuration ? "severe" : bloodTimer >= cfg.MediumBleedDuration ? "moderate" : "light", SourceName = "gunshot" });
                PlayBleedFeedback(victim, "gunshot");
            }

            if (cfg.Debug)
                Log.Info($"[GunshotBleeding] {ev.Player.Nickname} hit {victim.Nickname} in {hitboxType}, damage {damage}, bleed {bloodTimer}s");
        }

        private void ApplyBleedEffect(Player player, int durationSeconds, float damagePerSecond, float tickIntervalSeconds)
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
                return;
            }

            var state = new BleedTickState
            {
                DamagePerSecond = damagePerSecond,
                RemainingTicks = GetTickCount(durationSeconds, tickIntervalSeconds),
                TickIntervalSeconds = tickIntervalSeconds
            };

            activeBleeds[key] = state;
            StartBleedTick(player, key, state);
        }

        private void StartBleedTick(Player player, string key, BleedTickState state)
        {
            _ = Task.Run(async () =>
            {
                while (activeBleeds.TryGetValue(key, out var currentState) && currentState.RemainingTicks > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.25f, currentState.TickIntervalSeconds)));

                    if (player == null || !player.IsConnected || !player.IsAlive || player.Health <= 0f)
                        break;

                    if (currentState.RemainingTicks <= 0)
                        break;

                    var perTickDamage = Math.Max(1f, currentState.DamagePerSecond * currentState.TickIntervalSeconds);
                    skipNextHurting.Add(key);
                    player.Hurt(perTickDamage, "Bleeding", string.Empty);
                    PlayBleedFeedback(player, "bleed");
                    currentState.RemainingTicks--;
                }

                if (activeBleeds.ContainsKey(key))
                {
                    activeBleeds.Remove(key);
                    StartRecoveryLoop(player, key);
                }
            });
        }

        private void StartRecoveryLoop(Player player, string key)
        {
            _ = Task.Run(async () =>
            {
                while (player != null && player.IsConnected && player.IsAlive && player.Health > 0f)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1f, Plugin.Instance.Config.RecoveryTickSeconds)));

                    if (player == null || !player.IsConnected || !player.IsAlive || player.Health <= 0f)
                        break;

                    if (!injuryStates.TryGetValue(key, out var state))
                        break;

                    state.RemainingSeverity = Math.Max(0f, state.RemainingSeverity - Plugin.Instance.Config.RecoveryAmountPerTick);
                    if (state.RemainingSeverity <= 0f)
                    {
                        injuryStates.Remove(key);
                        ResetInjuryDebuffs(player);
                        break;
                    }

                    ApplyInjuryDebuffs(player, state);
                }
            });
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
                var movementSpeed = player.ReferenceHub.gameObject.GetComponent<UnityEngine.Component>() != null ? 0f : 0f;
                if (movementSpeed < 0f)
                    return;
            }
            catch
            {
                // Ignore.
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

                    foreach (var methodName in new[] { "PlayAmbientSound", "RpcPlaySound", "PlaySound", "SendAudio", "PlayPainSound", "PlayGunSound" })
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

        private string GetPlayerKey(Player player)
        {
            if (player == null)
                return string.Empty;

            if (!string.IsNullOrEmpty(player.UserId))
                return player.UserId;

            return player.Id.ToString();
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

            ev.Player.EnableEffect(EffectType.Bleeding, duration);
            ApplyBleedEffect(ev.Player, duration, damagePerSecond, 1f);
            ApplyInjuryState(ev.Player, new BleedProfile { Duration = duration, DamagePerSecond = damagePerSecond, TickIntervalSeconds = 1f, Severity = duration >= 8 ? "severe" : "moderate", SourceName = "scp914" });
            PlayBleedFeedback(ev.Player, "scp914");

            if (cfg.Debug)
                Log.Info($"[GunshotBleeding] {ev.Player.Nickname} suffered SCP-914 {ev.KnobSetting} injury → bleed {duration}s");
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
            injuryStates.Remove(GetPlayerKey(ev.Player));

            if (Plugin.Instance.Config.Debug)
                Log.Info($"[GunshotBleeding] {ev.Player.Nickname} used Medkit and stopped bleeding");
        }

        private void RemoveBleedEffect(Player player)
        {
            if (player == null)
                return;

            var key = GetPlayerKey(player);
            if (!string.IsNullOrEmpty(key))
                activeBleeds.Remove(key);
        }
    }
}
