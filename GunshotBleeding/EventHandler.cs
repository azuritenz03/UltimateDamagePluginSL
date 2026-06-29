using System;
using System.Collections.Generic;
using Exiled.Events.EventArgs.Player;
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
        private readonly HashSet<string> skipNextHurting = new HashSet<string>();

        private class ChestHitState
        {
            public int Count;
            public DateTime LastHitUtc;
        }

        public void OnHurting(HurtingEventArgs ev)
        {
            if (ev.Player == null || ev.Attacker == null)
                return;

            var key = GetPlayerKey(ev.Player);
            if (!string.IsNullOrEmpty(key) && skipNextHurting.Contains(key))
            {
                skipNextHurting.Remove(key);
                return;
            }

            if (!ev.Player.IsHuman || !ev.Attacker.IsHuman)
                return;

            var cfg = Plugin.Instance.Config;

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

                if (cfg.HeadshotInstantKill)
                    victim.Kill(DamageType.Firearm, "Headshot");
                else
                    victim.Hurt(victim.Health, "Headshot", string.Empty);

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
                victim.EnableEffect(EffectType.Bleeding, bloodTimer);

            if (cfg.Debug)
                Log.Info($"[GunshotBleeding] {ev.Player.Nickname} hit {victim.Nickname} in {hitboxType}, damage {damage}, bleed {bloodTimer}s");
        }

        private string GetPlayerKey(Player player)
        {
            if (player == null)
                return string.Empty;

            if (!string.IsNullOrEmpty(player.UserId))
                return player.UserId;

            return player.Id.ToString();
        }

        public void OnUsedItem(UsedItemEventArgs ev)
        {
            if (ev.Player == null || ev.Item == null)
                return;

            if (ev.Item.Type != ItemType.Medkit)
                return;

            ev.Player.DisableEffect(EffectType.Bleeding);

            if (Plugin.Instance.Config.Debug)
                Log.Info($"[GunshotBleeding] {ev.Player.Nickname} used Medkit and stopped bleeding");
        }
    }
}
