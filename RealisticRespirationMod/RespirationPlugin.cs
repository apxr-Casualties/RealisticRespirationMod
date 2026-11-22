using BepInEx;
using BepInEx.Logging;
using CUCoreLib;
using HarmonyLib;
using RealisticRespirationMod.Compat;

namespace RealisticRespirationMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(CUCoreLibPlugin.GUID, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(CardiacModGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(NeuroModGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class RespirationPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.casualties.realistic.respiration";
        public const string PluginName = "Realistic Respiration & Blood Gas Mod";
        public const string PluginVersion = "1.0.0";
        public const string CardiacModGuid = "com.casualties.realistic.ecg";
        public const string NeuroModGuid = "com.casualties.neurology.overhaul";

        internal static ManualLogSource? Log;
        private Harmony? _harmony;

        private void Awake()
        {
            Log = Logger;
            RespirationConfig.Bind(Config);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Patches.BodyRespirationPatches));
            _harmony.PatchAll(typeof(Patches.CrateBonusDropPatches));

            // Initialize zero-compile soft bridges to RealisticCardiacEcgMod & NeurologyOverhaulMod
            CardiacCompatBridge.TryInitialize(_harmony, Logger);
            NeuroCompatBridge.TryInitialize(Logger);

            CUCoreIntegration.RegisterAll(Logger);

            Logger.LogInfo(
                $"{PluginName} v{PluginVersion} loaded " +
                $"(CardiacMod: {CardiacCompatBridge.IsLoaded}, NeuroMod: {NeuroCompatBridge.IsLoaded}).");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            RespirationModManager.DisposeAll();
        }
    }
}
