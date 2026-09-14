using System;
using System.Collections.Generic;
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
        private MeshRef? coneMeshRef;
        private LoadedTexture? whiteTexture;
        private readonly float[] modelMatrix = Mat4f.Create();

        // Cached shader light vectors to avoid per-frame GC allocations
        private static readonly Vec4f FullLight = new Vec4f(1.0f, 1.0f, 1.0f, 1.0f);
        private static readonly Vec3f FullAmbient = new Vec3f(1.0f, 1.0f, 1.0f);
        private static readonly Vec4f ZeroGlow = new Vec4f(0.0f, 0.0f, 0.0f, 0.0f);
        private readonly Vec4f livingTint = new Vec4f();
        private readonly Vec4f corpseTint = new Vec4f();

        // High-performance target buffers (updated at low frequency 5 Hz instead of 60-144 Hz)
        private readonly List<Entity> livingTargets = new List<Entity>(16);
        private readonly List<Entity> corpseTargets = new List<Entity>(16);
        private float scanAccumulator = 0.20f; // Scan immediately on start

        public double RenderOrder => 0.99;
        public int RenderRange => 150;

        public BleedHighlightRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;
            InitMeshes();
        }

        private void EnsureWhiteTexture()
        {
            if (whiteTexture != null && whiteTexture.TextureId > 0) return;

            whiteTexture = new LoadedTexture(capi, 0, 1, 1);
            capi.Render.LoadOrUpdateTextureFromRgba(new int[] { unchecked((int)0xFFFFFFFF) }, false, 0, ref whiteTexture);
        }

        private void InitMeshes()
        {
            // 1. Solid cube mesh (-1 to 1) with white vertex colors and 0 glow flags
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
            Array.Fill(cubeMesh.Flags, 0);

            boxMeshRef = capi.Render.UploadMesh(cubeMesh);

            // 2. Inverted 3D cone mesh for corpse beacon (tip pointing down at carcass)
            MeshData coneMesh = CreateConeMesh();
            coneMeshRef = capi.Render.UploadMesh(coneMesh);

            // 3. 1x1 pure white RGBA texture (0xFFFFFFFF)
            EnsureWhiteTexture();
        }

        private MeshData CreateConeMesh(int segments = 16, float radius = 0.32f, float bodyHeight = 0.85f, float peakHeight = 0.95f)
        {
            int totalVerts = 1 + segments + 1;
            int totalIndices = segments * 6 * 2; // Double-sided for both body and top cap

            MeshData mesh = new MeshData(totalVerts, totalIndices, false, true, true, true);

            // Vertex 0: Bottom tip pointing down
            mesh.AddVertexWithFlags(0f, 0f, 0f, 0.5f, 0.5f, -1, 0);

            // Vertices 1..segments: Ring at bodyHeight
            for (int i = 0; i < segments; i++)
            {
                double angle = i * 2.0 * Math.PI / segments;
                float x = (float)(Math.Cos(angle) * radius);
                float z = (float)(Math.Sin(angle) * radius);
                mesh.AddVertexWithFlags(x, bodyHeight, z, 0.5f, 0.5f, -1, 0);
            }

            // Vertex segments + 1: Top cap peak
            int topPeakIdx = segments + 1;
            mesh.AddVertexWithFlags(0f, peakHeight, 0f, 0.5f, 0.5f, -1, 0);

            // Cone body faces (tip to ring)
            for (int i = 0; i < segments; i++)
            {
                int v1 = 1 + i;
                int v2 = 1 + ((i + 1) % segments);

                // Front face
                mesh.AddIndex(0);
                mesh.AddIndex(v1);
                mesh.AddIndex(v2);

                // Back face (double-sided)
                mesh.AddIndex(0);
                mesh.AddIndex(v2);
                mesh.AddIndex(v1);
            }

            // Top cap faces (ring to top peak)
            for (int i = 0; i < segments; i++)
            {
                int v1 = 1 + i;
                int v2 = 1 + ((i + 1) % segments);

                // Front face
                mesh.AddIndex(topPeakIdx);
                mesh.AddIndex(v2);
                mesh.AddIndex(v1);

                // Back face (double-sided)
                mesh.AddIndex(topPeakIdx);
                mesh.AddIndex(v1);
                mesh.AddIndex(v2);
            }

            return mesh;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            var config = BleedAndHuntModSystem.Config;
            if (config == null || !config.EnableXRay) return;
            if (boxMeshRef == null || coneMeshRef == null) return;

            IClientPlayer? player = capi.World.Player;
            if (player?.Entity == null) return;

            // Low-frequency target scan (5 times/sec): eliminates 95% of dictionary lookups and entity iterations
            scanAccumulator += deltaTime;
            if (scanAccumulator >= 0.20f)
            {
                scanAccumulator = 0f;
                ScanTrackedTargets(player, config);
            }

            // ZERO-COST IDLE: When player has no wounded targets, skip all OpenGL operations entirely!
            if (livingTargets.Count == 0 && corpseTargets.Count == 0) return;

            // Ensure white texture is valid
            EnsureWhiteTexture();

            Vec3d camPos = player.Entity.CameraPos;
            float maxDist = config.XRayMaxDistance;
            float maxDistSq = maxDist * maxDist;

            try
            {
                // Disable depth test to see through terrain and foliage
                capi.Render.GlToggleBlend(true);
                capi.Render.GLDisableDepthTest();
                capi.Render.GLDepthMask(false);

                // Render only active living targets (typically 0-2 targets)
                for (int i = 0; i < livingTargets.Count; i++)
                {
                    Entity entity = livingTargets[i];
                    if (entity?.Pos == null || !entity.Alive) continue;

                    float distSq = (float)entity.Pos.SquareDistanceTo(player.Entity.Pos);
                    if (distSq > maxDistSq) continue;

                    RenderLivingHighlight(entity, camPos, distSq, config);
                }

                // Render only active corpse beacons (typically 0-1 targets)
                for (int i = 0; i < corpseTargets.Count; i++)
                {
                    Entity entity = corpseTargets[i];
                    if (entity?.Pos == null) continue;

                    float distSq = (float)entity.Pos.SquareDistanceTo(player.Entity.Pos);
                    if (distSq > maxDistSq) continue;

                    RenderCorpseBeacon(entity, camPos, config);
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

        private void ScanTrackedTargets(IClientPlayer player, ModConfig config)
        {
            livingTargets.Clear();
            corpseTargets.Clear();

            string myUid = player.PlayerUID;
            float maxDist = config.XRayMaxDistance;
            float maxDistSq = maxDist * maxDist;
            string filter = config.XRayTargetFilter?.ToLowerInvariant() ?? "animals";

            var entities = capi.World.LoadedEntities.Values;
            int totalFound = 0;

            foreach (var entity in entities)
            {
                if (totalFound >= 16) break;
                if (entity?.Pos == null) continue;
                if (entity.EntityId == player.Entity.EntityId) continue;

                // 1. Distance culling (fast reject)
                float distSq = (float)entity.Pos.SquareDistanceTo(player.Entity.Pos);
                if (distSq > maxDistSq) continue;

                // 2. Only highlight targets wounded by THIS player
                string hunterUid = entity.WatchedAttributes?.GetString("bleedHunterUid", "") ?? "";
                if (hunterUid != myUid) continue;

                // 3. Category filter
                bool isAnimal = entity.WatchedAttributes?.GetBool("bleedIsAnimal", false) ?? false;
                if (!isAnimal)
                {
                    isAnimal = EntityHelper.IsAnimal(entity);
                }

                if (filter == "animals" && !isAnimal) continue;
                if (filter == "monsters" && isAnimal) continue;

                bool isDead = !entity.Alive || (entity.WatchedAttributes?.GetBool("bleedIsDead", false) ?? false);
                float secondsLeft = entity.WatchedAttributes?.GetFloat("bleedSecondsLeft", 0f) ?? 0f;
                float deadSecondsLeft = entity.WatchedAttributes?.GetFloat("bleedDeadSecondsLeft", 0f) ?? 0f;

                if (!isDead && secondsLeft > 0f)
                {
                    livingTargets.Add(entity);
                    totalFound++;
                }
                else if (isDead && deadSecondsLeft > 0f)
                {
                    corpseTargets.Add(entity);
                    totalFound++;
                }
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

            EnsureWhiteTexture();
            prog.Tex2D = whiteTexture!.TextureId;
            prog.RgbaLightIn = FullLight;
            prog.RgbaAmbientIn = FullAmbient;
            prog.RgbaGlowIn = ZeroGlow;
            prog.ExtraGlow = 0;
            prog.NormalShaded = 0;
            prog.ProjectionMatrix = capi.Render.CurrentProjectionMatrix;
            prog.ViewMatrix = capi.Render.CameraMatrixOriginf;

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

            // Position tip hovering directly over the animal body
            // Subtle, elegant vertical bobbing (+-6cm)
            float bob = (float)Math.Sin(capi.World.ElapsedMilliseconds / 350.0) * 0.06f;
            float tipY = (float)(entity.Pos.Y - camPos.Y + height + 0.35f + bob);
            float relX = (float)(entity.Pos.X - camPos.X);
            float relZ = (float)(entity.Pos.Z - camPos.Z);

            var prog = capi.Render.PreparedStandardShader((int)entity.Pos.X, (int)entity.Pos.Y + 1, (int)entity.Pos.Z);
            if (prog == null) return;

            EnsureWhiteTexture();
            prog.Tex2D = whiteTexture!.TextureId;
            prog.RgbaLightIn = FullLight;
            prog.RgbaAmbientIn = FullAmbient;
            prog.RgbaGlowIn = ZeroGlow;
            prog.ExtraGlow = 0;
            prog.NormalShaded = 0;
            prog.ProjectionMatrix = capi.Render.CurrentProjectionMatrix;
            prog.ViewMatrix = capi.Render.CameraMatrixOriginf;

            // Rotating 3D cone beacon pointing down at the carcass
            float rotY = (float)(capi.World.ElapsedMilliseconds / 500.0);
            Mat4f.Identity(modelMatrix);
            Mat4f.Translate(modelMatrix, modelMatrix, relX, tipY, relZ);
            Mat4f.RotateY(modelMatrix, modelMatrix, rotY);
            prog.ModelMatrix = modelMatrix;

            // Warm golden amber glow
            corpseTint.X = config.CorpseColorR;
            corpseTint.Y = config.CorpseColorG;
            corpseTint.Z = config.CorpseColorB;
            corpseTint.W = config.CorpseColorA;
            prog.RgbaTint = corpseTint;

            if (coneMeshRef != null)
            {
                capi.Render.RenderMesh(coneMeshRef);
            }

            prog.Stop();
        }

        public void Dispose()
        {
            livingTargets.Clear();
            corpseTargets.Clear();

            if (boxMeshRef != null)
            {
                capi.Render.DeleteMesh(boxMeshRef);
                boxMeshRef = null;
            }

            if (coneMeshRef != null)
            {
                capi.Render.DeleteMesh(coneMeshRef);
                coneMeshRef = null;
            }

            whiteTexture?.Dispose();
            whiteTexture = null;
        }
    }
}
