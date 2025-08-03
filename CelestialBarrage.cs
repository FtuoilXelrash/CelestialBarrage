using System.Collections.Generic;
using Oxide.Core.Plugins;
using Oxide.Core;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("CelestialBarrage", "Ftuoil Xelrash", "0.0.80")]
    [Description("Simulate a meteor strike using rockets falling from the sky")]
    class CelestialBarrage : RustPlugin
    {
        #region Fields
        [PluginReference]
        Plugin PopupNotifications;

        private Timer EventTimer = null;
        private List<Timer> RocketTimers = new List<Timer>();
        private HashSet<BaseEntity> meteorRockets = new HashSet<BaseEntity>(); // Track our meteor rockets
        private List<MapMarkerGenericRadius> activeMarkers = new List<MapMarkerGenericRadius>(); // Track map markers
        private List<VendingMachineMapMarker> activeVendingMarkers = new List<VendingMachineMapMarker>(); // Track vending markers for hover text
        #endregion

        #region Oxide Hooks       
        private void OnServerInitialized()
        {
            lang.RegisterMessages(Messages, this);
            LoadVariables();
            StartEventTimer();
        }  
        
        private void Unload()
        {
            StopTimer();
            foreach (var t in RocketTimers)
                t.Destroy();
            var objects = UnityEngine.Object.FindObjectsOfType<ItemCarrier>();
            if (objects != null)
                foreach (var gameObj in objects)
                    UnityEngine.Object.Destroy(gameObj);
            
            // Clean up any remaining map markers
            CleanupAllMapMarkers();
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            // Check if damage is from one of our meteor rockets
            if (info?.Initiator != null && meteorRockets.Contains(info.Initiator))
            {
                LogMeteorImpact(entity, info);
            }
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            // Clean up tracking when our rockets explode/die
            if (entity != null && meteorRockets.Contains(entity))
            {
                meteorRockets.Remove(entity);
            }
        }
        #endregion

        #region Functions
        private void StartEventTimer()
        {
            if (configData.Options.EnableAutomaticEvents)
            {
                if (configData.Options.EventTimers.UseRandomTimer)
                {
                    var random = RandomRange(configData.Options.EventTimers.RandomTimerMin, configData.Options.EventTimers.RandomTimerMax);
                    EventTimer = timer.Once(random * 60, () => { StartRandomOnMap(); StartEventTimer(); });
                }
                else EventTimer = timer.Repeat(configData.Options.EventTimers.EventInterval * 60, 0, () => StartRandomOnMap());
            }
        }
        private void StopTimer()
        {
            if (EventTimer != null)
                EventTimer.Destroy();
        }

        private void StartRandomOnMap()
        {
            // Check minimum player requirement
            if (configData?.Options?.MinimumPlayerCount != null && BasePlayer.activePlayerList.Count < configData.Options.MinimumPlayerCount)
            {
                Puts($"METEOR SHOWER SKIPPED: Not enough players online ({BasePlayer.activePlayerList.Count} < {configData.Options.MinimumPlayerCount})");
                return; 
            }

            // Check performance monitoring
            if (configData?.Options?.PerformanceMonitoring?.EnableFPSCheck == true)
            {
                float currentFPS = 1f / UnityEngine.Time.unscaledDeltaTime;
                if (currentFPS < configData.Options.PerformanceMonitoring.MinimumFPS)
                {
                    Puts($"METEOR SHOWER CANCELLED: Low FPS detected ({currentFPS:F1} < {configData.Options.PerformanceMonitoring.MinimumFPS})");
                    return;
                }
            }

            float mapsize = (TerrainMeta.Size.x / 2) - 600f;

            float randomX = UnityEngine.Random.Range(-mapsize, mapsize);
            float randomY = UnityEngine.Random.Range(-mapsize, mapsize);

            Vector3 callAt = new Vector3(randomX, 0f, randomY);

            // Randomly select intensity for automatic events
            // 50% Optimal, 30% Mild, 20% Extreme
            ConfigData.Settings selectedSetting;
            int randomIntensity = UnityEngine.Random.Range(1, 101);
            
            if (randomIntensity <= 50)
                selectedSetting = configData.z_IntensitySettings.Settings_Optimal;
            else if (randomIntensity <= 80)
                selectedSetting = configData.z_IntensitySettings.Settings_Mild;
            else
                selectedSetting = configData.z_IntensitySettings.Settings_Extreme;

            StartRainOfFire(callAt, selectedSetting, "Automatic Event");
        }
        private bool StartOnPlayer(string playerName, ConfigData.Settings setting, string eventType)
        {
            BasePlayer player = GetPlayerByName(playerName);

            if (player == null)
                return false;

            StartRainOfFire(player.transform.position, setting, eventType);
            return true;
        }
        private void StartBarrage(Vector3 origin, Vector3 direction) => timer.Repeat(configData.BarrageSettings.RocketDelay, configData.BarrageSettings.NumberOfRockets, () => SpreadRocket(origin, direction));

        private void StartRainOfFire(Vector3 origin, ConfigData.Settings setting, string eventType = "Manual")
        {
            float radius = setting.Radius;
            int numberOfRockets = setting.RocketAmount;
            float duration = setting.Duration;
            bool dropsItems = setting.ItemDropControl.EnableItemDrop;
            ItemDrop[] itemDrops = setting.ItemDropControl.ItemsToDrop;

            float intervals = duration / numberOfRockets;

            // Determine intensity level by comparing settings objects
            string intensity = "Unknown";
            if (ReferenceEquals(setting, configData.z_IntensitySettings.Settings_Mild))
                intensity = "Mild";
            else if (ReferenceEquals(setting, configData.z_IntensitySettings.Settings_Optimal))
                intensity = "Optimal";
            else if (ReferenceEquals(setting, configData.z_IntensitySettings.Settings_Extreme))
                intensity = "Extreme";

            // Log meteor shower start to console
            string gridRef = GetGridReference(origin);
            Vector3 groundPos = GetGroundPosition(origin);
            string teleportCmd = $"teleportpos {groundPos.x:F1} {groundPos.y:F1} {groundPos.z:F1}";

            // Show warning countdown if enabled
            if (configData.Options.WarningCountdown.EnableWarning)
            {
                if (configData.Options.NotifyEvent)
                {
                    if (PopupNotifications)
                        PopupNotifications.Call("CreatePopupNotification", $"Meteor shower incoming in {configData.Options.WarningCountdown.CountdownSeconds} seconds!");
                    else PrintToChat($"Meteor shower incoming in {configData.Options.WarningCountdown.CountdownSeconds} seconds!");
                }

                // Wait for countdown before starting
                timer.Once(configData.Options.WarningCountdown.CountdownSeconds, () => {
                    StartMeteorShowerWithEffects(origin, setting, eventType, intensity, gridRef, teleportCmd, intervals, numberOfRockets, duration, radius);
                });
            }
            else
            {
                // Start immediately without countdown
                StartMeteorShowerWithEffects(origin, setting, eventType, intensity, gridRef, teleportCmd, intervals, numberOfRockets, duration, radius);
            }
        }

        private void StartMeteorShowerWithEffects(Vector3 origin, ConfigData.Settings setting, string eventType, string intensity, string gridRef, string teleportCmd, float intervals, int numberOfRockets, float duration, float radius)
        {
            Puts($"========== METEOR SHOWER STARTED ==========");
            Puts($"Type: {eventType} ({intensity})");
            Puts($"Location: ({origin.x:F0}, {origin.z:F0}) Grid: {gridRef}");
            Puts($"Stats: {numberOfRockets} rockets, {duration}s duration, {radius}m radius");
            Puts($"Teleport: {teleportCmd}");
            Puts($"==========================================");

            // Create map marker if enabled
            if (configData.Options.MapMarkers.EnableMapMarkers)
            {
                CreateMapMarker(origin, intensity, duration);
            }

            // Call hook for rServerMessages to detect meteor shower
            Interface.CallHook("OnMeteorShowerStarted");

            timer.Repeat(intervals, numberOfRockets, () => RandomRocket(origin, radius, setting));
            
            // Schedule the end event hook to fire after all rockets have been spawned
            timer.Once(duration, () => {
                Puts($"========== METEOR SHOWER ENDED ===========");
                Puts($"Type: {eventType} ({intensity})");
                Puts($"Location: ({origin.x:F0}, {origin.z:F0}) Grid: {gridRef}");
                Puts($"Teleport: {teleportCmd}");
                Puts($"==========================================");
                
                // Remove map marker if it exists
                // Markers auto-remove via timer, no manual cleanup needed
                
                Interface.CallHook("OnMeteorShowerEnded");
            });
        }      

        private void RandomRocket(Vector3 origin, float radius, ConfigData.Settings setting)
        {
            bool isFireRocket = false;
            Vector2 rand = UnityEngine.Random.insideUnitCircle;
            Vector3 offset = new Vector3(rand.x * radius, 0, rand.y * radius);

            Vector3 direction = (Vector3.up * -2.0f + Vector3.right).normalized;
            Vector3 launchPos = origin + offset - direction * 200;

            if (RandomRange(1, setting.FireRocketChance) == 1)
                isFireRocket = true;

            BaseEntity rocket = CreateRocket(launchPos, direction, isFireRocket);
            if (setting.ItemDropControl.EnableItemDrop)
            {
                var comp = rocket.gameObject.AddComponent<ItemCarrier>();
                comp.SetCarriedItems(setting.ItemDropControl.ItemsToDrop);
                comp.SetDropMultiplier(configData.Options.GlobalDropMultiplier);
            }
        }

        private void SpreadRocket(Vector3 origin, Vector3 direction)
        {
            var barrageSpread = configData.BarrageSettings.RocketSpread;
            direction = Quaternion.Euler(UnityEngine.Random.Range((float)(-(double)barrageSpread * 0.5), barrageSpread * 0.5f), UnityEngine.Random.Range((float)(-(double)barrageSpread * 0.5), barrageSpread * 0.5f), UnityEngine.Random.Range((float)(-(double)barrageSpread * 0.5), barrageSpread * 0.5f)) * direction;
            CreateRocket(origin, direction, false);
        }

        private BaseEntity CreateRocket(Vector3 startPoint, Vector3 direction, bool isFireRocket)
        {
            ItemDefinition projectileItem;
         
            if (isFireRocket)
                projectileItem = ItemManager.FindItemDefinition("ammo.rocket.fire");
            else projectileItem = ItemManager.FindItemDefinition("ammo.rocket.basic");

            ItemModProjectile component = projectileItem.GetComponent<ItemModProjectile>();
            BaseEntity entity = GameManager.server.CreateEntity(component.projectileObject.resourcePath, startPoint, new Quaternion(), true);

            TimedExplosive timedExplosive = entity.GetComponent<TimedExplosive>();
            ServerProjectile serverProjectile = entity.GetComponent<ServerProjectile>();

            serverProjectile.gravityModifier = 0;
            serverProjectile.speed = 25;
            timedExplosive.timerAmountMin = 300;
            timedExplosive.timerAmountMax = 300;
            ScaleAllDamage(timedExplosive.damageTypes, configData.DamageControl.DamageMultiplier);

            // Add visual effects if enabled (simplified approach)
            if (configData.Options.VisualEffects.EnableParticleTrails)
            {
                AddVisualTrail(entity);
            }

            serverProjectile.InitializeVelocity(direction.normalized * 25);
            entity.Spawn();
            
            // Track this rocket as one of ours
            meteorRockets.Add(entity);
            
            return entity;
        }

        private void AddVisualTrail(BaseEntity rocket)
        {
            // Create a simple visual effect using existing Rust effects
            // This spawns a fire effect that follows the rocket
            timer.Repeat(0.1f, 30, () => {
                if (rocket != null && !rocket.IsDestroyed)
                {
                    // Spawn small fire effect at rocket position for visual trail
                    Effect.server.Run("assets/bundled/prefabs/fx/explosions/explosion_01.prefab", rocket.transform.position, Vector3.up, null, false);
                }
            });
        }

        private void ScaleAllDamage(List<DamageTypeEntry> damageTypes, float scale)
        {
            for (int i = 0; i < damageTypes.Count; i++)
            {
                damageTypes[i].amount *= scale;
            }
        }

        private void LogMeteorImpact(BaseCombatEntity entity, HitInfo info)
        {
            string entityType = "Unknown";
            string ownerInfo = "";
            bool isPlayerStructure = false;
            
            // Determine what was hit
            if (entity is BasePlayer)
            {
                var player = entity as BasePlayer;
                entityType = "Player";
                ownerInfo = $" ({player.displayName})";
                
                // Screen shake effect for the hit player if enabled
                if (configData.Options.VisualEffects.EnableScreenShake)
                {
                    ApplyScreenShake(player, 1.0f);
                }
            }
            else if (entity.OwnerID != 0)
            {
                // Player-owned structure
                isPlayerStructure = true;
                var ownerPlayer = BasePlayer.FindByID(entity.OwnerID);
                entityType = entity.ShortPrefabName;
                ownerInfo = ownerPlayer != null ? $" (Owner: {ownerPlayer.displayName})" : $" (OwnerID: {entity.OwnerID})";
                
                // Screen shake for nearby players if enabled
                if (configData.Options.VisualEffects.EnableScreenShake)
                {
                    ApplyScreenShakeToNearbyPlayers(entity.transform.position, 50f, 0.5f);
                }
            }
            else
            {
                // Natural/NPC entity
                entityType = entity.ShortPrefabName;
            }

            // Only log and fire hook for players or player structures
            if (entity is BasePlayer || isPlayerStructure)
            {
                string damageInfo = $"Damage: {info.damageTypes.Total():F1}";
                Vector3 pos = entity.transform.position;
                string posInfo = $"Position: {pos.x:F0}, {pos.y:F0}, {pos.z:F0}";
                
                Puts($"METEOR IMPACT: {entityType}{ownerInfo} | {damageInfo} | {posInfo}");
                
                // Fire the hook for other plugins
                Interface.CallHook("OnMeteorImpact", entity, info, entityType, ownerInfo);
            }
        }

        private void ApplyScreenShake(BasePlayer player, float intensity)
        {
            // Send screen shake effect to player
            player.SendConsoleCommand("client.shake", intensity);
        }

        private void ApplyScreenShakeToNearbyPlayers(Vector3 position, float radius, float intensity)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (Vector3.Distance(player.transform.position, position) <= radius)
                {
                    ApplyScreenShake(player, intensity);
                }
            }
        }

        private void CreateMapMarker(Vector3 position, string intensity, float duration)
        {
            if (!configData.Options.MapMarkers.EnableMapMarkers)
                return;

            try
            {
                string gridRef = GetGridReference(position);
                
                // Create a colored circle marker FIRST (like RaidableBases/Sputnik)
                var radiusMarker = GameManager.server.CreateEntity("assets/prefabs/tools/map/genericradiusmarker.prefab", position) as MapMarkerGenericRadius;
                
                if (radiusMarker != null)
                {
                    radiusMarker.enableSaving = false;
                    radiusMarker.Spawn(); // SPAWN FIRST!
                    
                    // Set circle color and size based on intensity AFTER spawning
                    switch (intensity.ToLower())
                    {
                        case "mild":
                            // Green circle
                            radiusMarker.color1 = new Color(0f, 1f, 0f, 1f);
                            radiusMarker.color2 = new Color(0f, 1f, 0f, 1f);
                            radiusMarker.radius = 0.15f;
                            radiusMarker.alpha = 0.5f;
                            break;
                        case "extreme":
                            // Red circle
                            radiusMarker.color1 = new Color(1f, 0f, 0f, 1f);
                            radiusMarker.color2 = new Color(1f, 0f, 0f, 1f);
                            radiusMarker.radius = 0.2f;
                            radiusMarker.alpha = 0.6f;
                            break;
                        case "optimal":
                        default:
                            // Yellow circle
                            radiusMarker.color1 = new Color(1f, 1f, 0f, 1f);
                            radiusMarker.color2 = new Color(1f, 1f, 0f, 1f);
                            radiusMarker.radius = 0.175f;
                            radiusMarker.alpha = 0.55f;
                            break;
                    }
                    
                    // Send updates AFTER setting properties (like Sputnik does)
                    radiusMarker.SendUpdate();
                    radiusMarker.SendNetworkUpdate();
                    
                    // Store marker for cleanup
                    activeMarkers.Add(radiusMarker);
                }
                
                // Create a vending machine marker for the hover text
                var vendingMarker = GameManager.server.CreateEntity("assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab", position) as VendingMachineMapMarker;
                
                if (vendingMarker != null)
                {
                    string markerText = "";
                    switch (intensity.ToLower())
                    {
                        case "mild":
                            markerText = $"Meteor Shower (Mild) - {gridRef}";
                            break;
                        case "extreme":
                            markerText = $"Meteor Storm (Extreme) - {gridRef}";
                            break;
                        case "optimal":
                        default:
                            markerText = $"Meteor Shower (Optimal) - {gridRef}";
                            break;
                    }
                    
                    vendingMarker.enableSaving = false;
                    vendingMarker.Spawn();
                    vendingMarker.markerShopName = markerText;
                    vendingMarker.SendNetworkUpdate();
                    
                    // Store for cleanup
                    activeVendingMarkers.Add(vendingMarker);
                }

                // Schedule marker removal after duration
                timer.Once(duration + 5f, () => {
                    if (radiusMarker != null && !radiusMarker.IsDestroyed)
                    {
                        activeMarkers.Remove(radiusMarker);
                        radiusMarker.Kill();
                    }
                    if (vendingMarker != null && !vendingMarker.IsDestroyed)
                    {
                        activeVendingMarkers.Remove(vendingMarker);
                        vendingMarker.Kill();
                    }
                });

                // Also broadcast location info to all players
                foreach (var player in BasePlayer.activePlayerList)
                {
                    switch (intensity.ToLower())
                    {
                        case "mild":
                            player.ChatMessage($"<color=#00FF00>● METEOR SHOWER (Mild)</color> incoming at grid <color=#FFFFFF>{gridRef}</color>");
                            break;
                        case "extreme":
                            player.ChatMessage($"<color=#FF0000>● METEOR STORM (Extreme)</color> incoming at grid <color=#FFFFFF>{gridRef}</color>");
                            break;
                        case "optimal":
                        default:
                            player.ChatMessage($"<color=#FFFF00>● METEOR SHOWER (Optimal)</color> incoming at grid <color=#FFFFFF>{gridRef}</color>");
                            break;
                    }
                }

                Puts($"Created map markers at {position} ({gridRef}) for {intensity} intensity meteor shower");
            }
            catch (System.Exception ex)
            {
                Puts($"Error creating map marker: {ex.Message}");
            }
        }

        private void RemoveMapMarker(Vector3 position)
        {
            // This method is kept for compatibility but markers auto-remove via timer
        }

        private void CleanupAllMapMarkers()
        {
            foreach (var marker in activeMarkers)
            {
                if (marker != null && !marker.IsDestroyed)
                {
                    marker.Kill();
                }
            }
            activeMarkers.Clear();
            
            foreach (var vendingMarker in activeVendingMarkers)
            {
                if (vendingMarker != null && !vendingMarker.IsDestroyed)
                {
                    vendingMarker.Kill();
                }
            }
            activeVendingMarkers.Clear();
            
            Puts("Cleaned up all map markers");
        }
        #endregion

        #region Config editing
        private void SetIntervals(int intervals)
        {
            StopTimer();

            configData.Options.EventTimers.EventInterval = intervals;
            SaveConfig(configData);

            StartEventTimer();
        }
        private void SetDamageMult(float scale)
        {
            configData.DamageControl.DamageMultiplier = scale;
            SaveConfig(configData);
        }
        private void SetNotifyEvent(bool notify)
        {
            configData.Options.NotifyEvent = notify;
            SaveConfig(configData);
        }
        private void SetDropRate(float rate)
        {
            configData.Options.GlobalDropMultiplier = rate;
            SaveConfig(configData);
        }
        #endregion

        #region Commands
        [ChatCommand("cb")]
        private void cmdCB(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin || args.Length == 0)
            {
                SendReply(player, msg("help1", player.UserIDString));
                SendReply(player, msg("help2", player.UserIDString));
                SendReply(player, msg("help3", player.UserIDString));
                SendReply(player, msg("help4", player.UserIDString));
                SendReply(player, msg("help5", player.UserIDString));
                SendReply(player, msg("help6", player.UserIDString));
                SendReply(player, msg("help7", player.UserIDString));
                SendReply(player, msg("help8", player.UserIDString));
                return;
            }
                

            switch (args[0].ToLower())
            {
                case "onplayer":
                    if (args.Length == 2)
                    {
                        if (StartOnPlayer(args[1], configData.z_IntensitySettings.Settings_Optimal, "Admin on Player"))
                            SendReply(player, string.Format(msg("calledOn", player.UserIDString), args[1]));
                        else
                            SendReply(player, msg("noPlayer", player.UserIDString));
                    }
                    else
                    {
                        StartRainOfFire(player.transform.position, configData.z_IntensitySettings.Settings_Optimal, "Admin on Position");
                        SendReply(player, msg("onPos", player.UserIDString));
                    }
                    break;

                case "onplayer_extreme":
                    if (args.Length == 2)
                    {
                        if (StartOnPlayer(args[1], configData.z_IntensitySettings.Settings_Extreme, "Admin on Player"))
                            SendReply(player, msg("Extreme", player.UserIDString) + string.Format(msg("calledOn", player.UserIDString), args[1]));
                        else
                            SendReply(player, msg("noPlayer", player.UserIDString));
                    }
                    else
                    {
                        StartRainOfFire(player.transform.position, configData.z_IntensitySettings.Settings_Extreme, "Admin on Position");
                        SendReply(player, msg("Extreme", player.UserIDString) + msg("onPos", player.UserIDString));
                    }
                    break;

                case "onplayer_mild":
                    if (args.Length == 2)
                    {
                        if (StartOnPlayer(args[1], configData.z_IntensitySettings.Settings_Mild, "Admin on Player"))
                            SendReply(player, msg("Mild", player.UserIDString) + string.Format(msg("calledOn", player.UserIDString), args[1]));
                        else
                            SendReply(player, msg("noPlayer", player.UserIDString));
                    }
                    else
                    {
                        StartRainOfFire(player.transform.position, configData.z_IntensitySettings.Settings_Mild, "Admin on Position");
                        SendReply(player, msg("Mild", player.UserIDString) + msg("onPos", player.UserIDString));
                    }
                    break;

                case "barrage":
                    StartBarrage(player.eyes.position + player.eyes.HeadForward() * 1f, player.eyes.HeadForward());
                    break;

                case "random":
                    StartRandomOnMap();
                    SendReply(player, msg("randomCall", player.UserIDString));
                    break;

                case "intervals":
                    if (args.Length > 1)
                    {
                        int newIntervals;
                        bool isValid;
                        isValid = int.TryParse(args[1], out newIntervals);

                        if (isValid)
                        {
                            if (newIntervals >= 4 || newIntervals == 0)
                            {
                                SetIntervals(newIntervals);
                                SendReply(player, string.Format(msg("interSet", player.UserIDString), newIntervals));
                                StopTimer();
                                StartEventTimer();
                            }
                            else
                            {
                                SendReply(player, msg("smallInter", player.UserIDString));
                            }
                        }
                        else
                        {
                            SendReply(player, string.Format(msg("invalidParam", player.UserIDString), args[1]));
                        }
                    }
                    break;
                case "droprate":
                    if (args.Length > 1)
                    {
                        float newDropMultiplier;
                        bool isValid;
                        isValid = float.TryParse(args[1], out newDropMultiplier);
                        if (isValid)
                        {
                            SetDropRate(newDropMultiplier);
                            SendReply(player, msg("dropMulti", player.UserIDString) + newDropMultiplier);
                        }
                        else
                        {
                            SendReply(player, string.Format(msg("invalidParam", player.UserIDString), args[1]));
                        }
                    }
                    break;
                case "damagescale":
                    if (args.Length > 1)
                    {
                        float newDamageMultiplier;
                        bool isValid;
                        isValid = float.TryParse(args[1], out newDamageMultiplier);

                        if (isValid)
                        {
                            SetDamageMult(newDamageMultiplier);
                            SendReply(player, msg("damageMulti", player.UserIDString) + newDamageMultiplier);
                        }
                        else
                        {
                            SendReply(player, string.Format(msg("invalidParam", player.UserIDString), args[1]));
                        }
                    }
                    break;

                case "togglemsg":
                    if (configData.Options.NotifyEvent)
                    {
                        SetNotifyEvent(false);
                        SendReply(player, msg("notifDeAct", player.UserIDString));
                    }
                    else
                    {
                        SetNotifyEvent(true);
                        SendReply(player, msg("notifAct", player.UserIDString));
                    }                    
                    break;

                default:
                    SendReply(player, string.Format(msg("unknown", player.UserIDString), args[0]));
                    break;
            }
        }

        [ConsoleCommand("cb.random")]
        private void ccmdEventRandom(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin)
                return;

            try
            {
                if (configData == null)
                {
                    Puts("Error: Plugin configuration not loaded. Try reloading the plugin or wait a moment after server startup.");
                    return;
                }
                
                StartRandomOnMap();
                Puts("Random event started");
            }
            catch (System.Exception ex)
            {
                Puts($"Error starting random event: {ex.Message}");
            }
        }

        [ConsoleCommand("cb.onposition")]
        private void ccmdEventOnPosition(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin)
                return;

            if (configData == null)
            {
                Puts("Error: Plugin configuration not loaded. Try reloading the plugin or wait a moment after server startup.");
                return;
            }

            float x, z;

            if (arg.Args.Length == 2 && float.TryParse(arg.Args[0], out x) && float.TryParse(arg.Args[1], out z))
            {
                try
                {
                    var position = new Vector3(x, 0, z);
                    StartRainOfFire(GetGroundPosition(position), configData.z_IntensitySettings.Settings_Optimal, "Console Command");
                    Puts($"Random event started on position {x}, {position.y}, {z}");
                }
                catch (System.Exception ex)
                {
                    Puts($"Error starting event at position: {ex.Message}");
                }
            }
            else
                Puts("Usage: cb.onposition x z");
        }
        #endregion

        #region Helpers
        private BasePlayer GetPlayerByName(string name)
        {
            string currentName;
            string lastName;
            BasePlayer foundPlayer = null;
            name = name.ToLower();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                currentName = player.displayName.ToLower();

                if (currentName.Contains(name))
                {
                    if (foundPlayer != null)
                    {
                        lastName = foundPlayer.displayName;
                        if (currentName.Replace(name, "").Length < lastName.Replace(name, "").Length)
                        {
                            foundPlayer = player;
                        }
                    }

                    foundPlayer = player;
                }
            }

            return foundPlayer;
        }

        private static int RandomRange(int min, int max) => UnityEngine.Random.Range(min, max);
       
        private Vector3 GetGroundPosition(Vector3 sourcePos)
        {
            RaycastHit hitInfo;

            if (Physics.Raycast(sourcePos, Vector3.down, out hitInfo, LayerMask.GetMask("Terrain", "World", "Construction")))
            {
                sourcePos.y = hitInfo.point.y;
            }
            sourcePos.y = Mathf.Max(sourcePos.y, TerrainMeta.HeightMap.GetHeight(sourcePos));
            return sourcePos;
        }

        private string GetGridReference(Vector3 position)
        {
            // Rust map grid system: center is 0,0
            // Grid letters go A-Z from west to east, numbers 0-25+ from south to north
            float mapSize = TerrainMeta.Size.x;
            float halfMap = mapSize / 2f;
            
            // Convert world coordinates to grid coordinates
            float gridX = (position.x + halfMap) / (mapSize / 26f);
            float gridZ = (position.z + halfMap) / (mapSize / 26f);
            
            // Get grid letter (A-Z)
            char gridLetter = (char)('A' + Mathf.Clamp((int)gridX, 0, 25));
            
            // Get grid number (0-25+)
            int gridNumber = Mathf.Clamp((int)gridZ, 0, 25);
            
            return $"{gridLetter}{gridNumber}";
        }        
        #endregion

        #region Classes 
        private class ItemCarrier : MonoBehaviour
        {
            private ItemDrop[] carriedItems = null;

            private float multiplier;

            public void SetCarriedItems(ItemDrop[] carriedItems) => this.carriedItems = carriedItems;

            public void SetDropMultiplier(float multiplier) => this.multiplier = multiplier;

            private void OnDestroy()
            {
                if (carriedItems == null)
                    return;

                int amount;

                for (int i = 0; i < carriedItems.Length; i++)
                {
                    if ((amount = (int)(RandomRange(carriedItems[i].Minimum, carriedItems[i].Maximum) * multiplier)) > 0)
                        ItemManager.CreateByName(carriedItems[i].Shortname, amount).Drop(gameObject.transform.position, Vector3.up);
                }
            }           
        }

        private class ItemDrop
        {
            public string Shortname { get; set; }
            public int Minimum { get; set; }
            public int Maximum { get; set; }
        }
        #endregion

        #region Config        
        private ConfigData configData;
        
        class ConfigData
        {
            public BarrageOptions BarrageSettings { get; set; }
            public DamageOptions DamageControl { get; set; }
            public ConfigOptions Options { get; set; }
            public IntensityOptions z_IntensitySettings { get; set; }

            public class DamageOptions
            {
                public float DamageMultiplier { get; set; }
            }

            public class BarrageOptions
            {
                public int NumberOfRockets { get; set; }
                public float RocketDelay { get; set; }
                public float RocketSpread { get; set; }
            }

            public class Drops
            {
                public bool EnableItemDrop { get; set; }
                public ItemDrop[] ItemsToDrop { get; set; }
            }

            public class ConfigOptions
            {
                public bool EnableAutomaticEvents { get; set; }
                public Timers EventTimers { get; set; }
                public float GlobalDropMultiplier { get; set; }
                public bool NotifyEvent { get; set; }
                public int MinimumPlayerCount { get; set; }
                public PerformanceSettings PerformanceMonitoring { get; set; }
                public WarningSettings WarningCountdown { get; set; }
                public EffectsSettings VisualEffects { get; set; }
                public MapMarkerSettings MapMarkers { get; set; }
            }

            public class PerformanceSettings
            {
                public bool EnableFPSCheck { get; set; }
                public float MinimumFPS { get; set; }
            }

            public class WarningSettings
            {
                public bool EnableWarning { get; set; }
                public float CountdownSeconds { get; set; }
            }

            public class EffectsSettings
            {
                public bool EnableScreenShake { get; set; }
                public bool EnableParticleTrails { get; set; }
            }

            public class MapMarkerSettings
            {
                public bool EnableMapMarkers { get; set; }
            }

            public class Timers
            {
                public int EventInterval { get; set; }
                public bool UseRandomTimer { get; set; }
                public int RandomTimerMin { get; set; }
                public int RandomTimerMax { get; set; }
            }

            public class Settings
            {
                public int FireRocketChance { get; set; }
                public float Radius { get; set; }
                public int RocketAmount { get; set; }
                public int Duration { get; set; }
                public Drops ItemDropControl { get; set; }
            }

            public class IntensityOptions
            {
                public Settings Settings_Mild { get; set; }
                public Settings Settings_Optimal { get; set; }
                public Settings Settings_Extreme { get; set; }
            }
        }

        private void LoadVariables()
        {
            LoadConfigVariables();
            ValidateConfig();
            SaveConfig();
        }

        private void ValidateConfig()
        {
            bool configChanged = false;
            Puts("========== CONFIG VALIDATION ==========");

            // Validate and fix null config sections
            if (configData == null)
            {
                Puts("Config is null - creating default config");
                LoadDefaultConfig();
                return;
            }

            // Validate BarrageSettings
            if (configData.BarrageSettings == null)
            {
                Puts("BarrageSettings is null - creating defaults");
                configData.BarrageSettings = new ConfigData.BarrageOptions();
                configChanged = true;
            }
            
            if (configData.BarrageSettings.NumberOfRockets <= 0)
            {
                Puts($"Invalid NumberOfRockets ({configData.BarrageSettings.NumberOfRockets}) - setting to 20");
                configData.BarrageSettings.NumberOfRockets = 20;
                configChanged = true;
            }
            
            if (configData.BarrageSettings.RocketDelay <= 0)
            {
                Puts($"Invalid RocketDelay ({configData.BarrageSettings.RocketDelay}) - setting to 0.33");
                configData.BarrageSettings.RocketDelay = 0.33f;
                configChanged = true;
            }
            
            if (configData.BarrageSettings.RocketSpread <= 0)
            {
                Puts($"Invalid RocketSpread ({configData.BarrageSettings.RocketSpread}) - setting to 16");
                configData.BarrageSettings.RocketSpread = 16f;
                configChanged = true;
            }

            // Validate DamageControl
            if (configData.DamageControl == null)
            {
                Puts("DamageControl is null - creating defaults");
                configData.DamageControl = new ConfigData.DamageOptions();
                configChanged = true;
            }
            
            if (configData.DamageControl.DamageMultiplier < 0)
            {
                Puts($"Invalid DamageMultiplier ({configData.DamageControl.DamageMultiplier}) - setting to 0.2");
                configData.DamageControl.DamageMultiplier = 0.2f;
                configChanged = true;
            }

            // Validate Options
            if (configData.Options == null)
            {
                Puts("Options is null - creating defaults");
                configData.Options = new ConfigData.ConfigOptions();
                configChanged = true;
            }

            // Validate EventTimers
            if (configData.Options.EventTimers == null)
            {
                Puts("EventTimers is null - creating defaults");
                configData.Options.EventTimers = new ConfigData.Timers();
                configChanged = true;
            }
            
            if (configData.Options.EventTimers.EventInterval <= 0)
            {
                Puts($"Invalid EventInterval ({configData.Options.EventTimers.EventInterval}) - setting to 30");
                configData.Options.EventTimers.EventInterval = 30;
                configChanged = true;
            }
            
            if (configData.Options.EventTimers.RandomTimerMin <= 0)
            {
                Puts($"Invalid RandomTimerMin ({configData.Options.EventTimers.RandomTimerMin}) - setting to 15");
                configData.Options.EventTimers.RandomTimerMin = 15;
                configChanged = true;
            }
            
            if (configData.Options.EventTimers.RandomTimerMax <= configData.Options.EventTimers.RandomTimerMin)
            {
                Puts($"Invalid RandomTimerMax ({configData.Options.EventTimers.RandomTimerMax}) - setting to 45");
                configData.Options.EventTimers.RandomTimerMax = 45;
                configChanged = true;
            }

            if (configData.Options.GlobalDropMultiplier < 0)
            {
                Puts($"Invalid GlobalDropMultiplier ({configData.Options.GlobalDropMultiplier}) - setting to 1.0");
                configData.Options.GlobalDropMultiplier = 1.0f;
                configChanged = true;
            }

            if (configData.Options.MinimumPlayerCount < 0)
            {
                Puts($"Invalid MinimumPlayerCount ({configData.Options.MinimumPlayerCount}) - setting to 2");
                configData.Options.MinimumPlayerCount = 2;
                configChanged = true;
            }

            // Validate PerformanceMonitoring
            if (configData.Options.PerformanceMonitoring == null)
            {
                Puts("PerformanceMonitoring is null - creating defaults");
                configData.Options.PerformanceMonitoring = new ConfigData.PerformanceSettings();
                configChanged = true;
            }
            
            if (configData.Options.PerformanceMonitoring.MinimumFPS <= 0)
            {
                Puts($"Invalid MinimumFPS ({configData.Options.PerformanceMonitoring.MinimumFPS}) - setting to 40");
                configData.Options.PerformanceMonitoring.MinimumFPS = 40f;
                configChanged = true;
            }

            // Validate WarningCountdown
            if (configData.Options.WarningCountdown == null)
            {
                Puts("WarningCountdown is null - creating defaults");
                configData.Options.WarningCountdown = new ConfigData.WarningSettings();
                configChanged = true;
            }
            
            if (configData.Options.WarningCountdown.CountdownSeconds < 0)
            {
                Puts($"Invalid CountdownSeconds ({configData.Options.WarningCountdown.CountdownSeconds}) - setting to 30");
                configData.Options.WarningCountdown.CountdownSeconds = 30f;
                configChanged = true;
            }

            // Validate VisualEffects
            if (configData.Options.VisualEffects == null)
            {
                Puts("VisualEffects is null - creating defaults");
                configData.Options.VisualEffects = new ConfigData.EffectsSettings();
                configChanged = true;
            }

            // Validate MapMarkers
            if (configData.Options.MapMarkers == null)
            {
                Puts("MapMarkers is null - creating defaults");
                configData.Options.MapMarkers = new ConfigData.MapMarkerSettings();
                configChanged = true;
            }

            // Validate IntensitySettings
            if (configData.z_IntensitySettings == null)
            {
                Puts("IntensitySettings is null - creating defaults");
                configData.z_IntensitySettings = new ConfigData.IntensityOptions();
                configChanged = true;
            }

            // Validate Mild Settings
            if (configData.z_IntensitySettings.Settings_Mild == null)
            {
                Puts("Settings_Mild is null - creating defaults");
                configData.z_IntensitySettings.Settings_Mild = CreateDefaultMildSettings();
                configChanged = true;
            }
            else
            {
                configChanged |= ValidateSettings(configData.z_IntensitySettings.Settings_Mild, "Mild");
            }

            // Validate Optimal Settings
            if (configData.z_IntensitySettings.Settings_Optimal == null)
            {
                Puts("Settings_Optimal is null - creating defaults");
                configData.z_IntensitySettings.Settings_Optimal = CreateDefaultOptimalSettings();
                configChanged = true;
            }
            else
            {
                configChanged |= ValidateSettings(configData.z_IntensitySettings.Settings_Optimal, "Optimal");
            }

            // Validate Extreme Settings
            if (configData.z_IntensitySettings.Settings_Extreme == null)
            {
                Puts("Settings_Extreme is null - creating defaults");
                configData.z_IntensitySettings.Settings_Extreme = CreateDefaultExtremeSettings();
                configChanged = true;
            }
            else
            {
                configChanged |= ValidateSettings(configData.z_IntensitySettings.Settings_Extreme, "Extreme");
            }

            if (configChanged)
            {
                Puts("Config validation completed - fixed invalid values");
                SaveConfig(configData);
            }
            else
            {
                Puts("Config validation completed - no issues found");
            }
            Puts("======================================");
        }

        private bool ValidateSettings(ConfigData.Settings settings, string settingName)
        {
            bool changed = false;

            if (settings.FireRocketChance <= 0)
            {
                Puts($"Invalid FireRocketChance in {settingName} ({settings.FireRocketChance}) - setting to 20");
                settings.FireRocketChance = 20;
                changed = true;
            }

            if (settings.Radius <= 0)
            {
                Puts($"Invalid Radius in {settingName} ({settings.Radius}) - setting to 300");
                settings.Radius = 300f;
                changed = true;
            }

            if (settings.RocketAmount <= 0)
            {
                Puts($"Invalid RocketAmount in {settingName} ({settings.RocketAmount}) - setting to 45");
                settings.RocketAmount = 45;
                changed = true;
            }

            if (settings.Duration <= 0)
            {
                Puts($"Invalid Duration in {settingName} ({settings.Duration}) - setting to 120");
                settings.Duration = 120;
                changed = true;
            }

            // Validate ItemDropControl
            if (settings.ItemDropControl == null)
            {
                Puts($"ItemDropControl in {settingName} is null - creating defaults");
                settings.ItemDropControl = new ConfigData.Drops
                {
                    EnableItemDrop = true,
                    ItemsToDrop = CreateDefaultItemDrops(settingName)
                };
                changed = true;
            }

            if (settings.ItemDropControl.ItemsToDrop == null || settings.ItemDropControl.ItemsToDrop.Length == 0)
            {
                Puts($"ItemsToDrop in {settingName} is null or empty - creating defaults");
                settings.ItemDropControl.ItemsToDrop = CreateDefaultItemDrops(settingName);
                changed = true;
            }
            else
            {
                // Validate each item drop
                for (int i = 0; i < settings.ItemDropControl.ItemsToDrop.Length; i++)
                {
                    var item = settings.ItemDropControl.ItemsToDrop[i];
                    if (item == null)
                    {
                        Puts($"ItemDrop #{i} in {settingName} is null - removing");
                        var itemList = new List<ItemDrop>(settings.ItemDropControl.ItemsToDrop);
                        itemList.RemoveAt(i);
                        settings.ItemDropControl.ItemsToDrop = itemList.ToArray();
                        i--; // Adjust index after removal
                        changed = true;
                        continue;
                    }

                    if (string.IsNullOrEmpty(item.Shortname))
                    {
                        Puts($"ItemDrop #{i} in {settingName} has invalid shortname - setting to 'stones'");
                        item.Shortname = "stones";
                        changed = true;
                    }

                    if (item.Minimum < 0)
                    {
                        Puts($"ItemDrop #{i} in {settingName} has invalid Minimum ({item.Minimum}) - setting to 0");
                        item.Minimum = 0;
                        changed = true;
                    }

                    if (item.Maximum < item.Minimum)
                    {
                        Puts($"ItemDrop #{i} in {settingName} has invalid Maximum ({item.Maximum}) - setting to {item.Minimum + 10}");
                        item.Maximum = item.Minimum + 10;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private ItemDrop[] CreateDefaultItemDrops(string settingName)
        {
            switch (settingName)
            {
                case "Mild":
                    return new ItemDrop[]
                    {
                        new ItemDrop { Maximum = 120, Minimum = 80, Shortname = "stones" },
                        new ItemDrop { Maximum = 50, Minimum = 25, Shortname = "metal.ore" }
                    };
                case "Optimal":
                    return new ItemDrop[]
                    {
                        new ItemDrop { Maximum = 250, Minimum = 160, Shortname = "stones" },
                        new ItemDrop { Maximum = 120, Minimum = 60, Shortname = "metal.fragments" },
                        new ItemDrop { Maximum = 50, Minimum = 20, Shortname = "hq.metal.ore" }
                    };
                case "Extreme":
                    return new ItemDrop[]
                    {
                        new ItemDrop { Maximum = 400, Minimum = 250, Shortname = "stones" },
                        new ItemDrop { Maximum = 300, Minimum = 125, Shortname = "metal.fragments" },
                        new ItemDrop { Maximum = 50, Minimum = 20, Shortname = "metal.refined" },
                        new ItemDrop { Maximum = 120, Minimum = 45, Shortname = "sulfur.ore" }
                    };
                default:
                    return new ItemDrop[]
                    {
                        new ItemDrop { Maximum = 100, Minimum = 50, Shortname = "stones" }
                    };
            }
        }

        private ConfigData.Settings CreateDefaultMildSettings()
        {
            return new ConfigData.Settings
            {
                FireRocketChance = 30,
                Radius = 500f,
                Duration = 240,
                RocketAmount = 20,
                ItemDropControl = new ConfigData.Drops
                {
                    EnableItemDrop = true,
                    ItemsToDrop = CreateDefaultItemDrops("Mild")
                }
            };
        }

        private ConfigData.Settings CreateDefaultOptimalSettings()
        {
            return new ConfigData.Settings
            {
                FireRocketChance = 20,
                Radius = 300f,
                Duration = 120,
                RocketAmount = 45,
                ItemDropControl = new ConfigData.Drops
                {
                    EnableItemDrop = true,
                    ItemsToDrop = CreateDefaultItemDrops("Optimal")
                }
            };
        }

        private ConfigData.Settings CreateDefaultExtremeSettings()
        {
            return new ConfigData.Settings
            {
                FireRocketChance = 10,
                Radius = 100f,
                Duration = 30,
                RocketAmount = 70,
                ItemDropControl = new ConfigData.Drops
                {
                    EnableItemDrop = true,
                    ItemsToDrop = CreateDefaultItemDrops("Extreme")
                }
            };
        }

        protected override void LoadDefaultConfig()
        {
            var config = new ConfigData
            {
                BarrageSettings = new ConfigData.BarrageOptions
                {
                    NumberOfRockets = 20,
                    RocketDelay = 0.33f,
                    RocketSpread = 16f
                },
                DamageControl = new ConfigData.DamageOptions
                {
                    DamageMultiplier = 0.2f,
                },                
                Options = new ConfigData.ConfigOptions
                {
                    EnableAutomaticEvents = true,
                    EventTimers = new ConfigData.Timers
                    {
                        EventInterval = 30,
                        RandomTimerMax = 45,
                        RandomTimerMin = 15,
                        UseRandomTimer = false
                    },
                    GlobalDropMultiplier = 1.0f,
                    NotifyEvent = true,
                    MinimumPlayerCount = 2,
                    PerformanceMonitoring = new ConfigData.PerformanceSettings
                    {
                        EnableFPSCheck = true,
                        MinimumFPS = 40f
                    },
                    WarningCountdown = new ConfigData.WarningSettings
                    {
                        EnableWarning = true,
                        CountdownSeconds = 30f
                    },
                    VisualEffects = new ConfigData.EffectsSettings
                    {
                        EnableScreenShake = true,
                        EnableParticleTrails = true
                    },
                    MapMarkers = new ConfigData.MapMarkerSettings
                    {
                        EnableMapMarkers = true
                    }
                },
                z_IntensitySettings = new ConfigData.IntensityOptions
                {
                    Settings_Mild = CreateDefaultMildSettings(),
                    Settings_Optimal = CreateDefaultOptimalSettings(),
                    Settings_Extreme = CreateDefaultExtremeSettings()
                }
            };
            SaveConfig(config);
        }

        private void LoadConfigVariables() => configData = Config.ReadObject<ConfigData>();
        void SaveConfig(ConfigData config) => Config.WriteObject(config, true);
        #endregion

        #region Localization
        string msg(string key, string playerId = "") => lang.GetMessage(key, this, playerId);
        Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            {"incoming", "Meteor Shower Incoming" },
            {"help1", "/cb onplayer <opt:playername> - Calls a event on your position, or the player specified"},
            {"help2", "/cb onplayer_extreme <opt:playername> - Starts a extreme event on your position, or the player specified"},
            {"help3", "/cb onplayer_mild <opt:playername> - Starts a optimal event on your position, or the player specified"},
            {"help4", "/cb barrage - Fire a barrage of rockets from your position"},
            {"help5", "/cb random - Calls a event at a random postion"},
            {"help6", "/cb intervals <amount> - Change the time between events"},
            {"help7", "/cb damagescale <amount> - Change the damage scale"},
            {"help8", "/cb togglemsg - Toggle public event broadcast"},
            {"calledOn", "Event called on {0}'s position"},
            {"noPlayer", "No player found with that name"},
            {"onPos", "Event called on your position"},
            {"Extreme", "Extreme"},
            {"Mild", "Mild" },
            {"randomCall", "Event called on random position"},
            {"invalidParam", "Invalid parameter '{0}'"},
            {"smallInter", "Event intervals under 4 minutes are not allowed"},
            {"interSet", "Event intervals set to {0} minutes"},
            {"dropMulti", "Global item drop multiplier set to "},
            {"damageMulti", "Damage scale set to "},
            {"notifDeAct", "Event notification de-activated"},
            {"notifAct", "Event notification activated"},
            {"unknown", "Unknown parameter '{0}'"}
        };
        #endregion

    }
}