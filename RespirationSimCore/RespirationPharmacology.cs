using System;

namespace RespirationSimCore
{
    /// <summary>
    /// Pharmacokinetics & pharmacodynamics for respiratory / acid-base rescue drugs:
    /// 1) Nikethamide (尼可刹米注射液): central medullary respiratory stimulant (injectable; oral 0.5x).
    ///    Raises medullary excitability & CO2 chemosensitivity; overdose (>1.6) causes CNS tremor/convulsion toxicity.
    /// 2) 5% Sodium Bicarbonate (碳酸氢钠注射液): buffers metabolic acidosis by raising plasma HCO3-,
    ///    while stoichiometrically liberating CO2 (H+ + HCO3- -> H2O + CO2) which requires intact ventilation to exhale!
    /// </summary>
    public sealed class RespirationPharmacology
    {
        // Nikethamide (half-life ~150s game time; 4 mL IV -> ~1.0 therapeutic peak)
        public float NikethamideDepot;
        public float NikethamideConc;

        // Sodium Bicarbonate infusion depot (delivers mmol/L HCO3- over ~18s IV)
        public float BicarbonateDepotMmolL;

        public float NikethamideExcitabilityAdd =>
            NikethamideConc > 0.02f
                ? Math.Min(2.4f, NikethamideConc * 1.15f)
                : 0f;

        public float NikethamideChemoSensitivityMult =>
            1.0f + Math.Min(1.1f, NikethamideConc * 0.55f);

        /// <summary>
        /// Narrow therapeutic index: >1.55 triggers CNS hyperexcitability (tremor & lowered seizure threshold).
        /// </summary>
        public float NikethamideConvulsionToxicity =>
            NikethamideConc > 1.55f
                ? RespMath.Clamp((NikethamideConc - 1.55f) / 1.45f, 0f, 2.0f)
                : 0f;

        public float Administer(RespirationDrugId drug, float ml, RespirationAdminRoute route)
        {
            if (ml <= 0f) return 0f;

            switch (drug)
            {
                case RespirationDrugId.Nikethamide:
                {
                    // Injectable formulation: full potency IV/IM, 0.5x oral
                    float routeMult = route == RespirationAdminRoute.Injectable ? 1.0f : 0.5f;
                    float delivered = (ml / 4.0f) * routeMult;
                    if (route == RespirationAdminRoute.Injectable)
                    {
                        NikethamideConc += delivered * 0.40f;
                        NikethamideDepot += delivered * 0.60f;
                    }
                    else
                    {
                        NikethamideDepot += delivered;
                    }
                    return delivered;
                }

                case RespirationDrugId.SodiumBicarbonate:
                {
                    // 5% NaHCO3 injectable: 15 mL IV delivers ~6.0 mmol/L plasma HCO3- equivalent; oral 0.45x
                    float routeMult = route == RespirationAdminRoute.Injectable ? 1.0f : 0.45f;
                    float deliveredMmolL = (ml / 15.0f) * 6.0f * routeMult;
                    BicarbonateDepotMmolL += deliveredMmolL;
                    return deliveredMmolL;
                }

                default:
                    return 0f;
            }
        }

        public void Step(float dt, float currentPh, out float hco3GainMmolL, out float co2ReleaseMmHg)
        {
            hco3GainMmolL = 0f;
            co2ReleaseMmHg = 0f;
            if (dt <= 0f) return;

            // 1. Nikethamide 1-compartment PK (fast absorption, ~150s half-life)
            if (NikethamideDepot > 0.0001f)
            {
                float absorbed = NikethamideDepot * (1f - (float)Math.Exp(-0.18f * dt));
                NikethamideDepot -= absorbed;
                NikethamideConc += absorbed;
            }
            else
            {
                NikethamideDepot = 0f;
            }

            if (NikethamideConc > 0.0001f)
            {
                const float kElim = 0.693147f / 150f;
                NikethamideConc *= (float)Math.Exp(-kElim * dt);
            }
            else
            {
                NikethamideConc = 0f;
            }

            // 2. Sodium Bicarbonate infusion absorption & CO2 liberation (scaled to match V1.0.2 blood gas kinetics so intact ventilation exhales liberated CO2 smoothly)
            if (BicarbonateDepotMmolL > 0.001f)
            {
                float absorbedHco3 = BicarbonateDepotMmolL * (1f - (float)Math.Exp(-0.065f * dt));
                BicarbonateDepotMmolL -= absorbedHco3;
                hco3GainMmolL = absorbedHco3;

                // Stoichiometric CO2 release is higher in acidemic blood (more H+ available to react with HCO3-)
                float acidFactor = currentPh < 7.40f
                    ? (1.0f + RespMath.Clamp((7.40f - currentPh) * 2.4f, 0f, 1.2f))
                    : 0.70f;
                // ~2.6 mmHg CO2 burden per mmol/L HCO3- buffered (exhaled quickly if ventilating, traps if lung failure!)
                co2ReleaseMmHg = absorbedHco3 * 2.6f * acidFactor;
            }
            else
            {
                BicarbonateDepotMmolL = 0f;
            }
        }

        public void ClearAll()
        {
            NikethamideDepot = 0f;
            NikethamideConc = 0f;
            BicarbonateDepotMmolL = 0f;
        }
    }
}
