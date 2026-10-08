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

[Info("Building Workbench", "MJSU", "1.4.4")]
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

    private readonly List<ulong> _notifiedPlayer = new();
    private readonly Dictionary<ulong, PlayerData> _playerData = new();
    private readonly Dictionary<uint, BuildingData> _buildingData = new();
    private float _scanRange;
    private float _halfScanRange;
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
    }

    protected override void LoadConfig()
    {
        base.LoadConfig();
        Config.Settings.DefaultValueHandling = DefaultValueHandling.Populate;
        _pluginConfig = Config.ReadObject<PluginConfig>();
        Config.WriteObject(_pluginConfig);
    }

    private void OnServerInitialized()
    {
        _physics = Physics.defaultPhysicsScene;
        if (_pluginConfig.BaseDistance < 3f)
        {
            PrintWarning("Distance from base to be considered inside building (Meters) cannot be less than 3 meters");
            _pluginConfig.BaseDistance = 3f;
        }

        if (_pluginConfig.UpdateRate < 0.5f)
        {
            PrintWarning("Inside building check frequency cannot be less than 0.5 seconds");
            _pluginConfig.UpdateRate = 0.5f;
        }

        if (_pluginConfig.RequiredDistance < 0f)
        {
            PrintWarning("Required distance from last update cannot be negative");
            _pluginConfig.RequiredDistance = 0f;
        }

        if (_pluginConfig.StationaryRefreshRate < _pluginConfig.UpdateRate)
        {
            PrintWarning("Stationary refresh frequency cannot be less than the normal check frequency");
            _pluginConfig.StationaryRefreshRate = _pluginConfig.UpdateRate;
        }

        _scanRange = _pluginConfig.BaseDistance;
        _halfScanRange = _scanRange / 2f;
        Config.WriteObject(_pluginConfig);

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
        NextTick(() => RefreshPlayer(player));
    }

    private void OnPlayerDisconnected(BasePlayer player)
    {
        player.nextCheckTime = 0;
        player.cachedCraftLevel = 0;
        if(_playerData.Remove(player.userID, out PlayerData playerData))
        {
            Dictionary<uint, BuildingData> buildingData = playerData.Buildings;
            foreach (BuildingData data in buildingData.Values)
            {
                data.LeaveBuilding(player);
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
        if (!_updateRunning && BasePlayer.activePlayerList.Count != 0)
        {
            _updateRunning = true;
            _wb.StartCoroutine(HandleWorkbenchUpdate());
        }
    }

    public IEnumerator HandleWorkbenchUpdate()
    {
        float frameWait = 0;
        try
        {
            for (int i = 0; i < BasePlayer.activePlayerList.Count; i++)
            {
                BasePlayer player = BasePlayer.activePlayerList[i];
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
                float requiredDistanceSqr = _pluginConfig.RequiredDistance * _pluginConfig.RequiredDistance;
                bool movedEnough = !data.HasScanned || (position - data.Position).sqrMagnitude >= requiredDistanceSqr;
                bool refreshExpired = !data.HasScanned || Time.realtimeSinceStartup - data.LastScanTime >= _pluginConfig.StationaryRefreshRate;
                if (!movedEnough && !refreshExpired)
                {
                    continue;
                }

                RefreshPlayer(player, data, position);

                float waitForFrames = Performance.report.frameRate * _pluginConfig.UpdateRate / BasePlayer.activePlayerList.Count * 0.9f;
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

    private void RestoreVanillaWorkbenchHandling(BasePlayer player)
    {
        if (!_playerData.Remove(player.userID, out PlayerData data))
        {
            return;
        }

        foreach (BuildingData building in data.Buildings.Values)
        {
            building.LeaveBuilding(player);
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
        List<uint> currentBuildings = Pool.Get<List<uint>>();

        if (_pluginConfig.FastBuildingCheck)
        {
            GetNearbyAuthorizedBuildingsFast(player, currentBuildings);
        }
        else
        {
            GetNearbyAuthorizedBuildings(player, currentBuildings);
        }

        List<uint> leftBuildings = Pool.Get<List<uint>>();
        foreach (uint buildingId in data.Buildings.Keys)
        {
            if (!currentBuildings.Contains(buildingId))
            {
                leftBuildings.Add(buildingId);
            }
        }

        for (int index = 0; index < leftBuildings.Count; index++)
        {
            uint leftBuilding = leftBuildings[index];
            OnPlayerLeftBuilding(player, leftBuilding);
        }

        for (int index = 0; index < currentBuildings.Count; index++)
        {
            uint currentBuilding = currentBuildings[index];
            if (!data.Buildings.ContainsKey(currentBuilding))
            {
                OnPlayerEnterBuilding(player, currentBuilding);
            }
        }

        //Puts($"{nameof(BuildingData)}.{nameof(UpdatePlayerPriv)} {player.displayName} In: {string.Join(",", currentBuildings.Select(b => b.ToString().ToArray()))} Left: {string.Join(",", leftBuildings.Select(b => b.ToString().ToArray()))}");

        Pool.FreeUnmanaged(ref currentBuildings);
        Pool.FreeUnmanaged(ref leftBuildings);
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

            BuildingData data = GetBuildingData(bench.buildingID);
            data.OnBenchBuilt(bench);
            UpdateBuildingPlayers(data);

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

            if (_notifiedPlayer.Contains(player.userID.Get()))
            {
                return;
            }

            _notifiedPlayer.Add(player.userID);

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
        BuildingData data = GetBuildingData(privilege.buildingID);
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
        BuildingData data = GetBuildingData(boat);
        if (data == null)
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
        NextTick(() => RefreshPlayer(player));
    }

    private void OnPlayerSleepEnded(BasePlayer player)
    {
        NextTick(() => RefreshPlayer(player));
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

    private readonly RaycastHit[] _hits = new RaycastHit[256];
    private readonly HashSet<uint> _processedBuildings = new();

    public void GetNearbyAuthorizedBuildingsFast(BasePlayer player, List<uint> authorizedPrivs)
    {
        OBB obb = player.WorldSpaceBounds();
        float baseDistance = _scanRange;
        int amount = _physics.Raycast(player.transform.position + Vector3.down * _halfScanRange, Vector3.up, _hits, baseDistance, Rust.Layers.Construction, QueryTriggerInteraction.Ignore);
        for (int index = 0; index < amount; index++)
        {
            BuildingBlock block = _hits[index].transform.ToBaseEntity() as BuildingBlock;
            if (!block)
            {
                continue;
            }

            if (_processedBuildings.Contains(block.buildingID) || obb.Distance(block.WorldSpaceBounds()) > baseDistance)
            {
                continue;
            }

            _processedBuildings.Add(block.buildingID);
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
        _processedBuildings.Clear();
    }

    public void GetNearbyAuthorizedBuildings(BasePlayer player, List<uint> authorizedPrivs)
    {
        OBB obb = player.WorldSpaceBounds();
        float baseDistance = _pluginConfig.BaseDistance;
        int amount = _physics.OverlapSphere(obb.position, baseDistance + obb.extents.magnitude, Vis.colBuffer, Rust.Layers.Construction | Rust.Layers.VehiclesLarge, QueryTriggerInteraction.Ignore);
        for (int index = 0; index < amount; index++)
        {
            Collider collider = Vis.colBuffer[index];
            BuildingBlock block = collider.ToBaseEntity() as BuildingBlock;
            if (!block)
            {
                continue;
            }

            if (_processedBuildings.Contains(block.buildingID) || obb.Distance(block.WorldSpaceBounds()) > baseDistance)
            {
                continue;
            }

            _processedBuildings.Add(block.buildingID);
            if (block is BoatBuildingBlock boatBlock)
            {
                if(_pluginConfig.EnableBoatCheck && TryGetPlayerBoat(boatBlock, out PlayerBoat boat) && boat.IsAuthedForBuilding(player))
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

        _processedBuildings.Clear();
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
            _nextWorkbenchRefresh = Time.realtimeSinceStartup + 60f;
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
        public bool BuiltNotification { get; set; }

        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Display cancel craft notification")]
        public bool CancelCraftNotification { get; set; }

        [DefaultValue(3f)]
        [JsonProperty(PropertyName = "Inside building check frequency (Seconds)")]
        public float UpdateRate { get; set; }

        [DefaultValue(false)]
        [JsonProperty(PropertyName = "Enable Fast Building Check (Only checks above and below a player)")]
        public bool FastBuildingCheck { get; set; }

        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Enable Boat Check")]
        public bool EnableBoatCheck { get; set; } = true;

        [DefaultValue(16f)]
        [JsonProperty(PropertyName = "Distance from base to be considered inside building (Meters)")]
        public float BaseDistance { get; set; }

        [DefaultValue(5)]
        [JsonProperty(PropertyName = "Required distance from last update (Meters)")]
        public float RequiredDistance { get; set; }

        [DefaultValue(10f)]
        [JsonProperty(PropertyName = "Maximum time between checks while stationary (Seconds)")]
        public float StationaryRefreshRate { get; set; }
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
