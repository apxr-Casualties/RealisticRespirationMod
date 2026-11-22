using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealisticRespirationMod.Patches
{
    /// <summary>
    /// Independent bonus drop on Medical Crates, Supply Crates, Drop Capsules, and Lifepod Chests
    /// for Nikethamide, Sodium Bicarbonate, and Portable ABG Analyzer without diluting vanilla loot tables.
    /// </summary>
    [HarmonyPatch]
    internal static class CrateBonusDropPatches
    {
        private static readonly HashSet<int> ProcessedEntities = new HashSet<int>();

        [HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.Update))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static void BuildingEntity_Update_Prefix(BuildingEntity __instance)
        {
            if (__instance == null || __instance.health >= 0.5f) return;
            if (!IsEligibleCrate(__instance.id)) return;

            int instanceId = __instance.GetInstanceID();
            if (!ProcessedEntities.Add(instanceId)) return;

            if (ProcessedEntities.Count > 2048)
            {
                ProcessedEntities.Clear();
                ProcessedEntities.Add(instanceId);
            }

            if (UnityEngine.Random.value >= RespirationConfig.ExtraCrateDropChance) return;

            string[] pool = CUCoreIntegration.BonusCrateDropPool;
            if (pool.Length == 0) return;

            string chosenId = pool[UnityEngine.Random.Range(0, pool.Length)];
            try
            {
                GameObject? spawned = Utils.Create(chosenId, __instance.transform.position, UnityEngine.Random.Range(0f, 360f));
                if (spawned != null)
                {
                    if (spawned.TryGetComponent<Rigidbody2D>(out var rb))
                    {
                        rb.velocity = new Vector2(UnityEngine.Random.Range(-6.0f, 6.0f), UnityEngine.Random.Range(1.5f, 6.0f));
                    }

                    bool nearPlayer = PlayerCamera.main != null &&
                                      PlayerCamera.main.body != null &&
                                      Vector2.Distance(__instance.transform.position, PlayerCamera.main.body.transform.position) < 8f;
                    if (nearPlayer && spawned.GetComponent<FreshItemDrop>() == null)
                    {
                        spawned.AddComponent<FreshItemDrop>();
                    }
                }
            }
            catch (Exception ex)
            {
                RespirationPlugin.Log?.LogWarning($"Failed to spawn bonus respiration drop '{chosenId}': {ex.Message}");
            }
        }

        private static bool IsEligibleCrate(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            string norm = id.Trim().ToLowerInvariant();
            return norm == "medcrate" ||
                   norm == "containercrate" ||
                   norm == "dropcapsule" ||
                   norm == "lifepodchest";
        }
    }
}
