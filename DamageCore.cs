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

/*
 *  Mod by Rexxar. Developed for Doctor Octoganapus. All planet assets belong
 *  exclusively to the creator, I claim only this script.
 *  
 *  No license. Feel free to use this script however you want. All I ask is that
 *  you give credit and leave this entire comment block intact.
 *  If you're feeling especialy nice, you can donate at http://paypal.me/rexxar
 *  
 *  This mod is kind of complicated, so here's a general overview:
 *  
 *  Each planet mod has a copy of this script. Alter the first few settings in Config
 *  to suit each planet. Never, ever change the settings at the bottom of Config. Doing
 *  so will break interop with other versions of this script, and weird shit will happen.
 *  
 *  Each planet gets a GameLogicComponent attached to it. This component will look for
 *  entities within the planet's influence, calculate the required damage, then send 
 *  the results back to the session component.
 *  
 *  Each mod will have a copy of the session component, but only one may run at a time.
 *  During init, a message is sent out to everyone listening to INIT_ID. The first one
 *  to get the message wins. It removes its hook from INIT_INHIBIT, then sends out a 
 *  message with that ID. All components receiving that message will immediately and
 *  permanently disable themselves, to prevent duplicate damage.
 *  
 *  This system is so that many planets can report damage into one session component,
 *  which doles out damage slowly over the given timespan. The idea is to reduce server
 *  lag by not processing enormous amounts of damage all at once.
 */

namespace AtmosphericDamage
{
    //static class so that both components can access it

    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class DamageCore : MySessionComponentBase
    {
        internal DamageCore Instance;
        internal IMyPhysics Physics;
        internal IMyCamera Camera;

        public static HashSet<MyPlanet> Planets = new HashSet<MyPlanet>();
        private static readonly Dictionary<IMySlimBlock, MyParticleEffect> _emitters = new Dictionary<IMySlimBlock, MyParticleEffect>();
        private readonly Random _random = new Random();
        private readonly Queue<KeyValuePair<IMyDestroyableObject, float>> _actionQueue = new Queue<KeyValuePair<IMyDestroyableObject, float>>();
        private List<IMyPlayer> playerList = new List<IMyPlayer>();
        private List<IMyEntity> entities = new List<IMyEntity>();
        private List<IMyEntity> lineIntersectedGrids = new List<IMyEntity>();
        private List<IMyEntity> lineIntersectedVoxels = new List<IMyEntity>();


        //Droplet Settings
        private DropletPool Droplets = new DropletPool(200000);
        private Color lineColor = Color.White;
        private float lineThickness = 0.05f;
        private Vector3D planetCentre;
        private MyPlanet closestPlanet;
        private Vector3D cameraDistanceFromSurface;
        private double cameraAltitude;
        private MatrixD customProjectionMatrix;

        private int _actionsPerTick;
        public uint Tick;
        private MyStringHash _damageHash;
        private bool _debug;
        private bool _disable;
        private bool _init;
        private bool _initSecondFrame;
        private bool _isServer;
        private bool _processing;
        private bool _fogEnabled = false;
        private static int _updateCount;

        private Vector4 whiteColor = Color.White.ToVector4();
        private MyStringId material = MyStringId.GetOrCompute("WeaponLaser");
        private MyStringId squarematerial = MyStringId.GetOrCompute("Square");
        private Vector3D PlanetClosestPoint;
        private readonly List<LineD> lines = new List<LineD>();
        private BoundingBoxD frustumBBox;

        internal bool CheckPlanet;

        //private IMyCubeGrid gridHit = null;
        //private Vector3I? intersectingBlockVector = null;

        //private HashSet<IMyEntity> rainImpactEntities = new HashSet<IMyEntity>();
        private List<MyEntity> rainImpactEntities = new List<MyEntity>();
        private MatrixD frustumMatrix = MatrixD.Identity;

        //private List<IMyEntity> rainImpactEntities = new List<IMyEntity>();

        public override void Draw()
        {
            UpdateEmitters();

            if (Tick % 5 == 0 && _init)
            {
                if (Camera == null) return;
                Droplets.DeallocateAll();
                rainImpactEntities.Clear();
                MyGamePruningStructure.GetTopMostEntitiesInBox(ref frustumBBox, rainImpactEntities);
                MyAPIGateway.Parallel.Start(CalculateLines);
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
                        var lineAmount = 500;
                        for(int i = 0; i < lineAmount; i++) // Line calculation LOOP
                        {
                            lineThickness = MyUtils.GetRandomFloat(0.01f, 0.05f);

                            offset.Y = (float)frustumBBox.Extents.Y;
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

                            lineIntersectedGrids.Clear();
                            lineIntersectedVoxels.Clear();

                            if (frustumBBox.Intersects(ref lineCheck))
                            {
                                foreach (var ent in rainImpactEntities)
                                {
                                    if (ent as IMyCubeGrid != null && ent.Physics != null)
                                    {
                                        lineIntersectedGrids.Add(ent);
                                    }

                                    if (ent as MyVoxelBase != null)
                                    {
                                        lineIntersectedVoxels.Add(ent);
                                    }
                                }
                                
                                foreach (var ent in lineIntersectedGrids)
                                {
                                    if (ent as IMyCubeGrid != null && ent.Physics != null)
                                    {
                                        var cubeGrid = ent as IMyCubeGrid;
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
                                            }
                                        }

                                        checkVoxel = false;
                                        
                                        //LogGridBlockHits(finalHitDistSq, finalHitPos, cubeGrid, blk, lineColor);
                                    }
                                }

                                if (checkVoxel)
                                {
                                    foreach (var ent in lineIntersectedVoxels)
                                    {
                                        if (ent as MyVoxelBase != null)
                                        {
                                            var voxel = ent as MyVoxelBase;
                                            var voxelCheck = VoxelHitCheck(ent as MyVoxelBase, closestPlanet, lineStartPoint, lineEndPoint, lineCheck);
                                            if (voxelCheck != Vector3D.Zero && voxelCheck != null)
                                            {
                                                finalHitPos = voxelCheck;
                                                hitDist = Vector3D.Distance(lineStartPoint, finalHitPos);
                                                checkVoxel = true;
                                                //LogVoxelHits(hitDist, voxel, finalHitPos);
                                            }
                                        }
                                    }
                                }

                                //Logging.Instance.WriteLine(isVoxel.ToString());
                                float distanceTotal = 0f;
                                var rainDropSize = MyUtils.GetRandomFloat(0.8f, 1.5f);
                                var randSkip = MyUtils.GetRandomInt(8);
                                var hasHit = hitDist > 0.001 && hitDist.Value < lineCheck.Length;
                                var dropsInDistance = hasHit ? hitDist.Value / rainDropSize : lineCheck.Length / rainDropSize;
                                var nextStart = hasHit && !checkVoxel ? finalHitPos : lineStartPoint;
                                var dir = hasHit && !checkVoxel ? -lineCheck.Direction : lineCheck.Direction;
                                lineColor = checkVoxel ? Color.Green : Color.White;

                                //var nextStart = hasHit ? finalHitPos : lineStartPoint;
                                //var dir = hasHit ? -lineCheck.Direction : lineCheck.Direction; 

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
                    Logging.Instance.WriteLine(e.ToString());
                }
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
                    var check = false;
                    var closestPos = closestPlanet.GetClosestSurfacePointGlobal(ref lineStartPoint);
                    var planetCenter = closestPlanet.PositionComp.WorldAABB.Center;
                    double cDistToCenter = 0;
                    double pDistTocenter = 0;

                    Vector3D.DistanceSquared(ref closestPos, ref planetCenter, out cDistToCenter);
                    Vector3D.DistanceSquared(ref lineStartPoint, ref planetCenter, out pDistTocenter);

                    if (cDistToCenter > pDistTocenter || cDistToCenter > Vector3D.DistanceSquared(planetCenter, lineEndPoint)) check = true;
                    if (check)
                    {
                        using (voxel.Pin())
                        {
                            voxel.GetIntersectionWithLine(ref lineCheck, out voxelHit);
                            
                            if (voxelHit.HasValue)
                                return voxelHit.Value;
                        }
                    }
                }

                if (!voxelHit.HasValue)
                    return Vector3D.Zero;
            }

            return Vector3D.Zero;
        }

        public static Vector3D VoxelIntersectionCheck(Vector3D startScan, Vector3D scanDirection, double distance)
        {
            var voxelFrom = startScan;
            var voxelTo = scanDirection * distance + voxelFrom;
            var line = new LineD(voxelFrom, voxelTo);

            List<IMyVoxelBase> nearbyVoxels = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(nearbyVoxels);
            Vector3D closestDistance = Vector3D.Zero;

            foreach (var voxel in nearbyVoxels)
            {
                if (Vector3D.Distance(voxel.GetPosition(), voxelFrom) > 120000)
                {
                    continue;
                }

                var voxelBase = voxel as MyVoxelBase;
                Vector3D? nearestHit = Vector3D.Zero;

                if (voxelBase.GetIntersectionWithLine(ref line, out nearestHit) == true)
                {
                    if (nearestHit.HasValue == true)
                    {
                        if (closestDistance == Vector3D.Zero)
                        {
                            closestDistance = (Vector3D)nearestHit;
                            continue;
                        }

                        if (Vector3D.Distance(voxelFrom, (Vector3D)nearestHit) < Vector3D.Distance(voxelFrom, closestDistance))
                        {
                            closestDistance = (Vector3D)nearestHit;
                        }
                    }
                }
            }

            return closestDistance;

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

        public void LogVoxelHits(double? hitDist, MyVoxelBase voxel, Vector3D finalHitPos)
        {
           if (hitDist.HasValue && voxel.StorageName != null)
           {
               Logging.Instance.WriteLine("GPS:GPSCheck:" + Math.Round(finalHitPos.X).ToString() +
                                          ":" + Math.Round(finalHitPos.Y).ToString() +
                                          ":" + Math.Round(finalHitPos.Z).ToString() +
                                          ":" + " \n               -> Hit Distance: " + hitDist.Value +
                                          " \n             -> VoxelName: " + voxel.StorageName);
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
            MySimpleObjectDraw.DrawTransparentSphere(ref worldMatrix, (float)radius, ref color, raster, 20, material, material, 0.5f);
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

        public override void UpdateBeforeSimulation()
        {
            
            try
            {
                if (MyAPIGateway.Session == null)
                    return;

                Tick = (uint)(Session.ElapsedPlayTime.TotalMilliseconds * 0.0625);


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


                if (_disable)
                    return;

                if (_debug)
                    DrawDebug();

                if (!_init)
                {
                    Initialize();
                    return;
                }

                if (!_initSecondFrame)
                {
                    _initSecondFrame = true;
                    MyAPIGateway.Utilities.UnregisterMessageHandler(Config.INIT_INHIBIT_ID, InitInhibitHandler);
                    MyAPIGateway.Utilities.SendModMessage(Config.INIT_ID, true);
                    return;
                }

                ProcessQueue();

                if (++_updateCount % Config.UPDATE_RATE != 0)
                    return;

                var damageEntites = new Dictionary<IMyDestroyableObject, float>();
                MyAPIGateway.Utilities.SendModMessage(Config.DAMAGE_LIST_ID, damageEntites);

                if (_debug)
                    MyAPIGateway.Utilities.ShowMessage("Damage" + DateTime.Now.Millisecond, "received damage queue: " + damageEntites.Count);

                var emitterEntities = new Dictionary<IMySlimBlock, int>();
                MyAPIGateway.Utilities.SendModMessage(Config.PARTICLE_LIST_ID, emitterEntities);

                if (_debug)
                    MyAPIGateway.Utilities.ShowMessage("Emitters", "Receive " + emitterEntities.Count);

                CheckAndRemoveEmitters(emitterEntities);
                AddNewEmitters(emitterEntities);
                //Communication.SendEmitters(emitterEntities);

                List<KeyValuePair<IMyDestroyableObject, float>> list = damageEntites.ToList();
                list.Shuffle();
                foreach (KeyValuePair<IMyDestroyableObject, float> entry in list)
                {
                    if (_actionQueue.Count < Config.MAX_QUEUE)
                        _actionQueue.Enqueue(entry);
                    else
                        break;
                }
                _actionsPerTick = (int)Math.Ceiling((double)_actionQueue.Count / Config.UPDATE_RATE);

                lines.Clear();
            }
            catch (Exception ex)
            {
                MyAPIGateway.Utilities.ShowMessage("", ex.ToString());
                MyLog.Default.WriteLineAndConsole("##MOD:" + ex);
                //throw;
            }
        }

        public override void BeforeStart()
        {
            base.BeforeStart();
            Camera = MyAPIGateway.Session.Camera;
            var aspectRatio = Camera.ViewportSize.X / Camera.ViewportSize.Y;
            customProjectionMatrix = MatrixD.CreatePerspectiveFieldOfView(Camera.FovWithZoom, aspectRatio, 0.1f, 70.0f);
            
        }

        public static void CheckAndRemoveEmitters(Dictionary<IMySlimBlock, int> newEmitters)
        {
            var toRemove = new HashSet<IMySlimBlock>();

            foreach (KeyValuePair<IMySlimBlock, MyParticleEffect> e in _emitters)
            {
                if (MyAPIGateway.Session.Camera != null)
                {
                    if (Vector3D.DistanceSquared(MyAPIGateway.Session.Camera.Position, e.Value.WorldMatrix.Translation) > 200 * 200)
                    {
                        toRemove.Add(e.Key);
                        MyParticlesManager.RemoveParticleEffect(e.Value);
                        continue;
                    }
                }

                if (!newEmitters.ContainsKey(e.Key))
                {
                    toRemove.Add(e.Key);
                    MyParticlesManager.RemoveParticleEffect(e.Value);
                }
            }

            foreach (IMySlimBlock r in toRemove)
                _emitters.Remove(r);
        }

        public static void AddNewEmitters(Dictionary<IMySlimBlock, int> newEmitters)
        {
            List<KeyValuePair<IMySlimBlock, int>> eList = newEmitters.Where(e => Vector3D.DistanceSquared(e.Key.GetPosition(), MyAPIGateway.Session.Camera.Position) < 10000).ToList();

            eList.Shuffle();

            //_effect.UserScale = 0.5f * Grid.GridSize;
            //Vector3 normal = -Vector3.Normalize(Grid.Physics.LinearVelocity);
            //MatrixD effectMatrix = MatrixD.CreateWorld(Grid.GridIntegerToWorld(kpair.Value), normal, Vector3.CalculatePerpendicularVector(normal));
            //effectMatrix.Translation = Grid.GridIntegerToWorld(kpair.Value);
            //_effect.WorldMatrix = effectMatrix;

            for (var index = 0; index < eList.Count; index++)
            {
                KeyValuePair<IMySlimBlock, int> e = eList[index];
                if (!_emitters.ContainsKey(e.Key))
                {
                    MyParticleEffect eff;
                    MyParticlesManager.TryCreateParticleEffect(e.Value, out eff);
                    var mat = new MatrixD();
                    mat.Translation = e.Key.GetPosition();

                    eff.WorldMatrix = mat;
                    eff.Start(e.Value, eff.Name);
                    if(e.Key.CubeGrid.GridSizeEnum == MyCubeSize.Small)
                        eff.UserEmitterScale = 0.1f;
                    _emitters.Add(e.Key, eff);
                }
            }
        }

        public override void LoadData()
        {
            base.LoadData();
            Instance = this;
        }

        protected override void UnloadData()
        {
            Logging.Instance.Close();
            Droplets.DeallocateAll();
            MyAPIGateway.Utilities.MessageEntered -= Utilities_MessageEntered;
            MyAPIGateway.Utilities.UnregisterMessageHandler(Config.INIT_INHIBIT_ID, InitInhibitHandler);
            MyAPIGateway.Utilities.UnregisterMessageHandler(Config.INIT_ID, InitMessageHandler);
        }

        private void DrawDebug()
        {
            for (var i = 0; i < lines.Count; i++)
            {
                LineD line = lines[i];
                MySimpleObjectDraw.DrawLine(line.From, line.To, MyStringId.GetOrCompute("WeaponLaser"), ref whiteColor, 0.1f);
            }
        }

        private void Initialize()
        {
            _init = true;
            _isServer = MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.OFFLINE || MyAPIGateway.Multiplayer.IsServer;
            Communication.RegisterHandlers();
            Config.InitVoxelIDs();

            if (Config.UPDATE_RATE % 10 != 0)
                throw new Exception("UPDATE_RATE must be divisible by 10!");
            MyAPIGateway.Utilities.RegisterMessageHandler(Config.INIT_ID, InitMessageHandler);
            MyAPIGateway.Utilities.RegisterMessageHandler(Config.INIT_INHIBIT_ID, InitInhibitHandler);
        }

        private void InitInhibitHandler(object o)
        {
            if (o is bool && (bool)o)
                _disable = true;
        }

        private void Initialize_Continuation()
        {
            if (_disable)
                return;

            MyAPIGateway.Utilities.MessageEntered += Utilities_MessageEntered;
            _damageHash = MyStringHash.GetOrCompute(Config.DAMAGE_STRING);
        }

        private void HandleDrawQueue(object o)
        {
            //throw new NotImplementedException();
        }

        private void InitMessageHandler(object o)
        {
            if (!(o is bool))
                return;

            if ((bool)o)
            {
                MyAPIGateway.Utilities.SendModMessage(Config.INIT_INHIBIT_ID, o);
                Initialize_Continuation();
            }
        }

        private void Utilities_MessageEntered(string messageText, ref bool sendToOthers)
        {
            if (messageText == "!$debug")
                _debug = !_debug;
        }

        //spread our invoke queue over many updates to avoid lag spikes
        private void ProcessQueue()
        {
            //if (_debug)
            //    MyAPIGateway.Utilities.ShowMessage("Damage", "Processing " + _actionQueue.Count);
            if (_actionQueue.Count == 0)
                return;

            for (var i = 0; i < _actionsPerTick; i++)
            {
                KeyValuePair<IMyDestroyableObject, float> pair;
                if (!_actionQueue.TryDequeue(out pair))
                    return;
                try
                {
                    pair.Key.DoDamage(pair.Value, _damageHash, true);
                }
                catch
                {
                    //don't care
                }
            }
        }

        private void UpdateEmitters()
        {
            var toRemove = new HashSet<IMySlimBlock>();
            foreach (KeyValuePair<IMySlimBlock, MyParticleEffect> e in _emitters)
            {
                Vector3D pos = e.Key.GetPosition();

                bool closed = e.Key.Closed();
                bool zero = Vector3D.IsZero(pos);

                if (closed || zero)
                {
                    //if(zero)
                    //MyAPIGateway.Utilities.ShowMessage("Emitter", $"{closed}:{zero}");
                    toRemove.Add(e.Key);
                    e.Value.Stop();
                    MyParticlesManager.RemoveParticleEffect(e.Value);
                    continue;
                }
                MatrixD mat = e.Value.WorldMatrix;
                mat.Translation = pos;
                e.Value.WorldMatrix = mat;
            }

            foreach (IMySlimBlock r in toRemove)
                _emitters.Remove(r);
        }
    }
}

