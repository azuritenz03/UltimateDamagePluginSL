using System;
using System.Collections.Concurrent;
using Exiled.API.Features;
using InventorySystem.Items.Armor;

namespace UltimateDamagePlugin
{
    public partial class EventHandler
    {
        private readonly ConcurrentDictionary<string, float> armorDurability = new ConcurrentDictionary<string, float>();

        private bool HasArmor(Player player, out float durability)
        {
            durability = 0f;
            if (player == null || player.Inventory == null)
                return false;

            try
            {
                if (!TryGetArmorItem(player.Inventory, out var armorItem))
                    return false;

                var key = GetPlayerKey(player);
                var prior = armorDurability.GetOrAdd(key, k => Plugin.Instance.Config.ArmorDurability);
                if (prior <= 0f)
                {
                    return false;
                }

                durability = prior;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyArmorDamage(Player player, string caliber, ref float damage)
        {
            if (player == null)
                return;

            var cfg = Plugin.Instance.Config;
            var key = GetPlayerKey(player);
            if (!HasArmor(player, out var durability))
                return;

            var reduced = damage * (1f - cfg.ArmorDamageReduction);
            if (cfg.Debug || cfg.DebugMode)
                Log.Info($"[GunshotBleeding] Armor resisted: damage {damage}->{reduced}");
            damage = reduced;

            try
            {
                var loss = cfg.ArmorDurabilityLossPerHit;
                armorDurability.AddOrUpdate(key, cfg.ArmorDurability - loss, (k, v) => Math.Max(0f, v - loss));
                if (cfg.Debug || cfg.DebugMode)
                    Log.Info($"[GunshotBleeding] Armor durability for {player.Nickname} now {armorDurability[key]}");
                if (armorDurability.TryGetValue(key, out var remaining) && remaining <= 0f)
                {
                    PlayBleedFeedback(player, "armorbreak");
                    if (cfg.Debug || cfg.DebugMode)
                        Log.Info($"[GunshotBleeding] Armor broken for {player.Nickname}");
                }
            }
            catch
            {
                if (cfg.Debug || cfg.DebugMode)
                    Log.Warn("[GunshotBleeding] Failed to update armor durability.");
            }
        }

        private float GetArmorDurability(Player player)
        {
            if (player == null)
                return 0f;

            if (armorDurability.TryGetValue(GetPlayerKey(player), out var value))
                return value;

            return 0f;
        }
    }
}
