using System.Runtime.CompilerServices;
using HarmonyLib;
using RealisticRespirationMod.UI;
using RespirationSimCore;
using UnityEngine;

namespace RealisticRespirationMod.Patches
{
    [HarmonyPatch]
    internal static class BodyRespirationPatches
    {
        private static readonly ConditionalWeakTable<WoundView, WoundViewAbgWidget> AbgWidgets =
            new ConditionalWeakTable<WoundView, WoundViewAbgWidget>();

        [HarmonyPatch(typeof(Body), "Start")]
        [HarmonyPostfix]
        private static void Body_Start_Postfix(Body __instance)
        {
            if (__instance == null) return;
            var ctrl = RespirationModManager.GetOrCreate(__instance);
            if (!SaveSystem.loadedRun)
            {
                // Initialize new run at physiological resting ~15/m instead of vanilla 25/m (100f)
                float restScaled = BodyRespirationSimulator.RestingRpm * BodyRespirationSimulator.VanillaRespScale;
                __instance.respiratoryRate = restScaled;
                ctrl.LastWrittenRespRate = restScaled;
                ctrl.LastWrittenBloodOxygen = __instance.bloodOxygen;
            }
        }

        [HarmonyPatch(typeof(Body), nameof(Body.HandleCirculation))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static void Body_HandleCirculation_Prefix(Body __instance)
        {
            if (__instance == null) return;
            RespirationModManager.CapturePreCirculation(__instance);
        }

        [HarmonyPatch(typeof(Body), nameof(Body.HandleCirculation))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Body_HandleCirculation_Postfix(Body __instance, Painkillers painkillers)
        {
            if (__instance == null) return;
            RespirationModManager.StepAfterCirculation(__instance, painkillers, Time.deltaTime);
        }

        [HarmonyPatch(typeof(Body), "HandleBody")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void Body_HandleBody_Postfix(Body __instance)
        {
            if (__instance == null) return;
            RespirationModManager.ApplyPostHandleBodyEffects(__instance, Time.deltaTime);

            if (PlayerCamera.main != null && PlayerCamera.main.body == __instance)
            {
                CUCoreIntegration.QueueMoodlesForBody(__instance);
            }
        }

        [HarmonyPatch(typeof(Body), nameof(Body.Heal))]
        [HarmonyPostfix]
        private static void Body_Heal_Postfix(Body __instance)
        {
            RespirationModManager.ResetBody(__instance, clearDrugs: true, clearModifiers: true);
        }

        [HarmonyPatch(typeof(Body), nameof(Body.TryLastStand))]
        [HarmonyPostfix]
        private static void Body_TryLastStand_Postfix(Body __instance)
        {
            if (__instance != null && __instance.succesfullyRolledLastStand)
            {
                RespirationModManager.ResetBody(__instance, clearDrugs: false, clearModifiers: false);
            }
        }

        /// <summary>
        /// Suppresses vanilla MoodleManager false-positive "hypoventilation" warnings at normal resting 12-20/m
        /// (vanilla assumed normal = 100f = 25/m and warned whenever respiratoryRate &lt; 90f = 22.5/m).
        /// </summary>
        [HarmonyPatch(typeof(MoodleManager), nameof(MoodleManager.AddMoodle))]
        [HarmonyPrefix]
        private static bool MoodleManager_AddMoodle_Prefix(ref int intensity, string icon)
        {
            if (icon != "hypoventilation") return true;

            Body? body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return true;

            var ctrl = RespirationModManager.TryGet(body);
            if (ctrl == null || !ctrl.HasSteppedOnce) return true;

            float rpm = ctrl.Sim.CurrentRpm;
            if (rpm >= 11.0f)
            {
                // Normal resting (12-20/m) or tachypnea: suppress vanilla hypoventilation warning
                return false;
            }

            // Clinically accurate bradypnea tiers (< 11/m mild, < 7.5/m severe)
            intensity = rpm < 7.5f ? 1 : 0;
            return true;
        }

        [HarmonyPatch(typeof(WoundView), nameof(WoundView.UpdateView))]
        [HarmonyPostfix]
        private static void WoundView_UpdateView_Postfix(WoundView __instance)
        {
            if (__instance == null || __instance.body == null) return;

            var ctrl = RespirationModManager.TryGet(__instance.body);
            if (ctrl != null && ctrl.HasSteppedOnce && __instance.respiratoryText != null)
            {
                ref readonly var s = ref ctrl.Sim.LastTelemetry;
                bool alert = !s.Breathing || s.IsPulmonaryFailure || s.ActualRpm < 6.0f || s.ActualRpm >= 40.0f;
                if (alert)
                {
                    float flash = Mathf.PingPong(Time.unscaledTime * 4f, 1f);
                    __instance.respiratoryText.color = Color.Lerp(Color.white, new Color(1f, 0.28f, 0.28f, 1f), flash);
                }
            }

            // Update ABG button right of oxyText + compact floating ABG card + respiratory tooltip
            var widget = AbgWidgets.GetOrCreateValue(__instance);
            widget.EnsureAndUpdate(__instance);
        }

        [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleInput))]
        [HarmonyPostfix]
        private static void PlayerCamera_HandleInput_Postfix(PlayerCamera __instance)
        {
            if (__instance == null || __instance.body == null) return;
            if (ConsoleScript.instance && ConsoleScript.instance.active) return;
            if (PauseHandler.paused || __instance.itemCustomizer.activeSelf || __instance.tradeMenu.activeSelf) return;
            if (__instance.craftingPanel.activeSelf && __instance.recipeFilterField != null && __instance.recipeFilterField.isFocused) return;

            KeyCode boundKey = KeyBinds.GetBind("realisticresp.abg_hotkey");
            KeyCode activeKey = boundKey != KeyCode.None ? boundKey : RespirationConfig.AbgAnalyzerHotkey;
            RespirationConfig.AbgAnalyzerHotkey = activeKey;

            if (activeKey != KeyCode.None && Input.GetKeyDown(activeKey))
            {
                RespirationModManager.OpenOrToggleAbgMinigame(__instance.body, item: null);
            }
        }
    }
}
