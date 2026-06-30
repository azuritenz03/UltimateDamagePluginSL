using Exiled.API.Interfaces;

namespace UltimateDamagePlugin
{
    public class Config : IConfig
    {
        public bool IsEnabled { get; set; } = true;

        // 🔴 REQUIRED by EXILED (this fixes your error)
        public bool Debug { get; set; } = false;

        public float BleedChance { get; set; } = 100f;

        public float ChestShotDamage { get; set; } = 50f;
        public float LimbShotDamage { get; set; } = 25f;
        public bool HeadshotInstantKill { get; set; } = true;

        public float ChestBleedChance { get; set; } = 100f;
        public int ChestBleedDuration { get; set; } = 18;
        public int ChestShotHitCount { get; set; } = 2;
        public int ChestShotWindowSeconds { get; set; } = 12;
        public float LimbBleedChance { get; set; } = 20f;
        public int LimbBleedDuration { get; set; } = 8;

        public float ArmorDamageReduction { get; set; } = 0.2f;
        public float ArmorBleedChanceModifier { get; set; } = 0.65f;
        public float ArmorBleedDurationModifier { get; set; } = 0.8f;
        // New armor durability and caliber behavior
        public float ArmorDurability { get; set; } = 100f;
        public float ArmorDurabilityLossPerHit { get; set; } = 12f;
        public float ArmorPenetrationDurabilityLossMultiplier { get; set; } = 2f;
        // Calibers that are generally resisted by armor (case-insensitive match)
        public string[] ArmorResistantCalibers { get; set; } = new[] { "9x19mm", "5.56mm" };
        // Calibers that often penetrate armor
        public string[] ArmorPenetratingCalibers { get; set; } = new[] { "7.62x39", "7.62x51", "12g" };

        public float RangeDamageFalloffStart { get; set; } = 12f;
        public float RangeDamageFalloffEnd { get; set; } = 40f;
        public float RangeMinimumDamagePercent { get; set; } = 0.6f;

        public float LightDamageThreshold { get; set; } = 10f;
        public float MediumDamageThreshold { get; set; } = 25f;
        public float HeavyDamageThreshold { get; set; } = 40f;

        public int LightBleedDuration { get; set; } = 5;
        public int MediumBleedDuration { get; set; } = 10;
        public int HeavyBleedDuration { get; set; } = 20;

        public float BleedDamagePerSecond { get; set; } = 2f;
        public float BleedTickIntervalSeconds { get; set; } = 1f;
        public bool BleedFeedbackEnabled { get; set; } = true;
        public float HumanDamageMultiplier { get; set; } = 1.25f;

        public bool EnableInjuryDebuffs { get; set; } = true;
        public float SevereBleedMovementPenalty { get; set; } = 0.25f;
        public float SevereBleedAimPenalty { get; set; } = 0.2f;
        public float RecoveryTickSeconds { get; set; } = 5f;
        public float RecoveryAmountPerTick { get; set; } = 1f;
        public float MedkitRecoveryAmount { get; set; } = 3f;
        public float FallInjuryThreshold { get; set; } = 12f;
        public float FallInjuryChance { get; set; } = 0.35f;
        public float HazardInjuryChance { get; set; } = 0.25f;
        public float HazardInjuryDamageMultiplier { get; set; } = 1.2f;
        // Default caliber string used in death/hurt messages when specific ammo is unavailable.
        public string DefaultCaliber { get; set; } = "9x19mm";
        // Use the game's built-in bleeding effect (may apply default large ticks). Disable to use plugin ticks only.
        public bool UseBuiltInBleedingEffect { get; set; } = false;

        // Ragdoll physics tuning
        public float RagdollMassMultiplier { get; set; } = 3f;
        public float RagdollDrag { get; set; } = 2f;
        public float RagdollAngularDrag { get; set; } = 0.9f;
        public float RagdollFreezeDuration { get; set; } = 0.35f;

        // HUD / Indicator options
        public bool EnableArmorHud { get; set; } = true;
        public string ArmorHudPosition { get; set; } = "TopRight"; // TopLeft/TopRight/BottomLeft/BottomRight
        public string ArmorHudColor { get; set; } = "#00BFFF";
        public bool EnableBleedHud { get; set; } = true;
        public string BleedHudPosition { get; set; } = "TopLeft";
        public string BleedHudColor { get; set; } = "#FF4500";
        // Maximum characters per HUD line to avoid overlapping other plugins
        public int HudMaxLineLength { get; set; } = 80;
        // Debug mode for verbose diagnostic logging and optional per-player overlay
        public bool DebugMode { get; set; } = false;
    }
}