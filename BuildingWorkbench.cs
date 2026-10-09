using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Building Workbench", "MJSU", "1.4.6")]
[Description("Extends the range of the workbench to work inside the entire building")]
public class BuildingWorkbench : RustPlugin
{
    #region Class Fields
    [PluginReference] private readonly Plugin GameTipAPI;

    private PluginConfig _pluginConfig;

    private WorkbenchBehavior _wb;
    private GameObject _go;
    private BuildingWorkbenchTrigger _tb;

    private const string UsePermission = "buildingworkbench.use";
    private const string CancelCraftPermission = "buildingworkbench.cancelcraft";
    private const string AccentColor = "#de8732";
    private const float WorkbenchCacheRefreshSeconds = 60f;

    private readonly HashSet<ulong> _notifiedPlayer = new();
    private readonly Dictionary<ulong, PlayerData> _playerData = new();
    private readonly Dictionary<uint, BuildingData> _buildingData = new();
    private readonly Dictionary<ulong, int> _settledRefreshGeneration = new();
    private float _scanRange;
    private float _halfScanRange;
    private float _requiredDistanceSqr;
    private bool _updateRunning;

    private PhysicsScene _physics;

    //private static BuildingWorkbench _ins;
    #endregion

    #region Setup & Loading
    private void Init()
    {
        //_ins = this;
        permission.RegisterPermission(UsePermission, this);
        permission.RegisterPermission(CancelCraftPermission, this);

        Unsubscribe(nameof(OnEntitySpawned));
        Unsubscribe(nameof(OnEntityKill));

    }

    protected override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            [LangKeys.Chat] = $"<color=#bebebe>[<color={AccentColor}>{Title}</color>] {{0}}</color>",
            [LangKeys.Notification] = "Your workbench range has been increased to work inside your building",
            [LangKeys.CraftCanceled] = "Your workbench level has changed. Crafts that required a higher level have been cancelled."
        }, this);
    }

    protected override void LoadDefaultConfig()
    {
        PrintWarning("Loading Default Config");
        _pluginConfig = new PluginConfig();
        Config.WriteObject(_pluginConfig, true);
    }

    protected override void LoadConfig()
    {
        base.LoadConfig();
        Config.Settings.DefaultValueHandling = DefaultValueHandling.Populate;
        try
        {
            _pluginConfig = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
        }
        catch (Exception exception)
        {
            PrintError($"The configuration file is invalid and has been replaced with defaults: {exception.Message}");
            LoadDefaultConfig();
            return;
        }

        Config.WriteObject(_pluginConfig, true);
    }

    private void OnServerInitialized()
    {
        _physics = Physics.defaultPhysicsScene;
        if (!IsFinite(_pluginConfig.BaseDistance) || _pluginConfig.BaseDistance < 3f)
        {
            PrintWarning("Distance from base to be considered inside building (Meters) cannot be less than 3 meters");
            _pluginConfig.BaseDistance = 3f;
        }

        if (!IsFinite(_pluginConfig.UpdateRate) || _pluginConfig.UpdateRate < 0.5f)
        {
            PrintWarning("Inside building check frequency cannot be less than 0.5 seconds");
            _pluginConfig.UpdateRate = 0.5f;
        }

        if (!IsFinite(_pluginConfig.RequiredDistance) || _pluginConfig.RequiredDistance < 0f)
        {
            PrintWarning("Required distance from last update cannot be negative");
            _pluginConfig.RequiredDistance = 0f;
        }

        if (!IsFinite(_pluginConfig.StationaryRefreshRate) || _pluginConfig.StationaryRefreshRate < _pluginConfig.UpdateRate)
        {
            PrintWarning("Stationary refresh frequency cannot be less than the normal check frequency");
            _pluginConfig.StationaryRefreshRate = _pluginConfig.UpdateRate;
        }

        _scanRange = _pluginConfig.BaseDistance;
        _halfScanRange = _scanRange / 2f;
        _requiredDistanceSqr = _pluginConfig.RequiredDistance * _pluginConfig.RequiredDistance;
        Config.WriteObject(_pluginConfig, true);

        _go = new GameObject("BuildingWorkbenchObject");
        _wb = _go.AddComponent<WorkbenchBehavior>();
        _tb = _go.AddComponent<BuildingWorkbenchTrigger>();

        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            OnPlayerConnected(player);
        }

        _wb.InvokeRepeating(StartUpdatingWorkbench, 1f, _pluginConfig.UpdateRate);

        Subscribe(nameof(OnEntitySpawned));
        Subscribe(nameof(OnEntityKill));
    }

    private void OnPlayerConnected(BasePlayer player)
    {
        if (!HasPermission(player, UsePermission))
        {
            return;
        }

        GetPlayerData(player.userID);
        player.nextCheckTime = float.MaxValue;
        player.EnterTrigger(_tb);
        ScheduleSettledRefresh(player);
    }

    private void OnPlayerDisconnected(BasePlayer player)
    {
        _settledRefreshGeneration.Remove(player.userID);
        player.nextCheckTime = 0;
        player.cachedCraftLevel = 0;
        if(_playerData.Remove(player.userID, out PlayerData playerData))
        {
            Dictionary<uint, BuildingData> buildingData = playerData.Buildings;
            foreach (BuildingData data in buildingData.Values)
            {
                data.LeaveBuilding(player);
                RemoveBuildingDataIfUnused(data);
            }
        }

        player.LeaveTrigger(_tb);
    }

    private void Unload()
    {
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            OnPlayerDisconnected(player);
        }

        if (_wb)
        {
            _wb.CancelInvoke(StartUpdatingWorkbench);
            _wb.StopAllCoroutines();
        }

        GameObject.Destroy(_go);
        //_ins = null;
    }
    #endregion

    #region Workbench Handler
    public void StartUpdatingWorkbench()
    {
        if (_wb && !_updateRunning && BasePlayer.activePlayerList.Count != 0)
        {
            _updateRunning = true;
            _wb.StartCoroutine(HandleWorkbenchUpdate());
        }
    }

    public IEnumerator HandleWorkbenchUpdate()
    {
        float frameWait = 0;
        List<BasePlayer> players = Pool.Get<List<BasePlayer>>();
        try
        {
            players.AddRange(BasePlayer.activePlayerList);
            int playerCount = players.Count;
            float frameRate = Mathf.Max(Performance.report.frameRate, 30f);
            for (int i = 0; i < playerCount; i++)
            {
                BasePlayer player = players[i];
                if (!player || !player.IsConnected)
                {
                    continue;
                }

                if (!HasPermission(player, UsePermission))
                {
                    RestoreVanillaWorkbenchHandling(player);
                    continue;
                }

                PlayerData data = GetPlayerData(player.userID);
                Vector3 position = player.transform.position;
                bool movedEnough = !data.HasScanned || (position - data.Position).sqrMagnitude >= _requiredDistanceSqr;
                bool refreshExpired = !data.HasScanned || Time.realtimeSinceStartup - data.LastScanTime >= _pluginConfig.StationaryRefreshRate;
                if (!movedEnough && !refreshExpired)
                {
                    continue;
                }

                RefreshPlayer(player, data, position);

                float waitForFrames = frameRate * _pluginConfig.UpdateRate / playerCount * 0.9f;
                if (waitForFrames >= 1)
                {
                    yield return null;
                    continue;
                }

                frameWait += waitForFrames;
                if (frameWait >= 1)
                {
                    frameWait -= 1f;
                    yield return null;
                }
            }
        }
        finally
        {
            Pool.FreeUnmanaged(ref players);
            _updateRunning = false;
        }
    }

    private void RefreshPlayer(BasePlayer player)
    {
        if (!player || !player.IsConnected || !HasPermission(player, UsePermission))
        {
            return;
        }

        RefreshPlayer(player, GetPlayerData(player.userID), player.transform.position);
    }

    private void RefreshPlayer(BasePlayer player, PlayerData data, Vector3 position)
    {
        if (player.triggers == null || !player.triggers.Contains(_tb))
        {
            player.EnterTrigger(_tb);
        }

        UpdatePlayerBuildings(player, data);
        data.Position = position;
        data.LastScanTime = Time.realtimeSinceStartup;
        data.HasScanned = true;
        UpdatePlayerWorkbenchLevel(player);
    }

    private void ScheduleSettledRefresh(BasePlayer player)
    {
        if (!player || !player.IsConnected || !HasPermission(player, UsePermission))
        {
            return;
        }

        ulong playerId = player.userID;
        int generation = _settledRefreshGeneration.TryGetValue(playerId, out int currentGeneration)
            ? currentGeneration + 1
            : 1;
        _settledRefreshGeneration[playerId] = generation;

        // Do not let a scan performed before teleport settling suppress the normal updater.
        GetPlayerData(playerId).HasScanned = false;

        ScheduleSettledRefreshAttempt(player, playerId, generation, 0.35f, false);
        ScheduleSettledRefreshAttempt(player, playerId, generation, 1f, false);
        ScheduleSettledRefreshAttempt(player, playerId, generation, 2.5f, true);
    }

    private void ScheduleSettledRefreshAttempt(BasePlayer player, ulong playerId, int generation, float delay, bool finalAttempt)
    {
        timer.Once(delay, () =>
        {
            if (!_settledRefreshGeneration.TryGetValue(playerId, out int currentGeneration) || currentGeneration != generation)
            {
                return;
            }

            if (finalAttempt)
            {
                _settledRefreshGeneration.Remove(playerId);
            }

            RefreshPlayer(player);
        });
    }

    private void RestoreVanillaWorkbenchHandling(BasePlayer player)
    {
        if (!_playerData.Remove(player.userID, out PlayerData data))
        {
            return;
        }

        foreach (BuildingData building in data.Buildings.Values)
        {
            building.LeaveBuilding(player);
            RemoveBuildingDataIfUnused(building);
        }

        player.LeaveTrigger(_tb);
        player.nextCheckTime = 0f;
        player.cachedCraftLevel = 0f;
        player._cachedWorkbench = null;
        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench1, false);
        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench2, false);
        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench3, false);
        player.SendNetworkUpdateImmediate();
        player.SendActiveWorkbenchIfChanged();
    }

    public void UpdatePlayerBuildings(BasePlayer player, PlayerData data)
    {
        HashSet<uint> currentBuildings = Pool.Get<HashSet<uint>>();
        List<uint> leftBuildings = Pool.Get<List<uint>>();
        try
        {
            if (_pluginConfig.FastBuildingCheck)
            {
                GetNearbyAuthorizedBuildingsFast(player, currentBuildings);
            }
            else
            {
                GetNearbyAuthorizedBuildings(player, currentBuildings);
            }

            foreach (uint buildingId in data.Buildings.Keys)
            {
                if (!currentBuildings.Contains(buildingId))
                {
                    leftBuildings.Add(buildingId);
                }
            }

            for (int index = 0; index < leftBuildings.Count; index++)
            {
                OnPlayerLeftBuilding(player, leftBuildings[index]);
            }

            foreach (uint buildingId in currentBuildings)
            {
                if (!data.Buildings.ContainsKey(buildingId))
                {
                    OnPlayerEnterBuilding(player, buildingId);
                }
            }
        }
        finally
        {
            Pool.FreeUnmanaged(ref currentBuildings);
            Pool.FreeUnmanaged(ref leftBuildings);
        }
    }

    public void OnPlayerEnterBuilding(BasePlayer player, uint buildingId)
    {
        Dictionary<uint, BuildingData> playerBuildings = GetPlayerData(player.userID).Buildings;
        if (playerBuildings.ContainsKey(buildingId))
        {
            return;
        }

        BuildingData building = GetBuildingData(buildingId);
        building.EnterBuilding(player);
        playerBuildings[buildingId] = building;
    }

    public void OnPlayerLeftBuilding(BasePlayer player, uint buildingId)
    {
        if (!_playerData.TryGetValue(player.userID, out PlayerData playerData) ||
            !playerData.Buildings.Remove(buildingId, out BuildingData building))
        {
            return;
        }

        building.LeaveBuilding(player);
        RemoveBuildingDataIfUnused(building);
    }
    #endregion

    #region Oxide Hooks
    private void OnEntitySpawned(Workbench bench)
    {
        //Needs to be in NextTick since other plugins can spawn Workbenches
        NextTick(() =>
        {
            if (!bench || bench.IsDestroyed || bench.buildingID == 0)
            {
                return;
            }

            if (_buildingData.TryGetValue(bench.buildingID, out BuildingData data))
            {
                data.OnBenchBuilt(bench);
                UpdateBuildingPlayers(data);
            }

            if (!_pluginConfig.BuiltNotification)
            {
                return;
            }

            BasePlayer player = BasePlayer.FindByID(bench.OwnerID);
            if (!player)
            {
                return;
            }

            if (!HasPermission(player, UsePermission))
            {
                return;
            }

            if (!_notifiedPlayer.Add(player.userID))
            {
                return;
            }

            if (GameTipAPI == null)
            {
                Chat(player, Lang(LangKeys.Notification, player));
            }
            else
            {
                GameTipAPI.Call("ShowGameTip", player, Lang(LangKeys.Notification, player), 6f);
            }
        });
    }

    private void OnEntityKill(Workbench bench)
    {
        if (!bench || bench.buildingID == 0 || !_buildingData.TryGetValue(bench.buildingID, out BuildingData data))
        {
            return;
        }

        data.OnBenchKilled(bench);
        UpdateBuildingPlayers(data);
        RemoveBuildingDataIfUnused(data);
    }

    private void OnEntityKill(BuildingPrivlidge tc)
    {
        OnCupboardClearList(tc);
    }

    private void OnEntityKill(PlayerBoatPrivilege privilege)
    {
        OnCupboardClearList(privilege);
    }

    private void OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player)
    {
        if (!HasPermission(player, UsePermission))
        {
            return;
        }

        OnPlayerEnterBuilding(player, privilege.buildingID);
        UpdatePlayerWorkbenchLevel(player);
    }

    private void OnCupboardAuthorize(PlayerBoatPrivilege privilege, BasePlayer player)
    {
        if (!_pluginConfig.EnableBoatCheck || !HasPermission(player, UsePermission))
        {
            return;
        }

        if (privilege.ParentVehicle is PlayerBoat boat)
        {
            BuildingData data = GetBuildingData(boat);
            if (data == null)
            {
                return;
            }

            OnPlayerEnterBuilding(player, data.BuildingId);
            UpdatePlayerWorkbenchLevel(player);
        }
    }

    private void OnCupboardDeauthorize(BuildingPrivlidge privilege, BasePlayer player)
    {
        OnPlayerLeftBuilding(player, privilege.buildingID);
        UpdatePlayerWorkbenchLevel(player);
    }

    private void OnCupboardDeauthorize(PlayerBoatPrivilege privilege, BasePlayer player)
    {
        if (!_pluginConfig.EnableBoatCheck)
        {
            return;
        }

        if (privilege.ParentVehicle is PlayerBoat boat)
        {
            BuildingData data = GetBuildingData(boat);
            if (data == null)
            {
                return;
            }

            OnPlayerLeftBuilding(player, data.BuildingId);
            UpdatePlayerWorkbenchLevel(player);
        }
    }

    private void OnCupboardClearList(BuildingPrivlidge privilege)
    {
        if (!_buildingData.TryGetValue(privilege.buildingID, out BuildingData data))
        {
            return;
        }

        for (int index = data.Players.Count - 1; index >= 0; index--)
        {
            BasePlayer player = data.Players[index];
            OnPlayerLeftBuilding(player, privilege.buildingID);
            UpdatePlayerWorkbenchLevel(player);
        }
    }

    private void OnCupboardClearList(PlayerBoatPrivilege privilege)
    {
        if (privilege.ParentVehicle is not PlayerBoat boat)
        {
            return;
        }
        if (!TryGetPlayerBoatBuildingId(boat, out uint buildingId) ||
            !_buildingData.TryGetValue(buildingId, out BuildingData data))
        {
            return;
        }

        for (int index = data.Players.Count - 1; index >= 0; index--)
        {
            BasePlayer player = data.Players[index];
            OnPlayerLeftBuilding(player, data.BuildingId);
            UpdatePlayerWorkbenchLevel(player);
        }
    }

    private void OnEntityEnter(TriggerWorkbench trigger, BasePlayer player)
    {
        if (!player.IsNpc)
        {
            UpdatePlayerWorkbenchLevel(player);
        }
    }

    private void OnEntityLeave(TriggerWorkbench trigger, BasePlayer player)
    {
        if (!player.IsNpc)
        {
            NextTick(() =>
            {
                UpdatePlayerWorkbenchLevel(player);
            });
        }
    }

    private void OnEntityLeave(BuildingWorkbenchTrigger trigger, BasePlayer player)
    {
        if (player.IsNpc)
        {
            return;
        }

        //_ins.Puts($"{nameof(BuildingWorkbench)}.{nameof(OnEntityLeave)} {nameof(BuildingWorkbenchTrigger)} {player.displayName}");

        NextTick(() =>
        {
            if (player && player.IsConnected && HasPermission(player, UsePermission))
            {
                player.EnterTrigger(_tb);
            }
        });
    }

    // NTeleportation calls this hook after /home and other player teleports.
    private void OnPlayerTeleported(BasePlayer player, Vector3 oldPosition, Vector3 newPosition)
    {
        ScheduleSettledRefresh(player);
    }

    private void OnPlayerSleepEnded(BasePlayer player)
    {
        ScheduleSettledRefresh(player);
    }

    private object CanCraft(ItemCrafter itemCrafter, ItemBlueprint blueprint, int amount)
    {
        BasePlayer player = itemCrafter?.owner;
        if (!player || blueprint == null || blueprint.workbenchLevelRequired <= player.cachedCraftLevel || !HasPermission(player, UsePermission))
        {
            return null;
        }

        // A craft attempt is authoritative evidence that stale workbench state matters now.
        // Refresh only when the requested blueprint needs a higher tier than the cached tier.
        RefreshPlayer(player);
        return null;
    }
    #endregion

    #region Helper Methods
    public void UpdateBuildingPlayers(BuildingData building)
    {
        for (int index = 0; index < building.Players.Count; index++)
        {
            BasePlayer player = building.Players[index];
            if (!player || !player.IsConnected || !HasPermission(player, UsePermission))
            {
                building.Players.RemoveAt(index--);
                continue;
            }

            UpdatePlayerWorkbenchLevel(player);
        }

        RemoveBuildingDataIfUnused(building);
    }

    public void UpdatePlayerWorkbenchLevel(BasePlayer player)
    {
        if (!player || !player.IsConnected || !HasPermission(player, UsePermission))
        {
            return;
        }

        byte previousLevel = (byte)player.cachedCraftLevel;
        byte level = 0;
        Workbench workbench = null;

        PlayerData playerData = GetPlayerData(player.userID);
        Dictionary<uint, BuildingData> playerBuildings = playerData.Buildings;
        if (playerBuildings != null)
        {
            foreach (BuildingData building in playerBuildings.Values)
            {
                byte buildingLevel = building.GetWorkbenchLevel();
                if (buildingLevel > level)
                {
                    level = buildingLevel;
                    workbench = building.BestWorkbench;
                }
            }
        }

        if (level != 3 && player.triggers != null)
        {
            for (int index = 0; index < player.triggers.Count; index++)
            {
                TriggerWorkbench trigger = player.triggers[index] as TriggerWorkbench;
                if (trigger && trigger.parentBench)
                {
                    byte workbenchLevel = (byte)trigger.parentBench.Workbenchlevel;
                    if (workbenchLevel > level)
                    {
                        level = workbenchLevel;
                        workbench = trigger.parentBench;
                    }
                }
            }
        }

        if ((byte)player.cachedCraftLevel == level && playerData.WorkbenchLevel == level && player._cachedWorkbench == workbench)
        {
            return;
        }

        //Puts($"{nameof(BuildingWorkbench)}.{nameof(UpdatePlayerWorkbenchLevel)} A {player.displayName} -> {level} - {workbench?.net.ID} - {player.lastSentActiveWorkbenchId}");
        player.nextCheckTime = float.MaxValue;
        player.cachedCraftLevel = level;
        player._cachedWorkbench = workbench;
        playerData.WorkbenchLevel = level;
        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench1, level == 1);
        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench2, level == 2);
        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench3, level == 3);
        player.SendNetworkUpdateImmediate();
        player.SendActiveWorkbenchIfChanged();

        if (level < previousLevel)
        {
            CancelInvalidCrafts(player, level);
        }
    }

    private void CancelInvalidCrafts(BasePlayer player, byte level)
    {
        if (!HasPermission(player, CancelCraftPermission) || player.inventory?.crafting?.queue == null)
        {
            return;
        }

        bool canceled = false;
        LinkedListNode<ItemCraftTask> node = player.inventory.crafting.queue.Last;
        while (node != null)
        {
            ItemCraftTask task = node.Value;
            node = node.Previous;
            if (task?.blueprint != null && level < task.blueprint.workbenchLevelRequired)
            {
                player.inventory.crafting.CancelTask(task.taskUID);
                canceled = true;
            }
        }

        if (canceled && _pluginConfig.CancelCraftNotification)
        {
            Chat(player, Lang(LangKeys.CraftCanceled, player));
        }
    }

    public bool TryGetPlayerBoat(BaseEntity entity, out PlayerBoat boat)
    {
        boat = PlayerBoat.GetParentPlayerBoat(entity);
        return boat;
    }

    public bool TryGetPlayerBoatBuildingId(PlayerBoat boat, out uint buildingId)
    {
        if (boat && boat.BoatBuildingBlocks.Cached.Count != 0)
        {
            buildingId = boat.BoatBuildingBlocks.Cached[0].buildingID;
            return true;
        }

        buildingId = 0;
        return false;
    }

    public PlayerData GetPlayerData(ulong playerId)
    {
        if (!_playerData.TryGetValue(playerId, out PlayerData data))
        {
            _playerData[playerId] = data = new PlayerData();
        }

        return data;
    }

    public BuildingData GetBuildingData(uint buildingId)
    {
        if (!_buildingData.TryGetValue(buildingId, out BuildingData data))
        {
            _buildingData[buildingId] = data = new BuildingData(buildingId);
        }

        return data;
    }

    public BuildingData GetBuildingData(PlayerBoat boat)
    {
        return TryGetPlayerBoatBuildingId(boat, out uint buildingId) ? GetBuildingData(buildingId) : null;
    }

    private void RemoveBuildingDataIfUnused(BuildingData building)
    {
        if (building == null || building.Players.Count != 0)
        {
            return;
        }

        if (_buildingData.TryGetValue(building.BuildingId, out BuildingData cached) && ReferenceEquals(cached, building))
        {
            _buildingData.Remove(building.BuildingId);
        }
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private readonly RaycastHit[] _hits = new RaycastHit[256];

    public void GetNearbyAuthorizedBuildingsFast(BasePlayer player, HashSet<uint> authorizedPrivs)
    {
        AddCurrentAuthorizedBuilding(player, authorizedPrivs);
        HashSet<uint> processedBuildings = Pool.Get<HashSet<uint>>();
        try
        {
            OBB obb = player.WorldSpaceBounds();
            int amount = _physics.Raycast(player.transform.position + Vector3.down * _halfScanRange, Vector3.up, _hits, _scanRange, Rust.Layers.Construction, QueryTriggerInteraction.Ignore);
            for (int index = 0; index < amount; index++)
            {
                BuildingBlock block = _hits[index].transform.ToBaseEntity() as BuildingBlock;
                if (!block || !processedBuildings.Add(block.buildingID) || obb.Distance(block.WorldSpaceBounds()) > _scanRange)
                {
                    continue;
                }

                if (block is BoatBuildingBlock boatBlock)
                {
                    if (_pluginConfig.EnableBoatCheck && TryGetPlayerBoat(boatBlock, out PlayerBoat boat) && boat.IsAuthedForBuilding(player))
                    {
                        authorizedPrivs.Add(boatBlock.buildingID);
                    }
                }
                else
                {
                    BuildingPrivlidge priv = block.GetBuilding()?.GetDominatingBuildingPrivilege();
                    if (priv && priv.IsAuthed(player))
                    {
                        authorizedPrivs.Add(priv.buildingID);
                    }
                }
            }
        }
        finally
        {
            Pool.FreeUnmanaged(ref processedBuildings);
        }
    }

    public void GetNearbyAuthorizedBuildings(BasePlayer player, HashSet<uint> authorizedPrivs)
    {
        AddCurrentAuthorizedBuilding(player, authorizedPrivs);
        HashSet<uint> processedBuildings = Pool.Get<HashSet<uint>>();
        try
        {
            OBB obb = player.WorldSpaceBounds();
            int amount = _physics.OverlapSphere(obb.position, _scanRange + obb.extents.magnitude, Vis.colBuffer, Rust.Layers.Construction | Rust.Layers.VehiclesLarge, QueryTriggerInteraction.Ignore);
            for (int index = 0; index < amount; index++)
            {
                BuildingBlock block = Vis.colBuffer[index].ToBaseEntity() as BuildingBlock;
                if (!block || !processedBuildings.Add(block.buildingID) || obb.Distance(block.WorldSpaceBounds()) > _scanRange)
                {
                    continue;
                }

                if (block is BoatBuildingBlock boatBlock)
                {
                    if (_pluginConfig.EnableBoatCheck && TryGetPlayerBoat(boatBlock, out PlayerBoat boat) && boat.IsAuthedForBuilding(player))
                    {
                        authorizedPrivs.Add(boatBlock.buildingID);
                    }
                }
                else
                {
                    BuildingPrivlidge priv = block.GetBuilding()?.GetDominatingBuildingPrivilege();
                    if (priv && priv.IsAuthed(player))
                    {
                        authorizedPrivs.Add(priv.buildingID);
                    }
                }
            }
        }
        finally
        {
            Pool.FreeUnmanaged(ref processedBuildings);
        }
    }

    private static void AddCurrentAuthorizedBuilding(BasePlayer player, HashSet<uint> authorizedBuildings)
    {
        BuildingPrivlidge privilege = player.GetBuildingPrivilege();
        if (privilege && privilege.IsAuthed(player) && privilege.buildingID != 0)
        {
            authorizedBuildings.Add(privilege.buildingID);
        }
    }

    public void Chat(BasePlayer player, string message) => PrintToChat(player, Lang(LangKeys.Chat, player, message));

    public bool HasPermission(BasePlayer player, string perm) => permission.UserHasPermission(player.UserIDString, perm);

    private string Lang(string key, BasePlayer player = null)
    {
        return lang.GetMessage(key, this, player?.UserIDString);
    }

    private string Lang(string key, BasePlayer player = null, params object[] args)
    {
        try
        {
            return string.Format(Lang(key, player), args);
        }
        catch (Exception ex)
        {
            PrintError($"Lang Key '{key}' threw exception\n:{ex}");
            throw;
        }
    }
    #endregion

    #region Building Data
    public class BuildingData
    {
        public readonly uint BuildingId;
        public Workbench BestWorkbench { get; protected set; }
        public readonly List<BasePlayer> Players = new();
        public List<Workbench> Workbenches { get; protected set; }
        private float _nextWorkbenchRefresh;

        public BuildingData(uint buildingId)
        {
            BuildingId = buildingId;
            Workbenches = BuildingManager.server.GetBuilding(buildingId)?.decayEntities.OfType<Workbench>().ToList() ?? new List<Workbench>();
            UpdateBestBench();
            _nextWorkbenchRefresh = Time.realtimeSinceStartup + WorkbenchCacheRefreshSeconds;
        }

        public void EnterBuilding(BasePlayer player)
        {
            //_ins.Puts($"{nameof(BuildingData)}.{nameof(EnterBuilding)} {player.displayName} {GetWorkbenchLevel()}");
            if (!Players.Contains(player))
            {
                Players.Add(player);
            }
        }

        public void LeaveBuilding(BasePlayer player)
        {
            //_ins.Puts($"{nameof(BuildingData)}.{nameof(LeaveBuilding)} {player.displayName} {GetWorkbenchLevel()}");
            while (Players.Remove(player))
            {
            }
        }

        public void OnBenchBuilt(Workbench workbench)
        {
            if (workbench && !WorkbenchExists(workbench))
            {
                Workbenches.Add(workbench);
            }

            UpdateBestBench();
        }

        public void OnBenchKilled(Workbench workbench)
        {
            Workbenches.Remove(workbench);
            UpdateBestBench();
        }

        public byte GetWorkbenchLevel()
        {
            if (!BestWorkbench || Time.realtimeSinceStartup >= _nextWorkbenchRefresh)
            {
                RefreshWorkbenches();
            }

            return BestWorkbench ? (byte)BestWorkbench.Workbenchlevel : (byte)0;
        }

        private bool WorkbenchExists(Workbench workbench)
        {
            for (int index = 0; index < Workbenches.Count; index++)
            {
                if (Workbenches[index] == workbench)
                {
                    return true;
                }
            }

            return false;
        }

        private void RefreshWorkbenches()
        {
            _nextWorkbenchRefresh = Time.realtimeSinceStartup + WorkbenchCacheRefreshSeconds;
            var building = BuildingManager.server.GetBuilding(BuildingId);
            Workbenches.Clear();
            if (building != null)
            {
                foreach (Workbench workbench in building.decayEntities.OfType<Workbench>())
                {
                    if (workbench && !workbench.IsDestroyed)
                    {
                        Workbenches.Add(workbench);
                    }
                }
            }

            UpdateBestBench();
        }

        private void UpdateBestBench()
        {
            BestWorkbench = null;
            for (int index = 0; index < Workbenches.Count; index++)
            {
                Workbench workbench = Workbenches[index];
                if (!workbench || workbench.IsDestroyed)
                {
                    Workbenches.RemoveAt(index--);
                    continue;
                }

                if (!BestWorkbench || BestWorkbench.Workbenchlevel < workbench.Workbenchlevel)
                {
                    BestWorkbench = workbench;
                }
            }
        }
    }
    #endregion

    #region Classes
    private class PluginConfig
    {
        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Display workbench built notification")]
        public bool BuiltNotification { get; set; } = true;

        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Display cancel craft notification")]
        public bool CancelCraftNotification { get; set; } = true;

        [DefaultValue(3f)]
        [JsonProperty(PropertyName = "Inside building check frequency (Seconds)")]
        public float UpdateRate { get; set; } = 3f;

        [DefaultValue(false)]
        [JsonProperty(PropertyName = "Enable Fast Building Check (Only checks above and below a player)")]
        public bool FastBuildingCheck { get; set; }

        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Enable Boat Check")]
        public bool EnableBoatCheck { get; set; } = true;

        [DefaultValue(16f)]
        [JsonProperty(PropertyName = "Distance from base to be considered inside building (Meters)")]
        public float BaseDistance { get; set; } = 16f;

        [DefaultValue(5f)]
        [JsonProperty(PropertyName = "Required distance from last update (Meters)")]
        public float RequiredDistance { get; set; } = 5f;

        [DefaultValue(10f)]
        [JsonProperty(PropertyName = "Maximum time between checks while stationary (Seconds)")]
        public float StationaryRefreshRate { get; set; } = 10f;
    }

    public class PlayerData
    {
        public Vector3 Position { get; set; }
        public Dictionary<uint, BuildingData> Buildings { get; } = new();
        public byte WorkbenchLevel { get; set; }
        public float LastScanTime { get; set; }
        public bool HasScanned { get; set; }
    }

    private class LangKeys
    {
        public const string Chat = nameof(Chat);
        public const string Notification = nameof(Notification);
        public const string CraftCanceled = nameof(CraftCanceled) + "V1";
    }

    public class WorkbenchBehavior : FacepunchBehaviour
    {

    }

    public class BuildingWorkbenchTrigger : TriggerBase
    {

    }
    #endregion
}
