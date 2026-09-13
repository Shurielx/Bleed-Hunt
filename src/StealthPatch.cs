using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace BleedAndHunt
{
    /// <summary>
    /// Harmony patches providing realistic snout-based stealth, field of view (FOV),
    /// multi-tiered hearing/smell proximity fallbacks, and anti-exploit raycast line of sight.
    /// </summary>
    public static class StealthPatch
    {
        private static bool isPatched = false;

        public static void ApplyPatches(Harmony harmony, ILogger? logger)
        {
            if (isPatched) return;

            try
            {
                // 1. Patch AiTaskBaseTargetable.CanSensePlayer (VSEssentials)
                MethodInfo? canSenseMethod = typeof(AiTaskBaseTargetable).GetMethod(
                    "CanSensePlayer",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(EntityPlayer), typeof(double) },
                    null
                );

                if (canSenseMethod != null)
                {
                    MethodInfo prefix = typeof(StealthPatch).GetMethod(
                        nameof(CanSensePlayerPrefix),
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                    )!;

                    harmony.Patch(canSenseMethod, prefix: new HarmonyMethod(prefix));
                    logger?.Notification("[Bleed & Hunt] Successfully patched AiTaskBaseTargetable.CanSensePlayer for stealth mechanics.");
                }
                else
                {
                    logger?.Warning("[Bleed & Hunt] Could not find AiTaskBaseTargetable.CanSensePlayer to patch.");
                }

                // 2. Patch AiTaskBaseTargetableR.CheckDetectionRange if available (modern tasks)
                Type? taskRType = typeof(AiTaskBaseTargetable).Assembly.GetType("Vintagestory.GameContent.AiTaskBaseTargetableR");
                if (taskRType != null)
                {
                    MethodInfo? checkDetectionMethod = taskRType.GetMethod(
                        "CheckDetectionRange",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        new Type[] { typeof(Entity), typeof(double) },
                        null
                    );

                    if (checkDetectionMethod != null)
                    {
                        MethodInfo prefixR = typeof(StealthPatch).GetMethod(
                            nameof(CheckDetectionRangePrefix),
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                        )!;

                        harmony.Patch(checkDetectionMethod, prefix: new HarmonyMethod(prefixR));
                        logger?.Notification("[Bleed & Hunt] Successfully patched AiTaskBaseTargetableR.CheckDetectionRange for stealth mechanics.");
                    }
                }

                isPatched = true;
            }
            catch (Exception ex)
            {
                logger?.Error("[Bleed & Hunt] Failed to apply stealth Harmony patches: " + ex.ToString());
            }
        }

        /// <summary>
        /// Prefix for classic AiTaskBaseTargetable.CanSensePlayer.
        /// </summary>
        private static bool CanSensePlayerPrefix(AiTaskBaseTargetable __instance, EntityPlayer eplr, double range, ref bool __result, EntityAgent? ___entity)
        {
            EntityAgent mob = ___entity ?? __instance.entity;
            if (CheckStealthDetection(mob, eplr, range, out bool detected))
            {
                __result = detected;
                return false; // Override vanilla
            }

            return true; // Let vanilla execute
        }

        /// <summary>
        /// Prefix for modern AiTaskBaseTargetableR.CheckDetectionRange.
        /// </summary>
        private static bool CheckDetectionRangePrefix(object __instance, Entity target, double range, ref bool __result, EntityAgent? ___entity)
        {
            if (target is EntityPlayer eplr && ___entity != null)
            {
                if (CheckStealthDetection(___entity, eplr, range, out bool detected))
                {
                    __result = detected;
                    return false; // Override vanilla
                }
            }

            return true; // Let vanilla execute
        }

        /// <summary>
        /// Evaluates whether a player is detected by a mob using realistic sensory rules:
        /// 1. Proximity sound/smell fallback (crouch vs walk vs sprint).
        /// 2. Snout position and forward FOV dot product.
        /// 3. Effective visual distance inside the cone.
        /// 4. Anti-exploit solid block raycast (ignores tall grass and foliage).
        /// Returns true if custom stealth logic handled the decision (result in detected), or false to defer to vanilla.
        /// </summary>
        public static bool CheckStealthDetection(EntityAgent? mob, EntityPlayer? player, double range, out bool detected)
        {
            detected = false;
            ModConfig config = BleedAndHuntModSystem.Config;
            if (config == null || !config.EnableStealthMechanics)
            {
                return false; // Defer to vanilla
            }

            if (mob == null || player == null || !mob.Alive || !player.Alive)
            {
                return false; // Defer to vanilla
            }

            // Creative / Spectator mode: ignore
            if (player.Player != null && (player.Player.WorldData.CurrentGameMode == EnumGameMode.Creative || player.Player.WorldData.CurrentGameMode == EnumGameMode.Spectator))
            {
                detected = false;
                return true;
            }

            // Dimension check
            if (mob.Pos.Dimension != player.Pos.Dimension)
            {
                detected = false;
                return true;
            }

            // Boss check: Do not alter detection for boss entities
            string mobPath = mob.Code?.Path?.ToLowerInvariant() ?? "";
            if (mob.HasBehavior<EntityBehaviorBoss>() || mob.HasBehavior("boss") || mobPath.Contains("eidolon") || mobPath.Contains("erel"))
            {
                return false; // Defer to vanilla
            }
            if (config.BossCodes != null)
            {
                for (int i = 0; i < config.BossCodes.Length; i++)
                {
                    if (mobPath.Contains(config.BossCodes[i]))
                    {
                        return false;
                    }
                }
            }

            // Alerted / Combat check: If animal was recently damaged or is currently fleeing/attacking, it cannot be snuck up on
            EntityBehaviorEmotionStates? emo = mob.GetBehavior<EntityBehaviorEmotionStates>();
            if (emo != null)
            {
                if (emo.IsInEmotionState("aggressiveondamage") || emo.IsInEmotionState("fleeondamage"))
                {
                    return false; // Defer to vanilla combat reaction
                }
            }

            // Distance calculation
            double dx = player.Pos.X - mob.Pos.X;
            double dy = player.Pos.Y - mob.Pos.Y;
            double dz = player.Pos.Z - mob.Pos.Z;
            double distSq = dx * dx + dy * dy + dz * dz;

            // Fail-fast ceiling: beyond base maximum seek range
            if (distSq > range * range)
            {
                detected = false;
                return true;
            }

            // --- 1. PROXIMITY SOUND / SMELL FALLBACK ---
            // If the player is within breathing/hearing distance, the animal detects them regardless of facing direction.
            bool isSneaking = player.Controls.Sneak && player.OnGround;
            bool isSprintingOrJumping = player.Controls.Sprint || !player.OnGround;

            float hearingRadius = isSneaking 
                ? config.SneakHearingRadius 
                : (isSprintingOrJumping ? config.SprintHearingRadius : config.WalkHearingRadius);

            if (distSq <= hearingRadius * hearingRadius)
            {
                // Mob hears footsteps or smells player
                detected = true;
                return true;
            }

            // --- 2. SNOUT POSITION & FORWARD VECTOR (FOV) ---
            // Eye/head height from entity properties:
            double eyeHeight = mob.Properties?.EyeHeight ?? (double)(mob.SelectionBox.Y2 * 0.75f);
            double forwardOffset = Math.Max(0.2, (double)(mob.SelectionBox?.XSize ?? 0.8f) * 0.40);

            // Snout coordinate projected forward along mob.Pos.Yaw:
            Vec3d snoutPos = new Vec3d(mob.Pos.X, mob.Pos.Y + eyeHeight, mob.Pos.Z).AheadCopy(forwardOffset, 0f, mob.Pos.Yaw);

            // Target coordinate at player chest/torso:
            double playerEyeHeight = player.Properties?.EyeHeight ?? 1.7;
            double playerTargetY = player.Pos.Y + (playerEyeHeight * 0.65);
            Vec3d targetPos = new Vec3d(player.Pos.X, playerTargetY, player.Pos.Z);

            // Horizontal direction from snout to target:
            double toTargetX = targetPos.X - snoutPos.X;
            double toTargetZ = targetPos.Z - snoutPos.Z;
            double horizDist = Math.Sqrt(toTargetX * toTargetX + toTargetZ * toTargetZ);

            if (horizDist < 0.001)
            {
                detected = true;
                return true;
            }

            double dirX = toTargetX / horizDist;
            double dirZ = toTargetZ / horizDist;

            // In Vintage Story, forward look vector from Yaw is: lookX = -sin(Yaw), lookZ = -cos(Yaw)
            double lookX = -Math.Sin(mob.Pos.Yaw);
            double lookZ = -Math.Cos(mob.Pos.Yaw);

            // Dot product = cos(angle between snout direction and target)
            double dot = lookX * dirX + lookZ * dirZ;

            // FOV cone threshold:
            double halfAngleRad = (config.StealthFovDegrees * 0.5) * (Math.PI / 180.0);
            double minDot = Math.Cos(halfAngleRad);

            // If outside forward FOV (sides or behind back):
            if (dot < minDot)
            {
                // Strictly behind the animal's snout line (dot < 0.0):
                if (dot < 0.0)
                {
                    detected = false;
                    return true;
                }

                // Peripheral flank zone (between minDot and 0.0):
                // Sneaking avoids peripheral detection entirely
                if (isSneaking)
                {
                    detected = false;
                    return true;
                }

                // If walking upright, peripheral vision range is sharply reduced to 35%
                if (horizDist > range * 0.35)
                {
                    detected = false;
                    return true;
                }
            }

            // --- 3. MAXIMUM SIGHT DISTANCE IN FRONT CONE ---
            double effectiveVisualRange = isSneaking ? (range * config.SneakSightRangeMultiplier) : range;
            if (horizDist > effectiveVisualRange)
            {
                detected = false;
                return true;
            }

            // --- 4. RAYCAST LINE OF SIGHT (ANTI-EXPLOIT) ---
            if (config.EnableLineOfSightRaycast && mob.World != null)
            {
                BlockSelection? blockSel = null;
                EntitySelection? entitySel = null;

                BlockFilter bfilter = (BlockPos pos, Block block) =>
                {
                    if (block == null) return false;
                    // Non-solid matter (air, liquids) does not block sight
                    if (block.MatterState != EnumMatterState.Solid) return false;
                    // Tall grass, flowers, mushrooms, saplings have no collision boxes and do not block sight
                    if (block.CollisionBoxes == null || block.CollisionBoxes.Length == 0) return false;
                    // Require solid surface (walls, rocks, cliffs, full wood logs)
                    return block.SideSolid.Any;
                };

                mob.World.RayTraceForSelection(snoutPos, targetPos, ref blockSel, ref entitySel, bfilter, null);

                if (blockSel != null)
                {
                    // Sight line is obstructed by solid terrain / cliff / wall
                    detected = false;
                    return true;
                }
            }

            // Clear visual detection inside the front FOV cone!
            detected = true;
            return true;
        }
    }
}
