using System;
using System.Collections.Generic;
using System.Linq;
using AtmosphereDamage;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.ModAPI;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;
using VRageRender;
using static VRage.ModAPI.MyAPIGatewayShortcuts;

namespace AtmosphericDamage
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Planet), false)]
    public class AtmosphereComponent : MyGameLogicComponent
    {
        private readonly Dictionary<IMySlimBlock, int> _blockParticles = new Dictionary<IMySlimBlock, int>();
        private readonly MyConcurrentDictionary<IMyDestroyableObject, float> _damageEntities = new MyConcurrentDictionary<IMyDestroyableObject, float>();
        private List<IMyPlayer> playerList = new List<IMyPlayer>();
        private MyStringHash _damageHash;
        private readonly List<LineD> _lines = new List<LineD>();
        public static MyPlanet _planet;
        private bool _processing;
        private BoundingSphereD _sphere;
        private List<IMyEntity> _topEntityCache;
        private int _updateCount;
        private int _tickCount;
        private Vector3D startingPosition;
        public static GetMainCameraCallback MainCamera;


        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            _planet = Entity as MyPlanet;
            if (_planet == null)
            {
                //TODO: Remove this component from the planet? Might not be worth it
                NeedsUpdate = MyEntityUpdateEnum.NONE;
                return;
            }
            _damageHash = MyStringHash.GetOrCompute(Config.DAMAGE_STRING);
            NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME | MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void Close()
        {
            MyAPIGateway.Utilities.UnregisterMessageHandler(Config.PARTICLE_LIST_ID, HandleParticleRequest);
            MyAPIGateway.Utilities.UnregisterMessageHandler(Config.DAMAGE_LIST_ID, HandleDamageRequest);
            MyAPIGateway.Utilities.UnregisterMessageHandler(Config.DRAW_LIST_ID, HandleDrawRequest);
        }

        //Planet might not be fully initialized at Init, so do these on the first frame
        public override void UpdateOnceBeforeFrame()
        {
            if (_planet == null || !_planet.StorageName.StartsWith(Config.PLANET_NAME))
            {
                //TODO: Remove this component from the planet? Might not be worth it
                NeedsUpdate = MyEntityUpdateEnum.NONE;
                return;
            }
            _sphere = new BoundingSphereD(_planet.PositionComp.GetPosition(), _planet.AverageRadius + _planet.AtmosphereAltitude);
            
            MyAPIGateway.Utilities.RegisterMessageHandler(Config.PARTICLE_LIST_ID, HandleParticleRequest);
            MyAPIGateway.Utilities.RegisterMessageHandler(Config.DAMAGE_LIST_ID, HandleDamageRequest);
            MyAPIGateway.Utilities.RegisterMessageHandler(Config.DRAW_LIST_ID, HandleDrawRequest);
        }

        public override void UpdateBeforeSimulation10()
        {
            if (_processing) //worker thread is busy
                return;

            _updateCount += 10;
            bool processCharacter = _updateCount % 60 == 0;
            bool processPlanet = _updateCount % Config.UPDATE_RATE == 0;

            if (processCharacter || processPlanet)
                MyAPIGateway.Parallel.Start(() =>
                                            {
                                                try
                                                {
                                                    _damageEntities.Clear();
                                                    _topEntityCache = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref _sphere);
                                                    if (processCharacter && Config.DAMAGE_PLAYERS)
                                                        ProcessCharacterDamage();
                                                    if (processPlanet)
                                                        ProcessDamage();
                                                }
                                                catch (Exception ex)
                                                {
                                                    MyLog.Default.WriteLineAndConsole($"##MOD: Atmospheric component error: {ex}");
                                                    throw;
                                                }
                                                finally
                                                {
                                                    _processing = false;
                                                }
                                            });
        }

        private void ProcessDamage()
        {
            foreach (IMyEntity entity in _topEntityCache)
            {
                var grid = entity as IMyCubeGrid;
                if (grid?.Physics != null)
                {
                    if (grid.Closed || grid.MarkedForClose)
                        continue;

                    float damage = grid.GridSizeEnum == MyCubeSize.Small ? Config.SMALL_SHIP_DAMAGE : Config.LARGE_SHIP_DAMAGE;
                    if (Config.BLOCK_VOXEL_DAMAGE)
                    {
                        byte voxelId;
                        List<IMySlimBlock> vBlocks = Utilities.GetBlocksContactingVoxel(grid, _planet, out voxelId);
                        VoxelDamageItem item;
                        if (!Config.VOXEL_IDS.TryGetValue(voxelId, out item))
                        {
                            MyLog.Default.WriteLine($"{voxelId} NOT FOUND");
                            item = Config.VOXEL_IDS.First().Value;
                        }
                        foreach (IMySlimBlock b in vBlocks)
                        {
                            _damageEntities.AddOrUpdate(b, damage * item.DamageMultiplier);
                            if (item.ParticleEffect.HasValue)
                                _blockParticles[b] = item.ParticleEffect.Value;
                        }
                    }

                    if (!Config.BLOCK_RADIATION_DAMAGE && !Config.BLOCK_ACID_RAIN && Utilities.IsEntityInsideGrid(grid))
                        continue;

                    //if (BLOCK_ACID_RAIN && IsEntityCovered(grid, sphere.Center))
                    //{
                    //    //MyAPIGateway.Utilities.ShowMessage("GRID COVERED", grid.DisplayName);
                    //    continue;
                    //}

                    var blocks = new List<IMySlimBlock>();
                    grid.GetBlocks(blocks);

                    Vector3D offset = Vector3D.Zero;
                    if (Config.BLOCK_ACID_RAIN)
                    {
                        Vector3D direction = grid.WorldVolume.Center - _sphere.Center;
                        direction.Normalize();
                        offset = direction;
                    }

                    if (Config.BLOCK_ACID_RAIN || Config.BLOCK_RADIATION_DAMAGE)
                        for (var i = 0; i < Math.Max(1, blocks.Count * 0.3); i++)
                        {
                            IMySlimBlock block;
                            if (Config.BLOCK_ACID_RAIN)
                                block = Utilities.GetRandomSkyFacingBlock(grid, blocks, offset, true);
                            else
                                block = Utilities.GetRandomExteriorBlock(grid, blocks);

                            if (block == null)
                                continue;

                            _damageEntities.AddOrUpdate(block, damage);
                            //blocks.Remove(block);
                            //QueueInvoke(() =>
                            //            {
                            //                if (block != null && !block.Closed())
                            //                    block.DoDamage(damage, _damageHash, true);
                            //            });
                        }

                    continue;
                }

                var floating = entity as IMyFloatingObject;
                if (floating != null)
                {
                    if (floating.Closed || floating.MarkedForClose)
                        continue;

                    if (Config.BLOCK_RADIATION_DAMAGE)
                        _damageEntities.AddOrUpdate(floating, Config.SMALL_SHIP_DAMAGE);

                    if (Config.BLOCK_ACID_RAIN && !(Utilities.IsEntityCovered(floating, _sphere.Center) || Utilities.IsFullyInsideVoxel(floating, _planet)))
                        _damageEntities.AddOrUpdate(floating, Config.SMALL_SHIP_DAMAGE);

                    Vector3D pos = floating.GetPosition();
                    Vector3D s = _planet.GetClosestSurfacePointGlobal(ref pos);
                    if (Vector3D.DistanceSquared(pos, s) <= 4)
                    {
                        MyVoxelMaterialDefinition mat = _planet.GetMaterialAt_R(ref s);
                        VoxelDamageItem vox;
                        if (Config.VOXEL_IDS.TryGetValue(mat.Index, out vox))
                            _damageEntities.AddOrUpdate(floating, Config.LARGE_SHIP_DAMAGE * vox.DamageMultiplier);
                    }
                }
            }

            if (Config.BLOCK_ACID_RAIN && Config.DRAW_RAIN)
                SendDrawQueue();
        }

        private void ProcessCharacterDamage()
        {
            
            foreach (IMyEntity entity in _topEntityCache)
            {
                var character = entity as IMyCharacter;
                if (character != null)
                {
                    if (character.Closed || character.MarkedForClose)
                        continue;

                    if (Config.PLAYER_ACID_RAIN && !Utilities.IsEntityCovered(entity, _sphere.Center))
                        _damageEntities.AddOrUpdate(character, Config.PLAYER_DAMAGE_AMOUNT);

                    if (Config.PLAYER_RADIATION_DAMAGE)
                    {
                        
                        var comp = character.Components.Get<MyCharacterOxygenComponent>();

                        //MyAPIGateway.Utilities.ShowMessage("Oxygen at Point Check: ", MyAPIGateway.Session.OxygenProviderSystem.GetOxygenInPoint(character.GetPosition()).ToString());
                        //MyAPIGateway.Utilities.ShowMessage("Oxygen at Char: ", comp.OxygenLevelAtCharacterLocation.ToString());
                        
                        if (comp == null || MyAPIGateway.Session.OxygenProviderSystem.GetOxygenInPoint(character.GetPosition()) >= comp.OxygenLevelAtCharacterLocation)
                        {
                            _damageEntities.AddOrUpdate(character, Config.PLAYER_DAMAGE_AMOUNT);
                        }
                    }

                    if (Config.PLAYER_VOXEL_DAMAGE)
                    {
                        Vector3D characterPos = character.GetPosition();
                        Vector3D surfacePos = _planet.GetClosestSurfacePointGlobal(ref characterPos);
                        //if (surfacePos == Vector3.Zero)
                        //    continue;
                        if (Vector3D.DistanceSquared(characterPos, surfacePos) > 6.25)
                            continue;
                        MyVoxelMaterialDefinition mat = _planet.GetMaterialAt_R(ref surfacePos);

                        //MyAPIGateway.Utilities.ShowMessage("Damage", mat.MaterialTypeName);
                        VoxelDamageItem item;
                        if (Config.VOXEL_IDS.TryGetValue(mat.Index, out item))
                            _damageEntities.AddOrUpdate(character, Config.PLAYER_DAMAGE_AMOUNT * item.CharacterMultiplier);
                    }

                    //_damageEntities[character] = Config.PLAYER_DAMAGE_AMOUNT;
                    //character.DoDamage(Config.PLAYER_DAMAGE_AMOUNT, _damageHash, true);
                }
            }
        }

        private void SendDrawQueue()
        {
            if (_lines.Any())
                MyAPIGateway.Utilities.InvokeOnGameThread(() => MyAPIGateway.Utilities.SendModMessage(Config.DRAW_LIST_ID, _lines));
        }

        private void HandleDrawRequest(object o)
        {
            //throw new NotImplementedException();
        }

        private void HandleDamageRequest(object o)
        {
            var dic = o as Dictionary<IMyDestroyableObject, float>;
            if (dic == null)
                return;
            foreach (KeyValuePair<IMyDestroyableObject, float> e in _damageEntities)
                dic.AddOrUpdate(e.Key, e.Value);

            _damageEntities.Clear();
        }

        private void HandleParticleRequest(object o)
        {
            var dic = o as Dictionary<IMySlimBlock, int>;
            if (dic == null)
                return; //log? meh.
            foreach (KeyValuePair<IMySlimBlock, int> e in _blockParticles)
                dic[e.Key] = e.Value;
            _blockParticles.Clear();
        }
    }
}

/*
                    foreach (IMyEntity entity in _topEntityCache)
                    {
                        var grid = entity as IMyCubeGrid;
                        if (grid?.Physics != null)
                        {
                            if (grid.Closed || grid.MarkedForClose)
                            continue;

                            if (!Config.BLOCK_RADIATION_DAMAGE && !Config.BLOCK_ACID_RAIN &&
                            Utilities.IsEntityInsideGrid(grid))
                            continue;

                            var blocks = new List<IMySlimBlock>();
                            grid.GetBlocks(blocks);

                            Vector3D offset = Vector3D.Zero;
                            if (Config.BLOCK_ACID_RAIN)
                            {
                                Vector3D direction = grid.WorldVolume.Center - _sphere.Center;
                                direction.Normalize();
                                offset = direction;
                            }

                            if (Config.BLOCK_ACID_RAIN || Config.BLOCK_RADIATION_DAMAGE)
                                for (var i = 0; i < Math.Max(1, blocks.Count * 0.3); i++)
                                {
                                    IMySlimBlock block;
                                    if (Config.BLOCK_ACID_RAIN)
                                        block = Utilities.GetRandomSkyFacingBlock(grid, blocks, offset, true);
                                    else
                                        block = Utilities.GetRandomExteriorBlock(grid, blocks);

                                    if (block == null)
                                        continue;

                                    startingBlockPosition = block.GetPosition();
                                    endingBlockPosition = block.GetPosition();


                                    Utilities.DrawLine(block.GetPosition(),
                                        block.GetPosition() + new Vector3D(250, 150, 150), Color.DarkGreen.ToVector4());
                                }

                            continue;
                        }
                    }

                    if (Config.BLOCK_ACID_RAIN && Config.DRAW_RAIN)
                        SendDrawQueue();
                    */
