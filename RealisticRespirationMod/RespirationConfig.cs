using BepInEx.Configuration;
using RespirationSimCore;
using UnityEngine;

namespace RealisticRespirationMod
{
    public static class RespirationConfig
    {
        public static float MaxRespiratoryRateBpm { get; set; } = 80f;
        public static float Co2DynamicsRateMultiplier { get; set; } = 1.0f;
        public static float HemothoraxFailureThreshold { get; set; } = 85f;
        public static bool EnablePulmonaryFailure { get; set; } = true;
        public static bool EnableAcidBaseSystemicEffects { get; set; } = true;
        public static float ExtraCrateDropChance { get; set; } = 0.35f;
        public static KeyCode AbgAnalyzerHotkey { get; set; } = KeyCode.K;

        public static void Bind(ConfigFile config)
        {
            var maxRr = config.Bind("Physiology", "MaxRespiratoryRateBpm", 80f,
                "Maximum physiological respiratory rate in breaths/min (default 80/m, replacing vanilla 25/m cap).");
            var co2Rate = config.Bind("Physiology", "Co2DynamicsRateMultiplier", 1.0f,
                "Multiplier for PaCO2/PaO2 gas exchange speed (0.25 - 3.0).");
            var hemoFail = config.Bind("Physiology", "HemothoraxFailureThreshold", 85f,
                "Hemothorax percentage at which acute pulmonary failure prevents breathing (50 - 99).");
            var enableFail = config.Bind("Physiology", "EnablePulmonaryFailure", true,
                "Enable pulmonary failure (thorax destruction, severe hemothorax, fatal PE) preventing ventilation.");
            var enableAb = config.Bind("Physiology", "EnableAcidBaseSystemicEffects", true,
                "Enable blood pH & PaCO2 systemic effects (CO2 narcosis, Kussmaul drive, cardiac/neuro acid-base coupling).");
            var dropChance = config.Bind("Loot", "ExtraCrateDropChance", 0.35f,
                "Independent bonus drop chance (0.0 - 1.0) on crates/capsules for Nikethamide, NaHCO3, and Portable ABG Analyzer.");

            MaxRespiratoryRateBpm = Mathf.Clamp(maxRr.Value, 25f, 120f);
            Co2DynamicsRateMultiplier = Mathf.Clamp(co2Rate.Value, 0.25f, 3.0f);
            HemothoraxFailureThreshold = Mathf.Clamp(hemoFail.Value, 50f, 99f);
            EnablePulmonaryFailure = enableFail.Value;
            EnableAcidBaseSystemicEffects = enableAb.Value;
            ExtraCrateDropChance = Mathf.Clamp01(dropChance.Value);

            maxRr.SettingChanged += (_, _) => MaxRespiratoryRateBpm = Mathf.Clamp(maxRr.Value, 25f, 120f);
            co2Rate.SettingChanged += (_, _) => Co2DynamicsRateMultiplier = Mathf.Clamp(co2Rate.Value, 0.25f, 3.0f);
            hemoFail.SettingChanged += (_, _) => HemothoraxFailureThreshold = Mathf.Clamp(hemoFail.Value, 50f, 99f);
            enableFail.SettingChanged += (_, _) => EnablePulmonaryFailure = enableFail.Value;
            enableAb.SettingChanged += (_, _) => EnableAcidBaseSystemicEffects = enableAb.Value;
            dropChance.SettingChanged += (_, _) => ExtraCrateDropChance = Mathf.Clamp01(dropChance.Value);
        }

        public static RespirationSimConfig ToSimConfig() => new RespirationSimConfig
        {
            MaxRespiratoryRateBpm = MaxRespiratoryRateBpm,
            Co2DynamicsRateMultiplier = Co2DynamicsRateMultiplier,
            HemothoraxFailureThreshold = HemothoraxFailureThreshold,
            EnablePulmonaryFailure = EnablePulmonaryFailure,
            EnableAcidBaseSystemicEffects = EnableAcidBaseSystemicEffects
        };
    }
}
