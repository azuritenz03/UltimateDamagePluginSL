using System;
using Exiled.Events.EventArgs.Player;
using Exiled.API.Enums;
using Exiled.API.Features;
using InventorySystem.Items.Armor;

namespace UltimateDamagePlugin
{
    public partial class EventHandler
    {
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
                var damage = hitboxType == HitboxType.Body ? cfg.ChestShotDamage : cfg.LimbShotDamage;
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
                    DoKill(victim, DamageType.Firearm, "Bullet wound to the head", cfg.DefaultCaliber);

                    if (cfg.Debug || cfg.DebugMode)
                        Log.Info($"[GunshotBleeding] {ev.Player.Nickname} scored headshot on {victim.Nickname}");

                    return;
                }

                damage *= cfg.HumanDamageMultiplier;
                damage *= distanceFactor;
                damage = Math.Max(1f, damage);

                if (victim?.Inventory != null)
                {
                    try
                    {
                        if (TryGetArmorItem(victim.Inventory, out var armorItem))
                        {
                            var prior = armorDurability.GetOrAdd(key, k => cfg.ArmorDurability);
                            if (prior > 0f)
                                ApplyArmorDamage(victim, cfg.DefaultCaliber, ref damage);
                        }
                    }
                    catch
                    {
                        if (cfg.Debug || cfg.DebugMode)
                            Log.Warn("[GunshotBleeding] Failed to evaluate armor during shot processing.");
                    }
                }

                var bleedProfile = CreateBleedProfile(damage, hitboxType, cfg);
                ev.CanHurt = false;
                skipNextHurting.TryAdd(key, 0);
                ProcessDamage(victim, damage, "Bullet wound", cfg.DefaultCaliber, ev.Player, hitboxType.ToString(), bleedProfile.Duration, bleedProfile.DamagePerSecond, bleedProfile.TickIntervalSeconds, "gunshot", true, false);

                if (cfg.Debug || cfg.DebugMode)
                    Log.Info($"[GunshotBleeding] {ev.Player.Nickname} hit {victim.Nickname} in {hitboxType}, damage={damage}, bleed={bleedProfile.Duration}s, severity={bleedProfile.Severity}");
            }
            catch (Exception ex)
            {
                var cfg2 = Plugin.Instance?.Config;
                if (cfg2 != null && (cfg2.Debug || cfg2.DebugMode))
                    Log.Error($"[GunshotBleeding] OnShot exception: {ex}");
            }
        }
    }
}
