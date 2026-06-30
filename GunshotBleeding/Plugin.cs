using Exiled.API.Features;

namespace UltimateDamagePlugin
{
    public class Plugin : Plugin<Config>
    {
        public static Plugin Instance;

        private EventHandler handler;

        public override void OnEnabled()
        {
            Instance = this;
            handler = new EventHandler();
            ValidateConfig();
            Exiled.Events.Handlers.Player.Hurting += handler.OnHurting;
            Exiled.Events.Handlers.Player.Shot += handler.OnShot;
            Exiled.Events.Handlers.Player.Died += handler.OnDied;
            Exiled.Events.Handlers.Player.UsedItem += handler.OnUsedItem;
            base.OnEnabled();
        }

        public override void OnDisabled()
        {
            Exiled.Events.Handlers.Player.Hurting -= handler.OnHurting;
            Exiled.Events.Handlers.Player.Shot -= handler.OnShot;
            Exiled.Events.Handlers.Player.Died -= handler.OnDied;
            Exiled.Events.Handlers.Player.UsedItem -= handler.OnUsedItem;
            try
            {
                handler?.Dispose();
            }
            catch { }
            handler = null;
            Instance = null;
            base.OnDisabled();
        }

        private void ValidateConfig()
        {
            var cfg = Instance.Config;
            if (cfg == null)
                return;

            bool changed = false;
            if (cfg.BleedTickIntervalSeconds <= 0f)
            {
                cfg.BleedTickIntervalSeconds = 1f;
                changed = true;
            }

            if (cfg.HudMaxLineLength < 20) { cfg.HudMaxLineLength = 20; changed = true; }
            if (cfg.HudMaxLineLength > 240) { cfg.HudMaxLineLength = 240; changed = true; }

            if (cfg.ArmorDurability < 0f) { cfg.ArmorDurability = 0f; changed = true; }
            if (cfg.ArmorDurability > 10000f) { cfg.ArmorDurability = 10000f; changed = true; }

            if (changed && cfg.Debug)
                Log.Info("[GunshotBleeding] Config validation adjusted invalid values to safe defaults.");
        }
    }
}