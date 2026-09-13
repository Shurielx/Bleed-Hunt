using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace BleedAndHunt
{
    public class WeaponSweepSystem
    {
        private readonly ICoreClientAPI capi;
        private long lastSweepAttackMs = 0;

        public WeaponSweepSystem(ICoreClientAPI capi)
        {
            this.capi = capi;
            capi.Input.InWorldAction += OnInWorldAction;
        }

        private void OnInWorldAction(EnumEntityAction action, bool on, ref EnumHandling handled)
        {
            if (!on) return;
            if (action != EnumEntityAction.LeftMouseDown && action != EnumEntityAction.InWorldLeftMouseDown) return;

            var config = BleedAndHuntModSystem.Config;
            if (config == null || !config.EnableBetterRange) return;

            IClientPlayer? player = capi.World.Player;
            if (player?.Entity == null) return;

            // If player's crosshair already targeted an entity, normal hit detection handles it
            if (player.CurrentEntitySelection != null) return;

            // Check if player is holding a melee weapon (knife, falx, sword, spear)
            ItemSlot? activeSlot = player.Entity.RightHandItemSlot;
            if (activeSlot?.Itemstack?.Collectible == null) return;

            string code = activeSlot.Itemstack.Collectible.Code?.Path?.ToLowerInvariant() ?? "";
            bool isMeleeWeapon = code.Contains("knife") || code.Contains("falx") ||
                                 code.Contains("sword") || code.Contains("blade") ||
                                 code.Contains("spear") || code.Contains("club") ||
                                 code.Contains("axe");

            if (!isMeleeWeapon) return;

            // Cooldown throttle to match weapon swing timing (min 280ms)
            long now = capi.World.ElapsedMilliseconds;
            if (now - lastSweepAttackMs < 280) return;

            // Determine weapon attack reach
            float attackReach = activeSlot.Itemstack.Collectible.GetAttackRange(activeSlot.Itemstack);
            if (attackReach <= 0.1f)
            {
                attackReach = code.Contains("spear") ? 3.5f : (code.Contains("falx") ? 3.0f : 2.0f);
            }

            // Find best target within lateral sweep cone
            Entity? target = FindSweepTarget(player.Entity, attackReach, config.BetterRangeSweepAngle);
            if (target != null && target.SelectionBox != null)
            {
                lastSweepAttackMs = now;
                var box = target.SelectionBox;
                double boxMidY = (box.Y1 + box.Y2) * 0.5;
                Vec3d eyePos = player.Entity.Pos.XYZ.AddCopy(player.Entity.LocalEyePos);
                Vec3d targetCenter = target.Pos.XYZ.AddCopy(0, boxMidY, 0);

                var selection = new EntitySelection
                {
                    Entity = target,
                    Position = target.Pos.XYZ,
                    HitPosition = new Vec3d(0, boxMidY, 0),
                    Face = BlockFacing.FromVector(eyePos.SubCopy(targetCenter)) ?? BlockFacing.UP,
                    SelectionBoxIndex = 0
                };

                try
                {
                    capi.World.TryAttackEntity(selection);
                    player.Entity.AnimManager?.StartAnimation("attack");
                    handled = EnumHandling.PreventDefault;
                }
                catch (Exception ex)
                {
                    capi.World.Logger.Warning("[BleedAndHunt] Error during weapon sweep attack: {0}", ex);
                }
            }
        }

        private Entity? FindSweepTarget(EntityPlayer player, float maxReach, float maxAngleDegrees)
        {
            try
            {
                Vec3d eyePos = player.Pos.XYZ.AddCopy(player.LocalEyePos);
                Vec3f viewVec = player.Pos.GetViewVector();
                Vec3d lookDir = new Vec3d(viewVec.X, viewVec.Y, viewVec.Z).Normalize();

                double maxAngleRad = maxAngleDegrees * (Math.PI / 180.0);
                double minDot = Math.Cos(maxAngleRad);

                Entity? bestTarget = null;
                double bestDistanceSq = double.MaxValue;
                double maxReachSq = maxReach * maxReach;

                foreach (var entity in capi.World.LoadedEntities.Values)
                {
                    if (entity == null || !entity.Alive) continue;
                    if (entity.EntityId == player.EntityId) continue;
                    if (entity is EntityPlayer) continue; // Never sweep-attack other players in co-op/multiplayer

                    // Only target animals or hostile agents (exclude inanimate objects)
                    if (!EntityHelper.IsAnimal(entity) && entity is not EntityAgent) continue;

                    // Center of target bounding box
                    var box = entity.SelectionBox;
                    if (box == null) continue;

                    double boxMidY = (box.Y1 + box.Y2) * 0.5;
                    Vec3d targetCenter = entity.Pos.XYZ.AddCopy(0, boxMidY, 0);

                    double distSq = eyePos.SquareDistanceTo(targetCenter);
                    if (distSq > maxReachSq || distSq < 0.0001) continue;

                    Vec3d toTarget = targetCenter.SubCopy(eyePos).Normalize();
                    double dot = lookDir.Dot(toTarget);

                    // Check if target is inside the sweep angle cone
                    if (dot >= minDot)
                    {
                        // Check line of sight: ensure no solid block obstructs the swing
                        BlockSelection? blockSel = null;
                        EntitySelection? entSel = null;
                        capi.World.RayTraceForSelection(eyePos, targetCenter, ref blockSel, ref entSel,
                            (pos, block) => block.CollisionBoxes != null && block.CollisionBoxes.Length > 0,
                            e => false
                        );

                        if (blockSel == null) // Clear line of sight
                        {
                            if (distSq < bestDistanceSq)
                            {
                                bestDistanceSq = distSq;
                                bestTarget = entity;
                            }
                        }
                    }
                }

                return bestTarget;
            }
            catch
            {
                return null; // Safe fallback on any unexpected entity collection mutation
            }
        }

        public void Dispose()
        {
            capi.Input.InWorldAction -= OnInWorldAction;
        }
    }
}
