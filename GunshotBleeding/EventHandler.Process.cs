using System;
using System.Reflection;
using Exiled.API.Enums;
using Exiled.API.Features;
using InventorySystem.Items.Armor;

namespace UltimateDamagePlugin
{
    public partial class EventHandler
    {
        private string GetPlayerKey(Player player)
        {
            if (player == null)
                return string.Empty;

            if (!string.IsNullOrEmpty(player.UserId))
                return player.UserId;

            return player.Id.ToString();
        }

        private void ProcessDamage(Player victim, float damage, string damageType, string caliber, Player attacker, string bodyPart, int bleedDuration, float bleedDamagePerSecond, float bleedTickIntervalSeconds, string sourceName, bool applyHurt = true, bool applyArmor = true)
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

                var isBleedSource = string.Equals(sourceName, "bleed", StringComparison.OrdinalIgnoreCase) || string.Equals(bodyPart, "BleedTick", StringComparison.OrdinalIgnoreCase);

                var hasArmor = false;
                try
                {
                    if (victim?.Inventory != null)
                        hasArmor = TryGetArmorItem(victim.Inventory, out var armorItem);
                }
                catch
                {
                    hasArmor = false;
                    if (cfg.Debug || cfg.DebugMode)
                        Log.Warn("[GunshotBleeding] Failed to query armor via reflection.");
                }

                if (hasArmor)
                {
                    var prior = armorDurability.GetOrAdd(key, k => cfg.ArmorDurability);
                    if (prior <= 0f)
                        hasArmor = false;
                }

                if (hasArmor && applyArmor)
                    ApplyArmorDamage(victim, caliber, ref damage);

                try
                {
                    if (!isBleedSource && (attacker == null || !attacker.IsHuman) && damage >= victim.Health)
                    {
                        var capped = Math.Max(1f, victim.Health - 1f);
                        if (cfg.Debug || cfg.DebugMode)
                            Log.Info($"[GunshotBleeding] Capping non-human damage {damage} -> {capped} for {victim.Nickname}");
                        damage = capped;
                    }
                }
                catch { }

                if (applyHurt)
                {
                    if (!string.IsNullOrEmpty(key))
                        skipNextHurting.TryAdd(key, 0);
                }
                try
                {
                    var hurtCalled = false;
                    var vType = victim.GetType();
                    var m = vType.GetMethod("Hurt", new Type[] { typeof(float), typeof(string), typeof(string) });
                    if (m != null)
                    {
                        m.Invoke(victim, new object[] { damage, damageType, caliber });
                        hurtCalled = true;
                    }

                    if (!hurtCalled)
                    {
                        m = vType.GetMethod("Hurt", new Type[] { typeof(float), typeof(string) });
                        if (m != null)
                        {
                            m.Invoke(victim, new object[] { damage, damageType });
                            hurtCalled = true;
                        }
                    }

                    if (!hurtCalled)
                    {
                        m = vType.GetMethod("Hurt", new Type[] { typeof(float) });
                        if (m != null)
                        {
                            m.Invoke(victim, new object[] { damage });
                            hurtCalled = true;
                        }
                    }

                    if (!hurtCalled)
                    {
                        if (victim.Health <= damage)
                        {
                            var reasonText = string.Equals(sourceName, "bleed", StringComparison.OrdinalIgnoreCase) || string.Equals(bodyPart, "BleedTick", StringComparison.OrdinalIgnoreCase)
                                ? "Died from blood loss"
                                : "Bullet holes found in body";
                            DoKill(victim, DamageType.Firearm, reasonText, caliber);
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
    }
}
