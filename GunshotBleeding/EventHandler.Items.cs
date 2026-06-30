using System;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.Events.EventArgs.Player;
using InventorySystem.Items;

namespace UltimateDamagePlugin
{
    public partial class EventHandler
    {
        public void OnUsedItem(UsedItemEventArgs ev)
        {
            if (ev.Player == null || ev.Item == null)
                return;

            if (!IsBleedCureItem(ev.Item.Type))
                return;

            ev.Player.DisableEffect(EffectType.Bleeding);
            RemoveBleedEffect(ev.Player);
            ResetInjuryDebuffs(ev.Player);
            injuryStates.TryRemove(GetPlayerKey(ev.Player), out _);

            UpdatePlayerHud(ev.Player);

            if (Plugin.Instance.Config.Debug)
                Log.Info($"[GunshotBleeding] {ev.Player.Nickname} used {ev.Item.Type} and stopped bleeding");
        }

        private bool IsBleedCureItem(ItemType itemType)
        {
            var name = itemType.ToString();
            return string.Equals(name, "Medkit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Scp500", StringComparison.OrdinalIgnoreCase);
        }
    }
}
