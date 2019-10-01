using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using AtmosphereDamage;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.GUI.DebugInputComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRage.Game.Models;
using VRage.Game.Utils;
using VRage.Game.VisualScripting;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using VRageRender;
using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum;
using MyVisualScriptLogicProvider = Sandbox.Game.MyVisualScriptLogicProvider;

namespace AtmosphericDamage
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class RainEffect : MySessionComponentBase
    {

        private bool _init;
        private static int _updateCount;
        public uint Tick;

        internal IMyCamera Camera;

        internal bool CheckPlanet;
        private Vector3D planetCentre;
        private MyPlanet closestPlanet;
        private Vector3D cameraDistanceFromSurface;
        private double cameraAltitude;
        private MatrixD customProjectionMatrix;

        private Vector4 whiteColor = Color.White.ToVector4();
        private MyStringId material = MyStringId.GetOrCompute("WeaponLaser");
        private MyStringId squarematerial = MyStringId.GetOrCompute("Square");
        private Vector3D PlanetClosestPoint;
        private readonly List<LineD> lines = new List<LineD>();
        private BoundingBoxD frustumBBox;

        //Droplet Settings
        private List<MyEntity> rainImpactEntities = new List<MyEntity>();
        private MatrixD frustumMatrix = MatrixD.Identity;
        private List<IMyEntity> lineIntersectedGrids = new List<IMyEntity>();
        private List<IMyEntity> lineIntersectedVoxels = new List<IMyEntity>();
        private DropletPool Droplets = new DropletPool(200000);
        private Color lineColor = Color.White;
        private float lineThickness = 0.05f;
        private MyVoxelBase voxelHitName;

        public override void BeforeStart()
        {
            base.BeforeStart();
            Camera = MyAPIGateway.Session.Camera;
            var aspectRatio = Camera.ViewportSize.X / Camera.ViewportSize.Y;
            customProjectionMatrix = MatrixD.CreatePerspectiveFieldOfView(Camera.FovWithZoom, aspectRatio, 0.1f, 70.0f);
        }

        public override void UpdateBeforeSimulation()
        {
            if (Tick % 301 == 0)
            {
                closestPlanet = MyGamePruningStructure.GetClosestPlanet(Camera.Position);
                if (closestPlanet != null)
                    planetCentre = closestPlanet.PositionComp.WorldAABB.Center;
            }

            if (Tick % 60 == 0 && closestPlanet != null)
            {
                cameraDistanceFromSurface = closestPlanet.GetClosestSurfacePointGlobal(Camera.Position);
                cameraAltitude = (Camera.Position - cameraDistanceFromSurface).Length();
            }

            lines.Clear();
        }

        public override void Draw()
        {
            if (Tick % 5 == 0)
            {
                if (Camera == null) return;
                Droplets.DeallocateAll();
                rainImpactEntities.Clear();
                MyGamePruningStructure.GetTopMostEntitiesInBox(ref frustumBBox, rainImpactEntities);
                //MyAPIGateway.Parallel.Start(CalculateLines);
                CalculateLines();
            }

            //if (Droplets.Active.Count >0) Logging.Instance.WriteLine($"droplets: {Droplets.Active.Count}");

            var dropletsToDraw = Droplets.Active;
            if (dropletsToDraw.Count > 0)
            {
                for (int i = 0; i < 700; i++) //Switched to hard limit of line per tick
                {
                    var droplet = dropletsToDraw[MyUtils.GetRandomInt(dropletsToDraw.Count - 1)];
                    if (droplet == null) continue;
                    MyTransparentGeometry.AddLineBillboard(material, droplet.LineColor, droplet.StartPoint, droplet.Direction, droplet.DrawLength, lineThickness);
                }
            }

            //DrawLineFromPlayerToPlanetCentre();
            //DrawAndLogFrustumBox();
        }

        public void CalculateLines()
        {
            try
            {
                if (closestPlanet != null)
                {
                    var planetAtmosphereAltitude = closestPlanet.AtmosphereAltitude;

                    var cameraUp = new Vector3D(Camera.Position - planetCentre);
                    var cameraForward = Vector3D.CalculatePerpendicularVector(cameraUp);
                    frustumMatrix = MatrixD.CreateWorld(Camera.Position, cameraForward, cameraUp);
                    var offset = Vector3.Zero;

                    var frustum = new BoundingFrustumD(Camera.ViewMatrix * customProjectionMatrix);
                    frustumBBox = BoundingBoxD.CreateInvalid();
                    frustumBBox.Include(ref frustum);

                    if (cameraAltitude < (planetAtmosphereAltitude / 2))
                    {
                        var lineAmount = 5000;
                        for (int i = 0; i < lineAmount; i++) // Line calculation LOOP
                        {
                            lineThickness = MyUtils.GetRandomFloat(0.01f, 0.05f);

                            offset.Y = (float) frustumBBox.Extents.Y;
                            offset.X = MyUtils.GetRandomInt(-60, 60);
                            offset.Z = MyUtils.GetRandomInt(-60, 60);

                            if (offset.X >= 0 && offset.X < 1)
                            {
                                offset.X = offset.X + 1;
                            }

                            if (offset.Z >= 0 && offset.Z < 1)
                            {
                                offset.Z = offset.Z + 1;
                            }

                            Vector3D lineStartPoint = Vector3D.Transform(offset, frustumMatrix);
                            Vector3D lineEndPoint = planetCentre;

                            var length = frustumBBox.HalfExtents.Y * 0.25; // Shorten line length by 1/4
                            LineD lineCheck = new LineD(lineStartPoint, lineEndPoint, length);

                            Vector3D finalHitPos = lineEndPoint;
                            Vector3D hitPos = lineEndPoint;
                            double? hitDist = double.MaxValue;
                            double finalHitDistSq = double.MaxValue;
                            var checkVoxel = true;
                            var isVoxel = false;

                            lineIntersectedGrids.Clear();
                            lineIntersectedVoxels.Clear();

                            if (frustumBBox.Intersects(ref lineCheck))
                            {
                                /*
                                foreach (var ent in rainImpactEntities) // Line calculation LOOP
                                {
                                    if (ent as IMyCubeGrid != null && (ent as IMyCubeGrid).Physics != null)
                                    {
                                        lineIntersectedGrids.Add(ent);
                                    }

                                    if (ent as MyVoxelBase != null)
                                    {
                                        lineIntersectedVoxels.Add(ent);
                                    }
                                }

                                foreach (var ent in  lineIntersectedGrids)
                                {
                                    var cubeGrid = ent as IMyCubeGrid;
                                    if (cubeGrid != null && cubeGrid.Physics != null)
                                    {

                                        MyOrientedBoundingBoxD gridOBB = new MyOrientedBoundingBoxD(cubeGrid.LocalAABB, cubeGrid.WorldMatrix);
                                        //DrawOBB(gridOBB, whiteColor, MySimpleObjectRasterizer.Wireframe, 0.01f);

                                        // If we don't intersect a grid continue.
                                        if (!gridOBB.Intersects(ref lineCheck).HasValue)
                                        {
                                            continue;
                                        }

                                        hitDist = GridHitCheck(cubeGrid, lineCheck, lineStartPoint, lineEndPoint);

                                        if (hitDist != null)
                                        {
                                            hitPos = lineStartPoint + (lineCheck.Direction * hitDist.Value);
                                            if (finalHitDistSq > hitDist.Value)
                                            {
                                                finalHitPos = hitPos;
                                                finalHitDistSq = hitDist.Value;
                                                checkVoxel = false;
                                            }
                                        }
                                        //LogGridBlockHits(finalHitDistSq, finalHitPos, cubeGrid, blk, lineColor);
                                    }
                                }

                                if (checkVoxel)
                                {
                                    foreach (var ent in lineIntersectedVoxels)
                                    {
                                        var voxel = ent as MyVoxelBase;
                                        if (voxel != null)
                                        {
                                            var voxelCheck = VoxelHitCheck(voxel, closestPlanet, lineStartPoint, lineEndPoint, lineCheck);
                                            if (voxelCheck != Vector3D.Zero && voxelCheck != null)
                                            {
                                                finalHitPos = voxelCheck;
                                                hitDist = Vector3D.Distance(lineStartPoint, finalHitPos);
                                                voxelHitName = voxel;
                                                isVoxel = true;
                                            }
                                        }
                                    }
                                }
                                */


                                for (int j = 0; j < rainImpactEntities.Count; j++) // Line calculation LOOP
                                {
                                    var rainedOnEnt = rainImpactEntities[j];
                                    var grid = rainedOnEnt as IMyCubeGrid;
                                    if (grid != null && grid.Physics != null)
                                    {
                                        lineIntersectedGrids.Add(rainedOnEnt);
                                    }
                                    else if (rainedOnEnt is MyVoxelBase)
                                    {
                                        lineIntersectedVoxels.Add(rainedOnEnt);
                                    }
                                }

                                for (int k = 0; k < lineIntersectedGrids.Count; k++)
                                {
                                    var intersectedGrid = lineIntersectedGrids[k];
                                    var cubeGrid = intersectedGrid as IMyCubeGrid;
                                    if (cubeGrid != null && cubeGrid.Physics != null)
                                    {

                                        MyOrientedBoundingBoxD gridOBB = new MyOrientedBoundingBoxD(cubeGrid.LocalAABB, cubeGrid.WorldMatrix);
                                        //DrawOBB(gridOBB, whiteColor, MySimpleObjectRasterizer.Wireframe, 0.01f);

                                        // If we don't intersect a grid continue.
                                        if (!gridOBB.Intersects(ref lineCheck).HasValue)
                                        {
                                            continue;
                                        }

                                        hitDist = GridHitCheck(cubeGrid, lineCheck, lineStartPoint, lineEndPoint);

                                        if (hitDist != null)
                                        {
                                            hitPos = lineStartPoint + (lineCheck.Direction * hitDist.Value);
                                            if (finalHitDistSq > hitDist.Value)
                                            {
                                                finalHitPos = hitPos;
                                                finalHitDistSq = hitDist.Value;
                                                checkVoxel = false;
                                            }
                                        }

                                        //LogGridBlockHits(finalHitDistSq, finalHitPos, cubeGrid, blk, lineColor);
                                    }
                                }

                                /*
                                if (checkVoxel)
                                {
                                    for (int l = 0; l < lineIntersectedVoxels.Count; l++)
                                    {
                                        var intersectedVoxel = lineIntersectedVoxels[l];
                                        var voxelHitName = intersectedVoxel as MyVoxelBase;
                                        if (voxelHitName != null)
                                        {
                                            var voxelCheck = VoxelHitCheck(voxelHitName, closestPlanet, lineStartPoint, lineEndPoint, lineCheck);
                                            if (voxelCheck != Vector3D.Zero && voxelCheck != null)
                                            {
                                                finalHitPos = voxelCheck;
                                                hitDist = Vector3D.Distance(lineStartPoint, finalHitPos);
                                                //LogVoxelHits(hitDist, voxelHitName, finalHitPos, lineCheck.Length);
                                                isVoxel = true;
                                            }
                                        }
                                    }
                                }
                                */
                                /*
                                // Log Loop sizes
                                if (_updateCount % 100 == 0)
                                {
                                    Logging.Instance.WriteLine(rainImpactEntities.Count.ToString() + " " +
                                                               lineIntersectedGrids.Count.ToString() + " " +
                                                               lineIntersectedVoxels.Count.ToString());
                                }
                                */


                                //Logging.Instance.WriteLine(isVoxel.ToString());
                                float distanceTotal = 0f;
                                var rainDropSize = MyUtils.GetRandomFloat(0.8f, 1.5f);
                                var randSkip = MyUtils.GetRandomInt(8);
                                var hasHit = hitDist.Value > 0.001 && (hitDist.Value < lineCheck.Length || isVoxel);
                                var dropsInDistance = hasHit ? hitDist.Value / rainDropSize : lineCheck.Length / rainDropSize;

                                //var nextStart = hasHit ? finalHitPos : lineStartPoint;
                                //var dir = hasHit ? -lineCheck.Direction : lineCheck.Direction;

                                var nextStart = hasHit ? finalHitPos : finalHitPos;
                                var dir = hasHit ? -lineCheck.Direction : -lineCheck.Direction;

                                //var nextStart = hasHit && !checkVoxel ? finalHitPos : finalHitPos;
                                //var dir = hasHit && !checkVoxel ? -lineCheck.Direction : -lineCheck.Direction;

                                lineColor = checkVoxel ? Color.Green : Color.White;
                                if (checkVoxel && voxelHitName != null && _updateCount % 300 == 0)
                                {
                                    //LogVoxelHits(hitDist, voxelHitName, finalHitPos, lineCheck.Length);
                                }

                                while (distanceTotal < dropsInDistance)
                                {
                                    if (randSkip-- <= 0)
                                    {
                                        Droplet droplet;
                                        Droplets.AllocateOrCreate(out droplet);

                                        droplet.StartPoint = nextStart;
                                        droplet.Direction = dir;
                                        droplet.DrawLength = rainDropSize;
                                        droplet.LineColor = lineColor;
                                        randSkip = MyUtils.GetRandomInt(8);
                                    }

                                    distanceTotal += rainDropSize;
                                    nextStart += (dir * rainDropSize);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (_updateCount % 100 == 0)
                {
                    MyVisualScriptLogicProvider.SendChatMessage(e.ToString(), "Error");
                }

                Logging.Instance.WriteLine(e.ToString());
            }
        }

        public static double? GridHitCheck(IMyCubeGrid cubeGrid, LineD lineCheck, Vector3D lineStartPoint, Vector3D lineEndPoint)
        {
            // Hit a grid, do a raycast to it for block hit.
            Vector3I? gridBlockHit = cubeGrid.RayCastBlocks(lineStartPoint, lineEndPoint);
            if (gridBlockHit.HasValue)
            {
                // Get the block on hit grid
                IMySlimBlock blk = cubeGrid.GetCubeBlock(gridBlockHit.Value);
                if (blk.FatBlock != null)
                {
                    var blockBBox = blk.FatBlock.LocalAABB;
                    MyOrientedBoundingBoxD blockOBB = new MyOrientedBoundingBoxD(blockBBox, blk.FatBlock.WorldMatrix);
                    double? blockIntersect = blockOBB.Intersects(ref lineCheck);
                    if (blockIntersect != null)
                    {
                        var hitDist = blockOBB.Intersects(ref lineCheck);
                        return hitDist;
                    }

                    //DrawOBB(blockOBB, Color.Red, MySimpleObjectRasterizer.Wireframe, 0.01f);
                }
                else
                {
                    var center = blk.GetPosition();

                    Vector3 halfExt;
                    blk.ComputeScaledHalfExtents(out halfExt);

                    BoundingBoxD blockBBox = new BoundingBoxD(-halfExt, halfExt);
                    Quaternion rotMatrix = Quaternion.CreateFromRotationMatrix(blk.CubeGrid.WorldMatrix);
                    MyOrientedBoundingBoxD blockOBB = new MyOrientedBoundingBoxD(center, blockBBox.HalfExtents, rotMatrix);

                    var hitDist = blockOBB.Intersects(ref lineCheck);
                    return hitDist;

                    //DrawOBB(blockOBB, Color.Green, MySimpleObjectRasterizer.Wireframe, 0.01f);
                }
            }

            return double.MaxValue;
        }

        public static Vector3D VoxelHitCheck(MyVoxelBase voxel, MyPlanet closestPlanet, Vector3D lineStartPoint, Vector3D lineEndPoint, LineD lineCheck)
        {
            Vector3D? voxelHit = Vector3D.Zero;
            if (voxel != null)
            {
                if (voxel.RootVoxel != voxel)
                    return Vector3D.Zero;

                if (voxel == closestPlanet)
                {
                    var closestPos = closestPlanet.GetClosestSurfacePointGlobal(ref lineStartPoint);
                    return closestPos;
                }
            }

            return Vector3D.Zero;
        }

        public void DrawAndLogFrustumBox()
        {
            Logging.Instance.WriteLine("Matrix Translation: " + frustumMatrix.Translation.ToString());
            Logging.Instance.WriteLine("Frustum BBox Centre: " + frustumBBox.Center.ToString());
            Logging.Instance.WriteLine("Frustum BBox Min: " + frustumBBox.Min.ToString());
            Logging.Instance.WriteLine("Frustum BBox Max: " + frustumBBox.Max.ToString());
            Logging.Instance.WriteLine("Player Position: " + MyAPIGateway.Session.Player.Character.GetPosition().ToString());
            Logging.Instance.WriteLine("Camera Position: " + Camera.WorldMatrix.Translation.ToString());
            var fObb = new MyOrientedBoundingBoxD(frustumBBox.Center, frustumBBox.HalfExtents, Quaternion.Zero);
            var c = Color.Red;
            DrawOBB(fObb, c);
        }

        public void LogGridBlockHits(double? finalHitDistSq, Vector3D finalHitPos, IMyCubeGrid cubeGrid, IMySlimBlock blk, Color lineColor)
        {
            var distCheck = "Expected";
            if (finalHitDistSq.Value > 1.0f)
            {
                distCheck = "Unexpected";
            }

            if (blk.FatBlock != null)
            {
                Logging.Instance.WriteLine("FatBlock(" + blk.BlockDefinition.DisplayNameText +
                                           "):" + Math.Round(finalHitPos.X).ToString() +
                                           ":" + Math.Round(finalHitPos.Y).ToString() +
                                           ":" + Math.Round(finalHitPos.Z).ToString() +
                                           ":" + " \n               -> Hit Distance: " + finalHitDistSq.Value +
                                           " \n             -> Gridname: " + cubeGrid.DisplayName +
                                           " : " + distCheck + " : LineColor: " + lineColor);
            }
            else
            {
                Logging.Instance.WriteLine("SlimBlock(" + blk.BlockDefinition.DisplayNameText +
                                           "):" + Math.Round(finalHitPos.X).ToString() +
                                           ":" + Math.Round(finalHitPos.Y).ToString() +
                                           ":" + Math.Round(finalHitPos.Z).ToString() +
                                           ":" + " \n               -> Hit Distance: " + finalHitDistSq.Value +
                                           " \n             -> Gridname: " + cubeGrid.DisplayName +
                                           " : " + distCheck + " : LineColor: " + lineColor);
            }
        }

        public void LogVoxelHits(double? hitDist, MyVoxelBase voxel, Vector3D finalHitPos, double lineCheckLength)
        {
            if (hitDist.HasValue && voxel.StorageName != null)
            {
                Logging.Instance.WriteLine("GPS:GPSCheck:" + Math.Round(finalHitPos.X).ToString() +
                                           ":" + Math.Round(finalHitPos.Y).ToString() +
                                           ":" + Math.Round(finalHitPos.Z).ToString() +
                                           ":" + " \n               -> Hit Distance: " + hitDist.Value +
                                           " \n             -> VoxelName: " + voxel.StorageName +
                                           " \n             -> LineCheck Length: " + lineCheckLength);
            }
        }

        public void DrawLine(Color lineColor, Vector3D lineStartPoint, Vector3D lineHitVectorPoint, float length = 1.0f, float thickness = 0.05f)
        {
            MyTransparentGeometry.AddLineBillboard(material, lineColor, lineStartPoint, lineHitVectorPoint - lineStartPoint, length, thickness);
        }

        public static void DrawBB(MatrixD wm, BoundingBoxD bb, Color color, MySimpleObjectRasterizer raster = MySimpleObjectRasterizer.Wireframe, float thickness = 0.01f)
        {
            var material = MyStringId.GetOrCompute("Square");
            MySimpleObjectDraw.DrawTransparentBox(ref wm, ref bb, ref color, raster, 1, thickness, material, material);
        }

        public static void DrawOBB(MyOrientedBoundingBoxD obb, Color color, MySimpleObjectRasterizer raster = MySimpleObjectRasterizer.Wireframe, float thickness = 0.01f)
        {
            var material = MyStringId.GetOrCompute("Square");
            var box = new BoundingBoxD(-obb.HalfExtent, obb.HalfExtent);
            var wm = MatrixD.CreateFromQuaternion(obb.Orientation);
            wm.Translation = obb.Center;
            MySimpleObjectDraw.DrawTransparentBox(ref wm, ref box, ref color, raster, 1, thickness, material, material);
        }

        public static void DrawSphere(MatrixD worldMatrix, double radius, MySimpleObjectRasterizer raster = MySimpleObjectRasterizer.Wireframe)
        {
            var color = Color.White;
            var material = MyStringId.GetOrCompute("Square");
            MySimpleObjectDraw.DrawTransparentSphere(ref worldMatrix, (float) radius, ref color, raster, 20, material, material, 0.5f);
        }

        public void DrawLineFromPlayerToPlanetCentre()
        {
            var player = MyAPIGateway.Session.Player;
            var playerPosition = player.Character.PositionComp.GetPosition();
            var lineColor = Color.White.ToVector4();
            var closestPlanet = MyGamePruningStructure.GetClosestPlanet(playerPosition);
            if (closestPlanet != null)
            {
                var planetCentre = closestPlanet.PositionComp.GetPosition();
                var lineStartPoint = playerPosition;
                var lineEndPoint = planetCentre;
                var lineCheck = new LineD(lineStartPoint, lineEndPoint);
                DrawLine(whiteColor, lineStartPoint, lineEndPoint);

            }
        }

        protected override void UnloadData()
        {
            Droplets.DeallocateAll();
        }
    }
}
