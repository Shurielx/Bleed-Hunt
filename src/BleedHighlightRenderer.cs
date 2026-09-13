using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace BleedAndHunt
{
    public class BleedHighlightRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private MeshRef? boxMeshRef;
        private LoadedTexture? whiteTexture;
        private readonly float[] modelMatrix = Mat4f.Create();

        // Cached shader light vectors to avoid per-frame GC allocations
        private static readonly Vec4f FullLight = new Vec4f(1.0f, 1.0f, 1.0f, 1.0f);
        private static readonly Vec3f FullAmbient = new Vec3f(1.0f, 1.0f, 1.0f);
        private readonly Vec4f livingTint = new Vec4f();
        private readonly Vec4f corpseTint = new Vec4f();

        public double RenderOrder => 0.99;
        public int RenderRange => 150;

        public BleedHighlightRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;
            InitMeshes();
        }

        private void InitMeshes()
        {
            // Solid cube mesh (-1 to 1) with white vertex colors and full glow
            MeshData cubeMesh = CubeMeshUtil.GetCube();
            if (cubeMesh.Rgba == null || cubeMesh.Rgba.Length != cubeMesh.VerticesCount * 4)
            {
                cubeMesh.Rgba = new byte[cubeMesh.VerticesCount * 4];
            }
            Array.Fill(cubeMesh.Rgba, (byte)255);

            if (cubeMesh.Flags == null || cubeMesh.Flags.Length != cubeMesh.VerticesCount)
            {
                cubeMesh.Flags = new int[cubeMesh.VerticesCount];
            }
            // Set full glow in vertex flags so standard shader never treats it as dark
            for (int i = 0; i < cubeMesh.Flags.Length; i++)
            {
                cubeMesh.Flags[i] = 255;
            }

            boxMeshRef = capi.Render.UploadMesh(cubeMesh);

            // 1x1 pure white RGBA texture (0xFFFFFFFF)
            whiteTexture = new LoadedTexture(capi);
            capi.Render.LoadOrUpdateTextureFromRgba(new int[] { unchecked((int)0xFFFFFFFF) }, false, 0, ref whiteTexture);
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            var config = BleedAndHuntModSystem.Config;
            if (config == null || !config.EnableXRay) return;
            if (boxMeshRef == null) return;

            IClientPlayer? player = capi.World.Player;
            if (player?.Entity == null) return;

            // Ensure white texture is valid
            if (whiteTexture == null || whiteTexture.TextureId <= 0)
            {
                whiteTexture = new LoadedTexture(capi);
                capi.Render.LoadOrUpdateTextureFromRgba(new int[] { unchecked((int)0xFFFFFFFF) }, false, 0, ref whiteTexture);
            }

            string myUid = player.PlayerUID;
            Vec3d camPos = player.Entity.CameraPos;
            float maxDist = config.XRayMaxDistance;
            float maxDistSq = maxDist * maxDist;

            try
            {
                // Disable depth test to see through terrain and foliage
                capi.Render.GlToggleBlend(true);
                capi.Render.GLDisableDepthTest();
                capi.Render.GLDepthMask(false);

                // Iterate loaded entities safely
                var entities = capi.World.LoadedEntities.Values;
                int renderedCount = 0;

                foreach (var entity in entities)
                {
                    if (renderedCount >= 16) break; // Hard cap on simultaneously highlighted targets
                    if (entity?.Pos == null) continue;
                    if (entity.EntityId == player.Entity.EntityId) continue;

                    try
                    {
                        // 1. Distance culling
                        float distSq = (float)entity.Pos.SquareDistanceTo(player.Entity.Pos);
                        if (distSq > maxDistSq) continue;

                        // 2. Only highlight targets wounded by THIS player
                        string hunterUid = entity.WatchedAttributes?.GetString("bleedHunterUid", "") ?? "";
                        if (hunterUid != myUid) continue;

                        // 3. Check target category filter (animals, monsters, all)
                        bool isAnimal = entity.WatchedAttributes?.GetBool("bleedIsAnimal", false) ?? false;
                        if (!isAnimal)
                        {
                            isAnimal = EntityHelper.IsAnimal(entity);
                        }

                        string filter = config.XRayTargetFilter?.ToLowerInvariant() ?? "animals";
                        if (filter == "animals" && !isAnimal) continue;
                        if (filter == "monsters" && isAnimal) continue;

                        bool isDead = !entity.Alive || (entity.WatchedAttributes?.GetBool("bleedIsDead", false) ?? false);
                        float secondsLeft = entity.WatchedAttributes?.GetFloat("bleedSecondsLeft", 0f) ?? 0f;
                        float deadSecondsLeft = entity.WatchedAttributes?.GetFloat("bleedDeadSecondsLeft", 0f) ?? 0f;

                        // Living target: active for 30s from last strike
                        if (!isDead && secondsLeft > 0f)
                        {
                            RenderLivingHighlight(entity, camPos, distSq, config);
                            renderedCount++;
                        }
                        // Downed corpse: upper beacon active for 10s after death to find kill
                        else if (isDead && deadSecondsLeft > 0f)
                        {
                            RenderCorpseBeacon(entity, camPos, config);
                            renderedCount++;
                        }
                    }
                    catch
                    {
                        // Per-entity safe fallback: error on one entity never breaks other highlights
                    }
                }
            }
            catch
            {
                // General safe fallback
            }
            finally
            {
                capi.Render.GLEnableDepthTest();
                capi.Render.GLDepthMask(true);
            }
        }

        private void RenderLivingHighlight(Entity entity, Vec3d camPos, float distSq, ModConfig config)
        {
            var box = entity.CollisionBox ?? entity.SelectionBox;
            float width = box != null ? (box.X2 - box.X1) : 0.8f;
            float height = box != null ? (box.Y2 - box.Y1) : 0.8f;
            float length = box != null ? (box.Z2 - box.Z1) : 0.8f;

            if (width <= 0.1f) width = 0.8f;
            if (height <= 0.1f) height = 0.8f;
            if (length <= 0.1f) length = 0.8f;

            // Center of the animal body relative to camera
            float centerY = box != null ? (box.Y1 + box.Y2) * 0.5f : (height / 2f);
            float relX = (float)(entity.Pos.X - camPos.X);
            float relY = (float)(entity.Pos.Y - camPos.Y + centerY);
            float relZ = (float)(entity.Pos.Z - camPos.Z);

            // Scale to envelope the entity
            float scaleX = (width / 2f) * 1.08f;
            float scaleY = (height / 2f) * 1.08f;
            float scaleZ = (length / 2f) * 1.08f;

            // Distance-based opacity fade:
            // 0 - 48m: full base opacity (default 20% = 0.20f)
            // 48m - 64m: smoothly fades from 20% down to 5% (0.05f)
            float dist = (float)Math.Sqrt(distSq);
            float alpha = config.XRayColorA;
            if (dist > 48f)
            {
                float t = Math.Clamp((dist - 48f) / (config.XRayMaxDistance - 48f), 0f, 1f);
                alpha = (1f - t) * config.XRayColorA + t * 0.05f;
            }

            var prog = capi.Render.PreparedStandardShader((int)entity.Pos.X, (int)entity.Pos.Y + 1, (int)entity.Pos.Z);
            if (prog == null) return;

            // Force emissive glow so the box is NEVER darkened by underground / night lighting
            prog.RgbaLightIn = FullLight;
            prog.RgbaAmbientIn = FullAmbient;
            prog.ExtraGlow = 255;
            prog.NormalShaded = 0;
            prog.ProjectionMatrix = capi.Render.CurrentProjectionMatrix;
            prog.ViewMatrix = capi.Render.CameraMatrixOriginf;

            if (whiteTexture != null)
            {
                capi.Render.BindTexture2d(whiteTexture.TextureId);
            }

            // Single clean bounding box on the living animal
            Mat4f.Identity(modelMatrix);
            Mat4f.Translate(modelMatrix, modelMatrix, relX, relY, relZ);
            Mat4f.Scale(modelMatrix, modelMatrix, scaleX, scaleY, scaleZ);
            prog.ModelMatrix = modelMatrix;

            livingTint.X = config.XRayColorR;
            livingTint.Y = config.XRayColorG;
            livingTint.Z = config.XRayColorB;
            livingTint.W = alpha;
            prog.RgbaTint = livingTint;

            if (boxMeshRef != null)
            {
                capi.Render.RenderMesh(boxMeshRef);
            }

            prog.Stop();
        }

        private void RenderCorpseBeacon(Entity entity, Vec3d camPos, ModConfig config)
        {
            var box = entity.CollisionBox ?? entity.SelectionBox;
            float height = box != null ? box.Y2 : 0.6f;
            if (height <= 0.1f) height = 0.6f;

            float relX = (float)(entity.Pos.X - camPos.X);
            float relZ = (float)(entity.Pos.Z - camPos.Z);
            float markerRelY = (float)(entity.Pos.Y - camPos.Y + height + 0.65f);

            var prog = capi.Render.PreparedStandardShader((int)entity.Pos.X, (int)entity.Pos.Y + 1, (int)entity.Pos.Z);
            if (prog == null) return;

            prog.RgbaLightIn = FullLight;
            prog.RgbaAmbientIn = FullAmbient;
            prog.ExtraGlow = 255;
            prog.NormalShaded = 0;
            prog.ProjectionMatrix = capi.Render.CurrentProjectionMatrix;
            prog.ViewMatrix = capi.Render.CameraMatrixOriginf;

            if (whiteTexture != null)
            {
                capi.Render.BindTexture2d(whiteTexture.TextureId);
            }

            // Rotating diamond beacon floating above corpse for 10s so player can easily locate kill
            float markerScale = 0.22f;
            Mat4f.Identity(modelMatrix);
            Mat4f.Translate(modelMatrix, modelMatrix, relX, markerRelY, relZ);
            Mat4f.RotateY(modelMatrix, modelMatrix, (float)(capi.World.ElapsedMilliseconds / 300.0));
            Mat4f.Scale(modelMatrix, modelMatrix, markerScale, markerScale * 1.5f, markerScale);
            prog.ModelMatrix = modelMatrix;

            // Warm amber gold glow
            corpseTint.X = config.CorpseColorR;
            corpseTint.Y = config.CorpseColorG;
            corpseTint.Z = config.CorpseColorB;
            corpseTint.W = config.CorpseColorA;
            prog.RgbaTint = corpseTint;

            if (boxMeshRef != null)
            {
                capi.Render.RenderMesh(boxMeshRef);
            }

            prog.Stop();
        }

        public void Dispose()
        {
            if (boxMeshRef != null)
            {
                capi.Render.DeleteMesh(boxMeshRef);
                boxMeshRef = null;
            }

            whiteTexture?.Dispose();
            whiteTexture = null;
        }
    }
}
