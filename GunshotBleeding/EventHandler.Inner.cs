using System;
using System.Collections.Concurrent;
using System.Reflection;
using Exiled.API.Features;
using Exiled.Events.EventArgs.Player;
using Exiled.Events.EventArgs.Scp914;
using Exiled.API.Enums;
using InventorySystem.Items;
using InventorySystem.Items.Armor;

namespace UltimateDamagePlugin
{
    public partial class EventHandler
    {
        private readonly Random random = new Random();
        private readonly ConcurrentDictionary<string, byte> skipNextHurting = new ConcurrentDictionary<string, byte>();
        private volatile bool isDisposed = false;

        public void OnHurting(HurtingEventArgs ev)
        {
            if (ev.Player == null)
                return;

            var key = GetPlayerKey(ev.Player);
            if (!string.IsNullOrEmpty(key) && skipNextHurting.ContainsKey(key))
            {
                skipNextHurting.TryRemove(key, out _);
                return;
            }
        }

        public void OnDied(Exiled.Events.EventArgs.Player.DiedEventArgs ev)
        {
            if (ev == null || ev.Player == null)
                return;

            try
            {
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
                    }
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            try
            {
                isDisposed = true;
                activeBleeds.Clear();
                injuryStates.Clear();
                skipNextHurting.Clear();
                armorDurability.Clear();
                lastHudText.Clear();
            }
            catch
            {
            }
        }

        private bool TryGetArmorItem(object inventory, out object armorItem)
        {
            armorItem = null;
            if (inventory == null)
                return false;

            try
            {
                var type = inventory.GetType();
                var prop = type.GetProperty("BodyArmor", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (prop != null)
                {
                    armorItem = prop.GetValue(inventory);
                    return armorItem != null;
                }

                var method = type.GetMethod("TryGetBodyArmor", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (method != null)
                {
                    var parameters = new object[] { null };
                    var result = method.Invoke(inventory, parameters);
                    if (result is bool success && success)
                    {
                        armorItem = parameters[0];
                        return armorItem != null;
                    }
                }
            }
            catch
            {
            }

            return false;
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
            }

            try
            {
                victim.Kill(type, reason);
            }
            catch
            {
            }
        }
    }
}
