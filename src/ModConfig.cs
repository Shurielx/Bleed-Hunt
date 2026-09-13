using System;

namespace BleedAndHunt
{
    public class ModConfig
    {
        // Core toggles

        /// <summary>Master toggle for weapon bleeding damage over time on hit.</summary>
        public bool EnableBleeding { get; set; } = true;

        /// <summary>Wounded prey limps and slows down when injured, taking 1.25x execute bonus damage below 50% HP.</summary>
        public bool EnableLimping { get; set; } = true;

        /// <summary>Hunter's scent vision: highlights wounded prey through foliage and terrain with a red aura.</summary>
        public bool EnableXRay { get; set; } = true;

        // Falx weapon boost

        /// <summary>Grants falx blades an extended forward reach.</summary>
        public bool EnableFalxRangeBoost { get; set; } = true;

        /// <summary>Forward reach in blocks for falx weapons (default: 3.0 blocks).</summary>
        public float FalxAttackRange { get; set; } = 3.0f;

        /// <summary>Falx blades inflict bonus bleed damage (+50% DPS) on strike.</summary>
        public bool EnableFalxBleedBonus { get; set; } = true;

        /// <summary>Bleed damage multiplier for falx weapons (default: 1.50 = +50% bleed DPS).</summary>
        public float FalxBleedMultiplier { get; set; } = 1.50f;

        // Stacking limit for bleed effect

        /// <summary>Maximum number of bleed stacks an entity can accumulate (default: 5).</summary>
        public int MaxBleedStacks { get; set; } = 5;

        // Bleeding damage per second based on weapon material tier

        /// <summary>Bleed DPS for Tier 0 weapons (Flint, Stone, Bone).</summary>
        public float Tier0DamagePerSecond { get; set; } = 0.25f;

        /// <summary>Bleed DPS for Tier 1 weapons (Copper, Scrap Iron).</summary>
        public float Tier1DamagePerSecond { get; set; } = 0.45f;

        /// <summary>Bleed DPS for Tier 2 weapons (Bronze varieties).</summary>
        public float Tier2DamagePerSecond { get; set; } = 0.65f;

        /// <summary>Bleed DPS for Tier 3 weapons (Iron, Meteoric Iron).</summary>
        public float Tier3DamagePerSecond { get; set; } = 0.80f;

        /// <summary>Bleed DPS for Tier 4 weapons (Steel).</summary>
        public float Tier4DamagePerSecond { get; set; } = 1.00f;

        /// <summary>Bleed DPS for Tier 5 weapons (Exotic and High-tier).</summary>
        public float Tier5DamagePerSecond { get; set; } = 1.25f;

        // Bleed duration per strike based on weapon material tier

        /// <summary>Bleed duration in seconds for Tier 0 weapons.</summary>
        public float Tier0DurationSeconds { get; set; } = 2.0f;

        /// <summary>Bleed duration in seconds for Tier 1 weapons.</summary>
        public float Tier1DurationSeconds { get; set; } = 3.0f;

        /// <summary>Bleed duration in seconds for Tier 2 weapons.</summary>
        public float Tier2DurationSeconds { get; set; } = 4.0f;

        /// <summary>Bleed duration in seconds for Tier 3 weapons.</summary>
        public float Tier3DurationSeconds { get; set; } = 4.0f;

        /// <summary>Bleed duration in seconds for Tier 4 weapons.</summary>
        public float Tier4DurationSeconds { get; set; } = 5.0f;

        /// <summary>Bleed duration in seconds for Tier 5 weapons.</summary>
        public float Tier5DurationSeconds { get; set; } = 6.0f;

        // Blood trail particles on ground

        /// <summary>Spawns visible glowing red blood droplet particles on the ground behind bleeding prey.</summary>
        public bool EnableBloodParticles { get; set; } = true;

        // X-Ray / Scent tracking

        /// <summary>How long hunter's scent highlight lasts in seconds from the last hit (default: 30s).</summary>
        public float XRayDurationSeconds { get; set; } = 30.0f;

        /// <summary>Maximum tracking distance in blocks for hunter's scent outline (default: 64 blocks).</summary>
        public float XRayMaxDistance { get; set; } = 64.0f;

        // Corpse / Downed prey highlight

        /// <summary>Emits a golden diamond beacon above downed prey so you never lose kills in tall grass.</summary>
        public bool EnableCorpseHighlight { get; set; } = true;

        /// <summary>Duration in seconds of the golden kill beacon after animal death (default: 10s).</summary>
        public float CorpseHighlightDurationSeconds { get; set; } = 10.0f;

        // Melee sweep assist

        /// <summary>Enables a lateral melee attack sweep cone so you don't miss agile moving targets.</summary>
        public bool EnableBetterRange { get; set; } = true;

        /// <summary>Half-angle of the melee attack cone assist in degrees (default: 22°).</summary>
        public float BetterRangeSweepAngle { get; set; } = 22.0f;

        // Filter and presets

        /// <summary>Which entities are tracked by hunter's scent: 'animals', 'monsters', or 'all'.</summary>
        public string XRayTargetFilter { get; set; } = "animals";

        /// <summary>Global bleed damage preset: 'default', 'subtle', or 'bloodlust'.</summary>
        public string BleedingPreset { get; set; } = "default";

        // Limping thresholds

        /// <summary>Health ratio threshold for initial limping (default: 0.75 = 75% HP).</summary>
        public float LimpThresholdHigh { get; set; } = 0.75f;

        /// <summary>Movement speed multiplier at initial limp threshold (default: 0.75 = 75% speed).</summary>
        public float LimpSpeedHigh { get; set; } = 0.75f;

        /// <summary>Health ratio threshold for severe limping and execute damage (default: 0.50 = 50% HP).</summary>
        public float LimpThresholdLow { get; set; } = 0.50f;

        /// <summary>Movement speed multiplier at severe limp threshold (default: 0.50 = 50% speed).</summary>
        public float LimpSpeedLow { get; set; } = 0.50f;

        // Scent aura color (RGBA)

        /// <summary>Red channel of hunter's scent aura (0.0 to 1.0).</summary>
        public float XRayColorR { get; set; } = 1.0f;

        /// <summary>Green channel of hunter's scent aura (0.0 to 1.0).</summary>
        public float XRayColorG { get; set; } = 0.08f;

        /// <summary>Blue channel of hunter's scent aura (0.0 to 1.0).</summary>
        public float XRayColorB { get; set; } = 0.12f;

        /// <summary>Opacity / alpha channel of hunter's scent aura (default: 0.20 = 20%).</summary>
        public float XRayColorA { get; set; } = 0.20f;

        // Downed animal color (RGBA)

        /// <summary>Red channel of golden corpse beacon (0.0 to 1.0).</summary>
        public float CorpseColorR { get; set; } = 1.0f;

        /// <summary>Green channel of golden corpse beacon (0.0 to 1.0).</summary>
        public float CorpseColorG { get; set; } = 0.75f;

        /// <summary>Blue channel of golden corpse beacon (0.0 to 1.0).</summary>
        public float CorpseColorB { get; set; } = 0.1f;

        /// <summary>Opacity / alpha channel of golden corpse beacon (default: 0.85).</summary>
        public float CorpseColorA { get; set; } = 0.85f;

        /// <summary>Entity code substrings considered bosses, immune to limping slowdown.</summary>
        public string[] BossCodes { get; set; } = new string[] { "erel", "eidolon" };

        // Realistic Stealth & Sensory System (v1.1.0)

        /// <summary>Enables realistic snout-based stealth FOV cone and raycast line-of-sight detection.</summary>
        public bool EnableStealthMechanics { get; set; } = true;

        /// <summary>Field of view cone in front of the animal's snout in degrees (default: 130° = 65° half-angle on each side).</summary>
        public float StealthFovDegrees { get; set; } = 130.0f;

        /// <summary>Proximity sound/smell hearing radius when the player is sneaking on Shift (default: 2.5 blocks).</summary>
        public float SneakHearingRadius { get; set; } = 2.5f;

        /// <summary>Proximity hearing radius when the player is walking upright (default: 6.5 blocks).</summary>
        public float WalkHearingRadius { get; set; } = 6.5f;

        /// <summary>Proximity hearing radius when the player is sprinting or jumping (default: 14.0 blocks).</summary>
        public float SprintHearingRadius { get; set; } = 14.0f;

        /// <summary>Visual detection distance multiplier when sneaking inside the forward FOV cone (default: 0.55 = 55% of base range).</summary>
        public float SneakSightRangeMultiplier { get; set; } = 0.55f;

        /// <summary>Enables raycasting to check if solid terrain, walls, or tree trunks block line of sight to the player.</summary>
        public bool EnableLineOfSightRaycast { get; set; } = true;
    }
}
