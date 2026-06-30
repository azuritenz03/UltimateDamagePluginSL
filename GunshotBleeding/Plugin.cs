using Exiled.API.Features;

namespace GunshotBleeding
{
    public class Plugin : Plugin<Config>
    {
        public static Plugin Instance;

        private EventHandler handler;

        public override void OnEnabled()
        {
            Instance = this;
            handler = new EventHandler();
            Exiled.Events.Handlers.Player.Hurting += handler.OnHurting;
            Exiled.Events.Handlers.Player.Shot += handler.OnShot;
            Exiled.Events.Handlers.Player.UsedItem += handler.OnUsedItem;
            Exiled.Events.Handlers.Scp914.UpgradingPlayer += handler.OnUpgradingPlayer;
            base.OnEnabled();
        }

        public override void OnDisabled()
        {
            Exiled.Events.Handlers.Player.Hurting -= handler.OnHurting;
            Exiled.Events.Handlers.Player.Shot -= handler.OnShot;
            Exiled.Events.Handlers.Player.UsedItem -= handler.OnUsedItem;
            Exiled.Events.Handlers.Scp914.UpgradingPlayer -= handler.OnUpgradingPlayer;
            handler = null;
            Instance = null;
            base.OnDisabled();
        }
    }
}