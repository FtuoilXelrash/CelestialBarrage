using System;
using System.Collections.Generic;
using Oxide.Core.Plugins;
using Oxide.Core;
using UnityEngine;
using Rust;
using System.Collections;
using Oxide.Core.Libraries.Covalence;
using Newtonsoft.Json;
using System.Text;
using System.Linq;
 
namespace Oxide.Plugins
{
    [Info("Celestial Barrage", "Ftuoil Xelrash", "1.0.20")]
    [Description("Create a Celestial Barrage falling from the sky")]
    class CelestialBarrage : RustPlugin
    {
        #region Fields

        private Timer EventTimer = null;
        private List<Timer> RocketTimers = new List<Timer>();
        private HashSet<BaseEntity> meteorRockets = new HashSet<BaseEntity>(); // Track our meteor rockets
        private List<(Vector3 position, float time)> recentExplosions = new List<(Vector3, float)>(); // Track recent explosion positions for impact detection
        private List<MapMarkerGenericRadius> activeMarkers = new List<MapMarkerGenericRadius>(); // Track map markers
        private List<VendingMachineMapMarker> activeVendingMarkers = new List<VendingMachineMapMarker>(); // Track vending markers for hover text
        private Dictionary<string, float> discordRateLimiter = new Dictionary<string, float>(); // Rate limit Discord messages
        private Dictionary<string, int> discordMessageCount = new Dictionary<string, int>(); // Track message count per minute
        private Queue<QueuedDiscordMessage> discordMessageQueue = new Queue<QueuedDiscordMessage>(); // Queue for rate limited messages
        private Timer discordQueueTimer = null;
        private bool wasGrenadeLauncherOverride = false; // Track if grenade launcher override occurred
        #endregion

        #region Oxide Hooks       
        private void OnServerInitialized()
        {
            try
            {
                lang.RegisterMessages(Messages, this);
                LoadVariables();
                StartEventTimer();
                StartDiscordQueueProcessor();
            }
            catch (System.Exception ex)
            {
                PrintError($"Error during plugin initialization: {ex.Message}");
            }
        }  
        
        private void Unload()
        {
            StopTimer();
            discordQueueTimer?.Destroy();
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
            float now = Time.realtimeSinceStartup;

            // Purge explosion records older than 0.5 seconds
            recentExplosions.RemoveAll(e => now - e.time > 0.5f);

            // Check live rockets and recent explosion positions
            bool nearMeteor = false;
            float closestDistance = float.MaxValue;

            foreach (var rocket in meteorRockets)
            {
                if (rocket != null && !rocket.IsDestroyed)
                {
                    float d = Vector3.Distance(rocket.transform.position, entity.transform.position);
                    if (d < 50f)
                    {
                        nearMeteor = true;
                        if (d < closestDistance) closestDistance = d;
                    }
                }
            }

            if (!nearMeteor)
            {
                foreach (var explosion in recentExplosions)
                {
                    float d = Vector3.Distance(explosion.position, entity.transform.position);
                    if (d < 50f)
                    {
                        nearMeteor = true;
                        if (d < closestDistance) closestDistance = d;
                        break;
                    }
                }
            }

            if (configData?.Logging?.LogDebugToConsole == true && (meteorRockets.Count > 0 || recentExplosions.Count > 0))
            {
                string weaponInfo = info?.WeaponPrefab?.ShortPrefabName ?? "Unknown";
                string attackerInfo = info?.Initiator?.ShortPrefabName ?? "Unknown";
                Puts($"[DEBUG] Damage event - Weapon: {weaponInfo}, Attacker: {attackerInfo}, Damage: {info?.damageTypes?.Total():F1}, Entity: {entity?.ShortPrefabName}, NearMeteor: {nearMeteor}");
            }

            if (nearMeteor)
            {
                if (configData?.Logging?.LogDebugToConsole == true)
                    Puts($"[DEBUG] Meteor impact detected - Distance: {closestDistance:F1}m");
                LogMeteorImpact(entity, info);
            }
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            // Clean up tracking when our rockets explode/die
            if (entity != null && meteorRockets.Contains(entity))
            {
                if (configData?.Logging?.LogDebugToConsole == true)
                {
                    Puts($"[DEBUG] Removing tracked meteor projectile: {entity.ShortPrefabName} (Remaining: {meteorRockets.Count - 1})");
                }
                // Record explosion position so OnEntityTakeDamage can still match impacts after the rocket is gone
                recentExplosions.Add((entity.transform.position, Time.realtimeSinceStartup));
                meteorRockets.Remove(entity);
            }
        }
        #endregion

        #region Functions
        private void StartDiscordQueueProcessor()
        {
            // Process Discord queue every 5 seconds
            discordQueueTimer = timer.Repeat(5f, 0, ProcessDiscordQueue);
        }

        private void ProcessDiscordQueue()
        {
            if (!configData.Logging.DiscordRateLimit.EnableRateLimit)
            {
                // If rate limiting is disabled, send all queued messages immediately
                while (discordMessageQueue.Count > 0)
                {
                    var queuedMessage = discordMessageQueue.Dequeue();
                    SendDiscordMessageImmediately(queuedMessage);
                }
                return;
            }

            // Reset message counts every minute
            float currentTime = UnityEngine.Time.realtimeSinceStartup;
            var keysToRemove = new List<string>();
            foreach (var kvp in discordRateLimiter)
            {
                if (currentTime - kvp.Value > 60f) // Reset after 1 minute
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
            foreach (var key in keysToRemove)
            {
                discordRateLimiter.Remove(key);
                discordMessageCount.Remove(key);
            }

            // Process queued messages within rate limits
            var messagesToProcess = new List<QueuedDiscordMessage>();
            while (discordMessageQueue.Count > 0 && messagesToProcess.Count < 3) // Process max 3 at a time
            {
                messagesToProcess.Add(discordMessageQueue.Dequeue());
            }

            foreach (var queuedMessage in messagesToProcess)
            {
                if (CanSendDiscordMessage(queuedMessage.MessageType))
                {
                    SendDiscordMessageImmediately(queuedMessage);
                }
                else
                {
                    // Put it back in queue if still rate limited
                    discordMessageQueue.Enqueue(queuedMessage);
                    break; // Stop processing to avoid infinite loop
                }
            }
        }

        private bool CanSendDiscordMessage(string messageType)
        {
            if (!configData.Logging.DiscordRateLimit.EnableRateLimit)
                return true;

            float currentTime = UnityEngine.Time.realtimeSinceStartup;
            string rateLimitKey = messageType;

            // Check if we've hit the per-minute limit
            if (discordMessageCount.ContainsKey(rateLimitKey))
            {
                if (discordMessageCount[rateLimitKey] >= configData.Logging.DiscordRateLimit.MaxImpactsPerMinute)
                {
                    return false; // Hit per-minute limit
                }
            }

            // Check cooldown between messages
            if (discordRateLimiter.ContainsKey(rateLimitKey))
            {
                float timeSinceLastMessage = currentTime - discordRateLimiter[rateLimitKey];
                if (timeSinceLastMessage < configData.Logging.DiscordRateLimit.ImpactMessageCooldown)
                {
                    return false; // Still in cooldown
                }
            }

            return true;
        }

        private void QueueDiscordMessage(string webhookUrl, string message, bool isAdmin, string messageType)
        {
            try
            {
                var queuedMessage = new QueuedDiscordMessage
                {
                    WebhookUrl = webhookUrl,
                    Message = message,
                    IsAdmin = isAdmin,
                    MessageType = messageType,
                    QueuedTime = UnityEngine.Time.realtimeSinceStartup
                };

                if (CanSendDiscordMessage(messageType))
                {
                    // Send immediately if not rate limited
                    SendDiscordMessageImmediately(queuedMessage);
                }
                else
                {
                    // Queue the message
                    discordMessageQueue.Enqueue(queuedMessage);
                
                    // Enhanced logging for queue status
                    if (configData.Logging.LogDebugToConsole)
                    {
                        Puts($"[DISCORD QUEUE] Message queued due to rate limiting. Queue size: {discordMessageQueue.Count}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                PrintError($"Error queuing Discord message: {ex.Message}");
            }
        }

        private void SendDiscordMessageImmediately(QueuedDiscordMessage queuedMessage)
        {
            // Update rate limiting tracking
            if (configData.Logging.DiscordRateLimit.EnableRateLimit)
            {
                float currentTime = UnityEngine.Time.realtimeSinceStartup;
                string rateLimitKey = queuedMessage.MessageType;
                
                discordRateLimiter[rateLimitKey] = currentTime;
                
                if (!discordMessageCount.ContainsKey(rateLimitKey))
                    discordMessageCount[rateLimitKey] = 0;
                discordMessageCount[rateLimitKey]++;
            }

            // Calculate queue delay for display
            float queueDelay = UnityEngine.Time.realtimeSinceStartup - queuedMessage.QueuedTime;
            
            // Send the message with queue info passed separately for proper embed formatting
            if (queueDelay > 5f)
            {
                SendDiscordEmbedWithQueueInfo(queuedMessage.WebhookUrl, queuedMessage.Message, queuedMessage.IsAdmin, queueDelay, discordMessageQueue.Count);
            }
            else
            {
                SendDiscordEmbedDirect(queuedMessage.WebhookUrl, queuedMessage.Message, queuedMessage.IsAdmin);
            }
            
            // Log when processing queued messages
            if (queueDelay > 5f && configData.Logging.LogDebugToConsole)
            {
                Puts($"[DISCORD QUEUE] Sent queued message (delayed {queueDelay:F0}s). {discordMessageQueue.Count} messages remaining in queue.");
            }
        }

        private void StartEventTimer()
        {
            try
            {
                if (configData.Options.EnableAutomaticEvents)
                {
                    EventTimer = timer.Repeat(configData.Options.EventTimers.EventIntervalMinutes * 60, 0, () => StartRandomOnMap());
                }
            }
            catch (System.Exception ex)
            {
                PrintError($"Error starting event timer: {ex.Message}");
            }
        }
        private void StopTimer()
        {
            if (EventTimer != null)
                EventTimer.Destroy();
        }

        private bool CheckPerformanceAndPlayerCount(string eventType = "Event")
        {
            // Check minimum player requirement
            if (configData?.Options?.MinimumPlayerCount != null && BasePlayer.activePlayerList.Count < configData.Options.MinimumPlayerCount)
            {
                string reason = "Not enough players online";
                string additionalInfo = $"Current Players: {BasePlayer.activePlayerList.Count} / {configData.Options.MinimumPlayerCount} minimum required";
                SendSkippedEventDiscord(reason, eventType, additionalInfo);

                // Keep console logging
                if (configData.Logging.LogDebugToConsole)
                {
                    Puts($"CELESTIAL BARRAGE SKIPPED - Not enough players online ({BasePlayer.activePlayerList.Count} < {configData.Options.MinimumPlayerCount}) - {eventType}");
                }
                return false;
            }

            // Check performance monitoring
            if (configData?.Options?.PerformanceMonitoring?.EnableFPSCheck == true)
            {
                float currentFPS = 1f / UnityEngine.Time.unscaledDeltaTime;
                if (currentFPS < configData.Options.PerformanceMonitoring.MinimumFPS)
                {
                    string reason = "Server performance below threshold";
                    string additionalInfo = $"Current FPS: {currentFPS:F1} / {configData.Options.PerformanceMonitoring.MinimumFPS} minimum required";
                    SendSkippedEventDiscord(reason, eventType, additionalInfo);

                    // Keep console logging
                    if (configData.Logging.LogDebugToConsole)
                    {
                        Puts($"CELESTIAL BARRAGE CANCELLED - Low FPS detected ({currentFPS:F1} < {configData.Options.PerformanceMonitoring.MinimumFPS}) - {eventType}");
                    }
                    return false;
                }
            }

            return true;
        }

        private void StartRandomOnMap(bool skipPlayerCount = false, bool skipFpsCheck = false)
        {
            // Player count check only applies to automatic events, not admin-triggered ones
            if (!skipPlayerCount && !CheckPerformanceAndPlayerCount("Automatic Event"))
                return;

            float mapsize = (TerrainMeta.Size.x / 2) - 600f;

            float randomX = UnityEngine.Random.Range(-mapsize, mapsize);
            float randomY = UnityEngine.Random.Range(-mapsize, mapsize);

            Vector3 callAt = new Vector3(randomX, 0f, randomY);

            var selectedSetting = GetRandomIntensitySetting();
            StartCelestialEvent(callAt, selectedSetting.setting, "Automatic Event", skipFpsCheck);
        }

        private (ConfigData.Settings setting, string intensity) GetRandomIntensitySetting()
        {
            var w = configData.IntensitySettings.SpawnWeights;
            float total = w.MildWeight + w.MediumWeight + w.ExtremeWeight;
            if (total <= 0f)
                return (configData.IntensitySettings.Mild, "Mild");

            float roll = UnityEngine.Random.Range(0f, total);

            if (roll < w.MildWeight)
                return (configData.IntensitySettings.Mild, "Mild");
            roll -= w.MildWeight;

            if (roll < w.MediumWeight)
                return (configData.IntensitySettings.Medium, "Medium");

            return (configData.IntensitySettings.Extreme, "Extreme");
        }

        private bool StartOnPlayer(string playerName, ConfigData.Settings setting, string eventType, BasePlayer caller = null, bool skipFpsCheck = false)
        {
            BasePlayer player = GetPlayerByName(playerName, caller);

            if (player == null)
                return false;

            StartCelestialEvent(player.transform.position, setting, eventType, skipFpsCheck);
            return true;
        }

        private bool StartRandomOnPlayer(string playerName, string eventType, BasePlayer caller = null, bool skipFpsCheck = false)
        {
            BasePlayer player = GetPlayerByName(playerName, caller);

            if (player == null)
                return false;

            var randomSetting = GetRandomIntensitySetting();
            StartCelestialEvent(player.transform.position, randomSetting.setting, eventType, skipFpsCheck);
            return true;
        }

        private void StartRandomOnPosition(Vector3 position, string eventType, bool skipFpsCheck = false)
        {
            var randomSetting = GetRandomIntensitySetting();
            StartCelestialEvent(position, randomSetting.setting, eventType, skipFpsCheck);
        }

        private void StartBarrage(Vector3 origin, Vector3 direction) => timer.Repeat(configData.BarrageSettings.RocketDelay, configData.BarrageSettings.NumberOfRockets, () => SpreadRocket(origin, direction));

        private void StartCelestialEvent(Vector3 origin, ConfigData.Settings setting, string eventType = "Manual", bool skipFpsCheck = false)
        {
            // FPS check applies to automatic events only; admin-triggered events bypass it
            if (!skipFpsCheck && configData?.Options?.PerformanceMonitoring?.EnableFPSCheck == true)
            {
                float currentFPS = 1f / UnityEngine.Time.unscaledDeltaTime;
                if (currentFPS < configData.Options.PerformanceMonitoring.MinimumFPS)
                {
                    LogMessage($"CELESTIAL BARRAGE BLOCKED\nLow FPS detected ({currentFPS:F1} < {configData.Options.PerformanceMonitoring.MinimumFPS})\n{eventType}", "admin");
                    return;
                }
            }

            float radius = setting.Radius;
            int numberOfRockets = setting.RocketAmount;
            float duration = configData.Options.EventTimers.UseRandomTimers
                ? UnityEngine.Random.Range(setting.DurationSecondsMin, setting.DurationSecondsMax)
                : setting.DurationSecondsMin;
            bool dropsItems = setting.ItemDropControl.EnableItemDrop;
            ItemDrop[] itemDrops = setting.ItemDropControl.ItemsToDrop;

            float intervals = duration / numberOfRockets;

            // Determine intensity level by comparing settings objects
            string intensity = "Unknown";
            if (ReferenceEquals(setting, configData.IntensitySettings.Mild))
                intensity = "Mild";
            else if (ReferenceEquals(setting, configData.IntensitySettings.Medium))
                intensity = "Medium";
            else if (ReferenceEquals(setting, configData.IntensitySettings.Extreme))
                intensity = "Extreme";

            // Log celestial barrage start to console
            string gridRef = GetGridReference(origin);
            Vector3 groundPos = GetGroundPosition(origin);
            string teleportCmd = $"teleportpos {groundPos.x:F1} {groundPos.y:F1} {groundPos.z:F1}";

            // Start meteor shower event
            StartMeteorShowerWithEffects(origin, setting, eventType, intensity, gridRef, teleportCmd, intervals, numberOfRockets, duration, radius);
        }

        private void StartMeteorShowerWithEffects(Vector3 origin, ConfigData.Settings setting, string eventType, string intensity, string gridRef, string teleportCmd, float intervals, int numberOfRockets, float duration, float radius)
        {
            string startMessage = $"========== METEOR SHOWER STARTED ==========\n" +
                                $"Type: {eventType} ({intensity})\n" +
                                $"Location: ({origin.x:F0}, {origin.z:F0}) Grid: {gridRef}\n" +
                                $"Stats: {numberOfRockets} rockets, {duration:F0}s duration ({setting.DurationSecondsMin:F0}-{setting.DurationSecondsMax:F0}s range), {radius}m radius\n" +
                                $"Teleport: {teleportCmd}\n" +
                                $"==========================================";

            // Send enhanced Discord embeds to admin
            SendEnhancedDiscordMessage(true, eventType, intensity, gridRef, numberOfRockets, duration, radius, origin, teleportCmd);

            // Send public Discord message if enabled
            if (configData.Logging.PublicChannel.Enabled && IsValidWebhookUrl(configData.Logging.PublicChannel.PublicWebhookURL))
            {
                SendPublicDiscordEmbed($"Celestial barrage started at {gridRef}!", intensity, gridRef, true);
            }

            // Send in-game notifications (independent of Discord)
            if (configData.Options.InGamePlayerEventNotifications)
            {
                // Broadcast location info to all players
                foreach (var player in BasePlayer.activePlayerList)
                {
                    switch (intensity.ToLower())
                    {
                        case "mild":
                            player.ChatMessage($"<color=#32CD32>CELESTIAL BARRAGE (Mild)</color> started at grid <color=#FFFFFF>{gridRef}</color>");
                            break;
                        case "extreme":
                            player.ChatMessage($"<color=#DC143C>CELESTIAL BARRAGE (Extreme)</color> started at grid <color=#FFFFFF>{gridRef}</color>");
                            break;
                        case "medium":
                        default:
                            player.ChatMessage($"<color=#FFD700>CELESTIAL BARRAGE (Medium)</color> started at grid <color=#FFFFFF>{gridRef}</color>");
                            break;
                    }
                }
            }

            // Create map marker if enabled
            if (configData.Options.VisualEffects.ShowEventMapMarkers)
            {
                CreateMapMarker(origin, intensity, duration);
            }


            timer.Repeat(intervals, numberOfRockets, () => RandomRocket(origin, radius, setting));
            
            // Schedule the end event hook to fire after all rockets have been spawned + 15 second buffer
            timer.Once(duration + 15f, () => {
                string endMessage = $"========== METEOR SHOWER ENDED ===========\n" +
                                  $"Type: {eventType} ({intensity})\n" +
                                  $"Location: ({origin.x:F0}, {origin.z:F0}) Grid: {gridRef}\n" +
                                  $"Teleport: {teleportCmd}\n" +
                                  $"==========================================";

                // Send enhanced Discord end embed to admin
                SendEnhancedDiscordMessage(false, eventType, intensity, gridRef, numberOfRockets, duration, radius, origin, teleportCmd);
                
                // Send public Discord end message if enabled
                if (configData.Logging.PublicChannel.Enabled && IsValidWebhookUrl(configData.Logging.PublicChannel.PublicWebhookURL))
                {
                    SendPublicDiscordEmbed($"Celestial barrage ended at {gridRef}", intensity, gridRef, false);
                }
                
                // Send in-game notifications (independent of Discord)
                if (configData.Options.InGamePlayerEventNotifications)
                {
                    // Broadcast end to all players
                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        player.ChatMessage($"<color=#20B2AA>Celestial barrage ended at grid {gridRef}</color>");
                    }
                }
                
                // Remove map marker if it exists
                // Markers auto-remove via timer, no manual cleanup needed
            });
        }

        private void SendEnhancedDiscordMessage(bool isStart, string eventType, string intensity, string gridRef, int numberOfRockets, float duration, float radius, Vector3 origin, string teleportCmd)
        {
            // Only send if admin Discord is enabled and event messages are included
            if (!configData.Logging.AdminChannel.Enabled || !configData.Logging.AdminChannel.IncludeEventMessages || !IsValidWebhookUrl(configData.Logging.AdminChannel.PrivateAdminWebhookURL))
                return;

            float currentFPS = 1f / UnityEngine.Time.unscaledDeltaTime;
            int playersOnline = BasePlayer.activePlayerList.Count;

            var embed = new object();

            if (isStart)
            {
                // Enhanced Started Message - WITH PROPER SECTION SPACING
                embed = new
                {
                    title = GetIntensityIcon(intensity) + " Celestial Barrage Event",
                    description = "🟢 **STARTED**",
                    color = GetIntensityColor(intensity, true),
                    fields = new[]
                    {
                        new { name = "⚡ **Event Information**", value = "", inline = false },
                        new { name = "", value = $"🎯 **Event Type:** `{eventType} ({intensity})`", inline = false },
                        new { name = "", value = $"📍 **Grid Location:** `{gridRef}`", inline = false },
                        
                        // SPACING BETWEEN SECTIONS
                        new { name = "", value = "", inline = false },
                        
                        new { name = "📊 **Event Statistics**", value = "", inline = false },
                        new { name = "", value = $"🚀 **Rocket Count:** `{numberOfRockets}`", inline = false },
                        new { name = "", value = $"⏱️ **Duration:** `{duration:F0}s`", inline = false },
                        new { name = "", value = $"📏 **Impact Radius:** `{radius}m`", inline = false },
                        
                        // SPACING BETWEEN SECTIONS
                        new { name = "", value = "", inline = false },
                        
                        new { name = "⚙️ **Server Status**", value = "", inline = false },
                        new { name = "", value = $"🖥️ **Server FPS:** `{currentFPS:F1}`", inline = false },
                        new { name = "", value = $"👥 **Players Online:** `{playersOnline}`", inline = false },
                        
                        // SPACING BETWEEN SECTIONS
                        new { name = "", value = "", inline = false },
                        
                        new { name = "🗺️ **Location Data**", value = "", inline = false },
                        new { name = "", value = $"🔗 **Teleport Command:** `{teleportCmd}`", inline = false }
                    },
                    timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    footer = new { text = $"Celestial Barrage v{Version}" }
                };
            }
            else
            {
                // Enhanced Ended Message - WITH PROPER SECTION SPACING
                embed = new
                {
                    title = "🌟 Celestial Barrage Event",
                    description = "🔴 **COMPLETED**",
                    color = 0x27AE60, // Success green
                    fields = new[]
                    {
                        new { name = "📋 **Event Summary**", value = "", inline = false },
                        new { name = "", value = $"🎯 **Event Type:** `{eventType} ({intensity})`", inline = false },
                        new { name = "", value = $"📍 **Grid Location:** `{gridRef}`", inline = false },
                        
                        // SPACING BETWEEN SECTIONS
                        new { name = "", value = "", inline = false },
                        
                        new { name = "🗺️ **Location Data**", value = "", inline = false },
                        new { name = "", value = $"🔗 **Teleport Command:** `{teleportCmd}`", inline = false }
                    },
                    timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    footer = new { text = $"Celestial Barrage v{Version}" }
                };
            }

            var payload = new
            {
                username = "Celestial Barrage Monitor",
                avatar_url = "https://cdn.discordapp.com/emojis/1234567890123456789.png",
                embeds = new[] { embed }
            };

            string json = JsonConvert.SerializeObject(payload);
            var headers = new Dictionary<string, string> { { "Content-Type", "application/json" } };

            webrequest.Enqueue(configData.Logging.AdminChannel.PrivateAdminWebhookURL, json, (code, response) =>
            {
                if (code != 200 && code != 204)
                {
                    PrintError($"Failed to send enhanced Discord embed to Admin webhook. Response code: {code}");
                    if (configData.Logging.LogDebugToConsole)
                    {
                        Puts($"Admin Discord webhook error response: {response}");
                    }
                }
            }, this, Core.Libraries.RequestMethod.POST, headers);
        }

        private string GetIntensityIcon(string intensity)
        {
            switch (intensity.ToLower())
            {
                case "mild":
                    return "💫"; // Changed from 🌿 to 💫
                case "medium":
                    return "⚡"; // Electric energy
                case "extreme":
                    return "🔥"; // Intense fire
                default:
                    return "☄️"; // Default meteor
            }
        }

        private int GetIntensityColor(string intensity, bool isStart)
        {
            if (isStart)
            {
                switch (intensity.ToLower())
                {
                    case "mild":
                        return 0x2ECC71; // Emerald green
                    case "medium":
                        return 0xF39C12; // Orange
                    case "extreme":
                        return 0xE74C3C; // Crimson red
                    default:
                        return 0x3498DB; // Blue
                }
            }
            else
            {
                return 0x27AE60; // Success green for completed events
            }
        }

        private void RandomRocket(Vector3 origin, float radius, ConfigData.Settings setting)
        {
            try
            {
                bool isFireRocket = false;
                Vector2 rand = UnityEngine.Random.insideUnitCircle;
                Vector3 offset = new Vector3(rand.x * radius, 0, rand.y * radius);

                Vector3 direction = (Vector3.up * -2.0f + Vector3.right).normalized;
                Vector3 launchPos = origin + offset - direction * 200;

                if (RandomRange(1, setting.FireRocketChance) == 1)
                    isFireRocket = true;

                BaseEntity rocket = CreateRocket(launchPos, direction, isFireRocket, setting);

                // Check if rocket creation failed
                if (rocket == null)
                {
                    PrintWarning("Failed to create meteor rocket - skipping this rocket spawn");
                    return;
                }

                if (setting.ItemDropControl.EnableItemDrop)
                {
                    var comp = rocket.gameObject.AddComponent<ItemCarrier>();
                    comp.SetCarriedItems(setting.ItemDropControl.ItemsToDrop);
                    comp.SetDropMultiplier(configData.IntensitySettings.ItemDropMultiplier);
                }

                // If grenade launcher override occurred, spawn bonus original projectile
                if (wasGrenadeLauncherOverride)
                {
                    timer.Once(UnityEngine.Random.Range(1f, 3f), () => {
                        // Create random offset position
                        Vector2 bonusRand = UnityEngine.Random.insideUnitCircle;
                        Vector3 bonusOffset = new Vector3(bonusRand.x * radius, 0, bonusRand.y * radius);
                        Vector3 bonusLaunchPos = origin + bonusOffset - direction * 200;

                        // Spawn another random projectile (easiest approach)
                        RandomRocket(origin, radius, setting);
                    });
                }
            }
            catch (System.Exception ex)
            {
                PrintError($"Error creating rocket: {ex.Message}");
            }
        }

        private void SpreadRocket(Vector3 origin, Vector3 direction)
        {
            var barrageSpread = configData.BarrageSettings.RocketSpread;
            direction = Quaternion.Euler(UnityEngine.Random.Range((float)(-(double)barrageSpread * 0.5), barrageSpread * 0.5f), UnityEngine.Random.Range((float)(-(double)barrageSpread * 0.5), barrageSpread * 0.5f), UnityEngine.Random.Range((float)(-(double)barrageSpread * 0.5), barrageSpread * 0.5f)) * direction;
            BaseEntity rocket = CreateRocket(origin, direction, false, configData.IntensitySettings.Medium);
            
            // Check if rocket creation failed
            if (rocket == null)
            {
                PrintWarning("Failed to create barrage rocket - skipping this rocket spawn");
                return;
            }
        }

        private BaseEntity CreateRocket(Vector3 startPoint, Vector3 direction, bool isFireRocket, ConfigData.Settings setting)
        {
            ItemDefinition projectileItem = null;
            wasGrenadeLauncherOverride = false; // Reset flag
         
            if (isFireRocket)
            {
                // 1% MLRS, 40% fire rocket, 59% catapult incendiary boulder
                float roll = UnityEngine.Random.Range(0f, 1f);
                if (roll < 0.01f)
                    projectileItem = ItemManager.FindItemDefinition("ammo.rocket.mlrs");
                else if (roll < 0.41f) // 0.01 + 0.40 = 0.41
                    projectileItem = ItemManager.FindItemDefinition("ammo.rocket.fire");
                else
                    projectileItem = ItemManager.FindItemDefinition("catapult.ammo.incendiary");
            }
            else  
            {
                // 20% basic, 20% hv, 20% smoke rocket, 40% catapult boulder
                float roll = UnityEngine.Random.Range(0f, 1f);
                if (roll < 0.2f)
                    projectileItem = ItemManager.FindItemDefinition("ammo.rocket.basic");
                else if (roll < 0.4f)
                    projectileItem = ItemManager.FindItemDefinition("ammo.rocket.hv");
                else if (roll < 0.6f)
                    projectileItem = ItemManager.FindItemDefinition("ammo.rocket.smoke");
                else
                    projectileItem = ItemManager.FindItemDefinition("catapult.ammo.boulder");
            }

            // 5% chance to override with grenade launcher (50/50 smoke or HE)
            if (UnityEngine.Random.Range(0f, 1f) < 0.05f)
            {
                wasGrenadeLauncherOverride = true; // Set flag
                if (UnityEngine.Random.Range(0f, 1f) < 0.5f)
                    projectileItem = ItemManager.FindItemDefinition("ammo.grenadelauncher.smoke");
                else
                    projectileItem = ItemManager.FindItemDefinition("ammo.grenadelauncher.he");
            }

            // VALIDATION: Fallback to basic rocket if item not found
            if (projectileItem == null)
            {
                PrintWarning("Failed to find projectile item, falling back to basic rocket");
                projectileItem = ItemManager.FindItemDefinition("ammo.rocket.basic");
                
                // If even basic rocket fails, try one more fallback
                if (projectileItem == null)
                {
                    string reason = "Critical error: Missing rocket ammo items";
                    string additionalInfo = "Unable to find any rocket projectiles in item definitions";
                    SendSkippedEventDiscord(reason, "System Check", additionalInfo);

                    PrintError("Critical: Cannot find any rocket ammo items! Meteor event cancelled.");
                    return null; // Return null to indicate failure
                }
            }

            // Debug logging for projectile creation
            if (configData?.Logging?.LogDebugToConsole == true)
            {
                Puts($"[DEBUG] Creating projectile: {projectileItem.shortname}");
            }

            // Validate component exists
            ItemModProjectile component = projectileItem.GetComponent<ItemModProjectile>();
            if (component == null)
            {
                PrintError($"Critical: {projectileItem.shortname} has no ItemModProjectile component!");
                return null;
            }

            BaseEntity entity = GameManager.server.CreateEntity(component.projectileObject.resourcePath, startPoint, new Quaternion(), true);

            TimedExplosive timedExplosive = entity.GetComponent<TimedExplosive>();
            ServerProjectile serverProjectile = entity.GetComponent<ServerProjectile>();

            serverProjectile.gravityModifier = 0;
            serverProjectile.speed = 25;
            timedExplosive.timerAmountMin = 300;
            timedExplosive.timerAmountMax = 300;
            ScaleAllDamage(timedExplosive.damageTypes, setting.DamageMultiplier);

            // Add visual effects if enabled (simplified approach)
            if (configData.Options.VisualEffects.EnableParticleTrails)
            {
                AddVisualTrail(entity, projectileItem.shortname);
            }

            serverProjectile.InitializeVelocity(direction.normalized * 25);
            entity.Spawn();
            
            // Track this rocket as one of ours
            meteorRockets.Add(entity);
            
            // Debug logging for tracking
            if (configData?.Logging?.LogDebugToConsole == true)
            {
                Puts($"[DEBUG] Tracking meteor projectile: {entity.ShortPrefabName} (Total tracked: {meteorRockets.Count})");
            }
            
            return entity;
        }

        private void AddVisualTrail(BaseEntity rocket, string projectileType)
        {
            string effectPath = GetTrailEffectForProjectile(projectileType);

            // Skip if no effect specified
            if (string.IsNullOrEmpty(effectPath))
                return;

            float baseFrequency = GetTrailFrequencyForProjectile(projectileType);
            int iterations = GetTrailDurationForProjectile(projectileType);

            // Track spawn count for this rocket's trail
            int spawnCount = 0;

            // Create recursive function that generates new random delay for EACH effect spawn
            Action spawnNextEffect = null;
            spawnNextEffect = () => {
                // Check if rocket is still valid and we haven't reached max iterations
                if (rocket != null && !rocket.IsDestroyed && spawnCount < iterations)
                {
                    // Spawn the effect at current rocket position
                    Effect.server.Run(effectPath, rocket.transform.position, Vector3.up, null, false);
                    spawnCount++;

                    // Generate NEW random delay for THIS iteration (unique every time)
                    // Increased randomization range (±0.5) for more organic, less mechanical effect patterns
                    float randomizedFrequency = Mathf.Max(0f, baseFrequency + UnityEngine.Random.Range(-0.5f, 0.5f));

                    // Schedule the next effect spawn with the random delay
                    timer.Once(randomizedFrequency, spawnNextEffect);
                }
            };

            // Start the trail effect sequence
            spawnNextEffect();
        }

        private string GetTrailEffectForProjectile(string projectileType)
        {
            switch (projectileType)
            {
                case "ammo.rocket.fire":
                    return "assets/bundled/prefabs/fx/gas_explosion_small.prefab"; // Bigger explosion for fire
                case "ammo.rocket.hv": 
                    return "assets/content/vehicles/mlrs/effects/pfx_mlrs_backfire.prefab";
                case "ammo.rocket.mlrs":
                    return "assets/content/vehicles/mlrs/effects/pfx_mlrs_backfire.prefab";
                case "ammo.rocket.basic":
                    return "assets/bundled/prefabs/fx/build/promote_toptier.prefab";
                case "catapult.ammo.incendiary":
                    return "assets/bundled/prefabs/fx/fire/fire_v2.prefab";
                case "ammo.rocket.smoke":
                    return "assets/bundled/prefabs/fx/fire/fire_v3.prefab";
                case "catapult.ammo.boulder":
                    return "assets/bundled/prefabs/fx/weapons/landmine/landmine_explosion.prefab";
                case "ammo.grenadelauncher.smoke":
                    return "assets/content/effects/weather/pfx_lightning_medium.prefab";                
                case "ammo.grenadelauncher.he":
                    return "assets/content/effects/weather/pfx_lightning_strong.prefab";  
                default:
                    return "assets/bundled/prefabs/fx/build/promote_toptier.prefab"; // Standard fallback
            }
        }

        private float GetTrailFrequencyForProjectile(string projectileType)
        {
            switch (projectileType)
            {
                case "ammo.rocket.hv": 
                    return 0.95f;
                case "ammo.rocket.mlrs":
                    return 0.95f;
                case "ammo.rocket.fire":
                    return 0.95f;
                case "ammo.rocket.basic":
                    return 0.45f;                
                case "ammo.grenadelauncher.he":
                    return 0.95f;
                case "catapult.ammo.incendiary":
                    return 0.25f;
                case "ammo.rocket.smoke":
                    return 0.15f;
                case "ammo.grenadelauncher.smoke":
                    return 0.95f;
                case "catapult.ammo.boulder":
                    return 2.5f;
                default:
                    return 0.45f; // Standard fallback
            }
        }

        private int GetTrailDurationForProjectile(string projectileType)
        {
            switch (projectileType)
            {
                case "ammo.rocket.hv": 
                    return 5;
                case "ammo.rocket.mlrs":
                    return 5;
                case "ammo.rocket.fire":
                    return 5;
                case "ammo.rocket.smoke":
                    return 45;                
                case "ammo.grenadelauncher.smoke":
                    return 10;
                case "catapult.ammo.boulder":
                    return 6;                
                case "catapult.ammo.incendiary":
                    return 30;
                case "ammo.rocket.basic":
                    return 30;                
                case "ammo.grenadelauncher.he":
                    return 10;                
                default:
                    return 15;
            }
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
            bool isPlayer = false;
            string damageSource = "";

            // Determine what weapon/projectile caused the damage
            if (info?.WeaponPrefab != null)
            {
                damageSource = info.WeaponPrefab.ShortPrefabName;
            }

            float totalDamage = info?.damageTypes?.Total() ?? 0f;
            bool isCatapultImpact = damageSource.Contains("boulder") || damageSource.Contains("catapult");
            bool isGrenadeImpact = damageSource.Contains("40mm") || damageSource.Contains("grenade");

            // Enhanced NPC filtering - check for known NPC types first
            if (IsNPCEntity(entity))
            {
                // This is an NPC - don't log it
                return;
            }

            // Determine what was hit
            if (entity is BasePlayer)
            {
                var player = entity as BasePlayer;

                // Additional check to filter out NPC players (scientists, etc.)
                if (player.IsNpc || player.userID < 76561197960265728L)
                {
                    // This is an NPC player - don't log it
                    return;
                }

                entityType = "Player";
                ownerInfo = player.displayName;
                isPlayer = true;
                
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
                ownerInfo = ownerPlayer != null ? ownerPlayer.displayName : $"OwnerID: {entity.OwnerID}";
                
                // Screen shake for nearby players if enabled
                if (configData.Options.VisualEffects.EnableScreenShake)
                {
                    ApplyScreenShakeToNearbyPlayers(entity.transform.position, 50f, 0.5f);
                }
            }
            else
            {
                // Natural/unowned entity - don't log these
                return;
            }

            // Apply damage threshold — players always pass, structures/deployables must meet minimum
            if (!isPlayer && totalDamage < configData.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold)
                return;

            // Log and fire hook for players or player structures only
            if (isPlayer || isPlayerStructure)
            {
                string damageInfo = $"Damage: {totalDamage:F1}";
                Vector3 pos = entity.transform.position;
                string teleportCmd = $"teleportpos {pos.x:F1} {pos.y:F1} {pos.z:F1}";
                
                // Enhanced debug logging
                if (configData?.Logging?.LogDebugToConsole == true)
                {
                    string projectileType = "Unknown";
                    if (damageSource.Contains("rocket")) projectileType = "Rocket";
                    else if (isCatapultImpact) projectileType = "Catapult";
                    else if (isGrenadeImpact) projectileType = "Grenade";

                    Puts($"[DEBUG] LOGGING IMPACT - Type: {projectileType}, Weapon: {damageSource}, Damage: {totalDamage:F1}");
                }

                // Console logging for admin monitoring
                if (configData.Logging.LogDebugToConsole)
                {
                    string consoleMessage = isPlayer ?
                        $"METEOR IMPACT: {entityType} - Player: {ownerInfo} - Weapon: {damageSource} - {damageInfo}" :
                        $"METEOR IMPACT: {entityType} - Owner: {ownerInfo} - Weapon: {damageSource} - {damageInfo}";
                    Puts(consoleMessage);
                }

                // Send beautiful Discord embed message
                SendMeteorImpactDiscord(entityType, ownerInfo, damageSource, damageInfo, teleportCmd, isPlayer, isPlayerStructure);
                
                // Fire the hook for other plugins
                Interface.CallHook("OnCelestialBarrageImpact", entity, info, entityType, ownerInfo);
            }
        }

        private bool IsNPCEntity(BaseCombatEntity entity)
        {
            if (entity == null) return false;
            
            // Check if it's an NPC player first
            if (entity is BasePlayer player)
            {
                return player.IsNpc || player.userID < 76561197960265728L;
            }
            
            // Check for common NPC entity types by prefab name
            string prefabName = entity.ShortPrefabName.ToLower();
            
            // List of known NPC entities that should be filtered
            string[] npcPrefabs = {
                "scientist",           // Scientists
                "murderer",           // Murderer NPCs
                "bandit",             // Bandit NPCs
                "dweller",            // Tunnel dwellers
                "scarecrow",          // Scarecrows
                "zombie",             // Zombies (if any)
                "bear",               // Bears
                "wolf",               // Wolves
                "chicken",            // Chickens
                "stag",               // Deer/Stags
                "boar",               // Boars
                "horse",              // Horses
                "ridablehorse",       // Ridable horses
                "shark",              // Sharks
                "simpleshark",        // Simple sharks
                "bradley",            // Bradley APC
                "patrolhelicopter",   // Patrol helicopter
                "ch47",               // Chinook helicopter
                "cargoship",          // Cargo ship
                "oilfireball",        // Oil rig fireball
                "heavyscientist",     // Heavy scientists
                "scientistnpc",       // Scientist NPCs
                "tunneldweller",      // Tunnel dwellers
                "underwaterdweller",  // Underwater dwellers
                "npc",                // Generic NPC
                "pet"                 // Pets
            };
            
            // Check if the entity prefab name contains any NPC identifiers
            foreach (string npcType in npcPrefabs)
            {
                if (prefabName.Contains(npcType))
                {
                    return true;
                }
            }
            
            // Additional check for entities that might be NPCs but have OwnerIDs
            // (like tamed animals or NPCs spawned by other plugins)
            if (entity.OwnerID != 0)
            {
                // Check if the "owner" is actually an NPC rather than a real player
                var owner = BasePlayer.FindByID(entity.OwnerID);
                if (owner != null && (owner.IsNpc || owner.userID < 76561197960265728L))
                {
                    return true;
                }
            }
            
            return false;
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
            if (!configData.Options.VisualEffects.ShowEventMapMarkers)
                return;

            try
            {
                string gridRef = GetGridReference(position);
                
                // Create a colored circle marker FIRST
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
                            radiusMarker.radius = 0.55f;
                            radiusMarker.alpha = 0.6f;
                            break;
                        case "extreme":
                            // Red circle
                            radiusMarker.color1 = new Color(1f, 0f, 0f, 1f);
                            radiusMarker.radius = 0.3f;
                            radiusMarker.alpha = 0.6f;
                            break;
                        case "medium":
                        default:
                            // Yellow circle
                            radiusMarker.color1 = new Color(1f, 1f, 0f, 1f);
                            radiusMarker.radius = 0.4f;
                            radiusMarker.alpha = 0.6f;
                            break;
                    }
                    
                    // Send updates AFTER setting properties
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
                            markerText = $"Celestial Barrage (Mild) - {gridRef}";
                            break;
                        case "extreme":
                            markerText = $"Celestial Storm (Extreme) - {gridRef}";
                            break;
                        case "medium":
                        default:
                            markerText = $"Celestial Barrage (Medium) - {gridRef}";
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

            }
            catch (System.Exception ex)
            {
                // Only log actual errors, not successful marker creation
                PrintError($"Error creating map marker: {ex.Message}");
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
        }

        private void LogMessage(string message, string logType)
        {
            // Always log to console if enabled
            if (configData.Logging.LogDebugToConsole)
            {
                Puts(message);
            }

            // Send to Discord webhooks based on type and settings
            if (logType == "admin" && configData.Logging.AdminChannel.Enabled && IsValidWebhookUrl(configData.Logging.AdminChannel.PrivateAdminWebhookURL))
            {
                QueueDiscordMessage(configData.Logging.AdminChannel.PrivateAdminWebhookURL, message, true, "admin");
            }
        }

        private bool IsValidWebhookUrl(string url)
        {
            return !string.IsNullOrEmpty(url) && 
                   url.StartsWith("https://discord.com/api/webhooks/") && 
                   url.Length > 50;
        }

        private void SendPublicDiscordEmbed(string message, string intensity, string gridRef, bool isStart)
        {
            // Determine embed color based on intensity and event type
            int color;
            string title;
            
            if (isStart)
            {
                title = "☄️ Celestial Barrage Started";
                switch (intensity.ToLower())
                {
                    case "mild":
                        color = 0x2ecc71; // Green for mild
                        break;
                    case "extreme":
                        color = 0xe74c3c; // Red for extreme
                        break;
                    case "medium":
                    default:
                        color = 0xf1c40f; // Yellow for medium
                        break;
                }
            }
            else
            {
                title = "🌠 Celestial Barrage Ended";
                color = 0x95a5a6; // Gray for ended
            }

            var embed = new
            {
                title = title,
                description = message,
                color = color,
                timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                fields = new[]
                {
                    new
                    {
                        name = "Intensity",
                        value = intensity,
                        inline = false
                    },
                    new
                    {
                        name = "Grid Reference",
                        value = gridRef,
                        inline = false
                    }
                },
                footer = new { text = $"Celestial Barrage v{Version}" }
            };

            var payload = new
            {
                username = "Celestial Barrage",
                avatar_url = "https://i.imgur.com/meteor.png",
                embeds = new[] { embed }
            };

            string json = JsonConvert.SerializeObject(payload);
            var headers = new Dictionary<string, string> { { "Content-Type", "application/json" } };

            webrequest.Enqueue(configData.Logging.PublicChannel.PublicWebhookURL, json, (code, response) =>
            {
                if (code != 200 && code != 204)
                {
                    PrintError($"Failed to send Discord embed to Public webhook. Response code: {code}");
                    if (configData.Logging.LogDebugToConsole)
                    {
                        Puts($"Public Discord webhook error response: {response}");
                    }
                }
            }, this, Core.Libraries.RequestMethod.POST, headers);
        }

        private void SendSkippedEventDiscord(string reason, string eventType, string additionalInfo = "")
        {
            // Only send if admin Discord is enabled and event messages are included
            if (!configData.Logging.AdminChannel.Enabled || !configData.Logging.AdminChannel.IncludeEventMessages || !IsValidWebhookUrl(configData.Logging.AdminChannel.PrivateAdminWebhookURL))
                return;

            // Create rich embed for skipped event
            var embed = new
            {
                title = "⏭️ Event Skipped",
                color = 0x95a5a6, // Gray color for skipped events
                timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                fields = new[]
                {
                    new { name = "❌ Reason", value = reason, inline = true },
                    new { name = "🎮 Event Type", value = eventType, inline = true },
                    new { name = "📅 Time", value = $"<t:{((System.DateTimeOffset)System.DateTime.UtcNow).ToUnixTimeSeconds()}:F>", inline = false }
                }
            };

            // Add additional info field if provided
            var fieldsList = embed.fields.ToList();
            if (!string.IsNullOrEmpty(additionalInfo))
            {
                fieldsList.Add(new { name = "ℹ️ Details", value = additionalInfo, inline = false });
            }

            var finalEmbed = new
            {
                title = embed.title,
                color = embed.color,
                timestamp = embed.timestamp,
                fields = fieldsList.ToArray(),
                footer = new { text = $"Celestial Barrage v{Version}" }
            };

            var payload = new
            {
                embeds = new[] { finalEmbed }
            };

            var json = JsonConvert.SerializeObject(payload);
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };

            webrequest.Enqueue(configData.Logging.AdminChannel.PrivateAdminWebhookURL, json, (code, response) =>
            {
                if (code != 200 && code != 204)
                {
                    PrintError($"Failed to send skipped event Discord embed. Response code: {code}");
                    if (configData.Logging.LogDebugToConsole)
                    {
                        Puts($"Discord webhook error response: {response}");
                    }
                }
            }, this, Core.Libraries.RequestMethod.POST, headers);
        }

        private void SendMeteorImpactDiscord(string entityType, string ownerInfo, string damageSource, string damageInfo, string teleportCmd, bool isPlayer, bool isPlayerStructure)
        {
            // Only send if admin Discord is enabled and impact messages are included
            if (!configData.Logging.AdminChannel.Enabled || !configData.Logging.AdminChannel.IncludeImpactMessages || !IsValidWebhookUrl(configData.Logging.AdminChannel.PrivateAdminWebhookURL))
                return;

            // Check if this type of impact should be logged
            if (isPlayer && !configData.Logging.AdminChannel.ImpactFiltering.LogPlayerImpacts)
                return;
            if (isPlayerStructure && !configData.Logging.AdminChannel.ImpactFiltering.LogStructureImpacts)
                return;

            // Determine embed properties based on impact type
            int color;
            string title;
            string targetIcon;
            string targetLabel;

            if (isPlayer)
            {
                color = 0xe74c3c; // Red for player impacts
                title = "💥 Player Impact";
                targetIcon = "👤";
                targetLabel = "Player";
            }
            else if (isPlayerStructure)
            {
                color = 0xf39c12; // Orange for structure impacts
                title = "🏗️ Structure Impact";
                targetIcon = "🏠";
                targetLabel = "Building";
            }
            else
            {
                color = 0x3498db; // Blue for other entities
                title = "🎯 Entity Impact";
                targetIcon = "🎪";
                targetLabel = "Entity";
            }

            // Create rich embed for meteor impact
            var embed = new
            {
                title = title,
                color = color,
                timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                fields = new[]
                {
                    new { name = $"{targetIcon} {targetLabel}", value = isPlayer ? ownerInfo : $"{entityType}\nOwner: {ownerInfo}", inline = true },
                    new { name = "💀 Damage", value = damageInfo, inline = true },
                    new { name = "🚀 Weapon", value = damageSource, inline = true },
                    new { name = "🎮 Teleport", value = $"`{teleportCmd}`", inline = false }
                },
                footer = new { text = $"Celestial Barrage v{Version}" }
            };

            var payload = new
            {
                embeds = new[] { embed }
            };

            var json = JsonConvert.SerializeObject(payload);
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };

            // Send directly using webrequest to avoid conflicts with plain text queue system
            webrequest.Enqueue(configData.Logging.AdminChannel.PrivateAdminWebhookURL, json, (code, response) =>
            {
                if (code != 200 && code != 204)
                {
                    PrintError($"Failed to send meteor impact Discord embed. Response code: {code}");
                    if (configData.Logging.LogDebugToConsole)
                    {
                        Puts($"Discord webhook error response: {response}");
                    }
                }
            }, this, Core.Libraries.RequestMethod.POST, headers);
        }

        private void SendDiscordEmbed(string webhookUrl, string message, bool isAdmin)
        {
            // This method now just calls the direct sending method
            // Rate limiting is handled at the queue level
            SendDiscordEmbedDirect(webhookUrl, message, isAdmin);
        }

        private void SendDiscordEmbedWithQueueInfo(string webhookUrl, string message, bool isAdmin, float queueDelay, int remainingCount)
        {
            // Determine embed color and title based on message content
            int color = 0x3498db; // Default blue
            string title = "Celestial Barrage";
            string description = message;

            if (message.Contains("CELESTIAL BARRAGE STARTED"))
            {
                color = 0xe74c3c; // Red for start
                title = "☄️ Celestial Barrage Started";
            }
            else if (message.Contains("CELESTIAL BARRAGE ENDED"))
            {
                color = 0x2ecc71; // Green for end
                title = "🌠 Celestial Barrage Ended";
            }
            else if (message.Contains("METEOR IMPACT"))
            {
                color = 0xf39c12; // Orange for impacts
                if (message.Contains("METEOR IMPACT: Player"))
                    title = "👤 Player Impact";
                else
                    title = "🏠 Structure Impact";
            }
            else if (message.Contains("SKIPPED") || message.Contains("CANCELLED"))
            {
                color = 0x95a5a6; // Gray for skipped
                title = "⏭️ Event Skipped";
            }

            var embedFields = new List<object>();
            
            // Add queue info formatted like other messages with each data point on new lines
            embedFields.Add(new
            {
                name = "⏱️ Message Queue Info",
                value = $"Queued for: {queueDelay:F0}s\nMessages Remaining: {remainingCount}",
                inline = false
            });

            var embed = new
            {
                title = title,
                description = $"```{description}```",
                color = color,
                timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                fields = embedFields.ToArray(),
                footer = new { text = $"Celestial Barrage v{Version}" }
            };

            var payload = new
            {
                username = "Celestial Barrage",
                avatar_url = "https://i.imgur.com/meteor.png",
                embeds = new[] { embed }
            };

            string json = JsonConvert.SerializeObject(payload);
            var headers = new Dictionary<string, string> { { "Content-Type", "application/json" } };

            webrequest.Enqueue(webhookUrl, json, (code, response) =>
            {
                if (code != 200 && code != 204)
                {
                    string webhookType = isAdmin ? "Private Admin" : "Public";
                    PrintError($"Failed to send Discord embed to {webhookType} webhook. Response code: {code}");
                    if (configData.Logging.LogDebugToConsole)
                    {
                        Puts($"Discord webhook error response: {response}");
                    }
                }
            }, this, Core.Libraries.RequestMethod.POST, headers);
        }

        private void SendDiscordEmbedDirect(string webhookUrl, string message, bool isAdmin)
        {
            // Determine embed color and title based on message content
            int color = 0x3498db; // Default blue
            string title = "Celestial Barrage";
            string description = message;

            if (message.Contains("CELESTIAL BARRAGE STARTED"))
            {
                color = 0xe74c3c; // Red for start
                title = "☄️ Celestial Barrage Started";
            }
            else if (message.Contains("CELESTIAL BARRAGE ENDED"))
            {
                color = 0x2ecc71; // Green for end
                title = "🌠 Celestial Barrage Ended";
            }
            if (message.Contains("METEOR IMPACT"))
            {
                color = 0xf39c12; // Orange for impacts
                if (message.Contains("METEOR IMPACT: Player"))
                    title = "👤 Player Impact";
                else
                    title = "🏠 Structure Impact";
            }
            else if (message.Contains("SKIPPED") || message.Contains("CANCELLED"))
            {
                color = 0x95a5a6; // Gray for skipped
                title = "⏭️ Event Skipped";
            }

            var embed = new
            {
                title = title,
                description = $"```{description}```",
                color = color,
                timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                footer = new { text = $"Celestial Barrage v{Version}" }
            };

            var payload = new
            {
                username = "Celestial Barrage",
                avatar_url = "https://i.imgur.com/meteor.png",
                embeds = new[] { embed }
            };

            string json = JsonConvert.SerializeObject(payload);
            var headers = new Dictionary<string, string> { { "Content-Type", "application/json" } };

            webrequest.Enqueue(webhookUrl, json, (code, response) =>
            {
                if (code != 200 && code != 204)
                {
                    string webhookType = isAdmin ? "Private Admin" : "Public";
                    PrintError($"Failed to send Discord embed to {webhookType} webhook. Response code: {code}");
                    if (configData.Logging.LogDebugToConsole)
                    {
                        Puts($"Discord webhook error response: {response}");
                    }
                }
            }, this, Core.Libraries.RequestMethod.POST, headers);
        }
        #endregion

        #region Config editing
        // Config editing methods removed - use config file directly
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
                

            bool isSpectating = player.HasPlayerFlag(BasePlayer.PlayerFlags.Spectating);

            switch (args[0].ToLower())
            {
                case "onplayer":
                    if (args.Length == 2)
                    {
                        if (StartRandomOnPlayer(args[1], "Admin on Player", player, skipFpsCheck: true))
                        {
                            if (!isSpectating)
                                SendReply(player, string.Format(msg("calledOn", player.UserIDString), args[1]));
                        }
                        else
                            SendReply(player, msg("noPlayer", player.UserIDString));
                    }
                    else
                    {
                        StartRandomOnPosition(player.transform.position, "Admin on Position", skipFpsCheck: true);
                        if (!isSpectating)
                            SendReply(player, msg("onPos", player.UserIDString));
                    }
                    break;

                case "onplayer_extreme":
                    if (args.Length == 2)
                    {
                        if (StartOnPlayer(args[1], configData.IntensitySettings.Extreme, "Admin on Player", player, skipFpsCheck: true))
                        {
                            if (!isSpectating)
                                SendReply(player, msg("Extreme", player.UserIDString) + string.Format(msg("calledOn", player.UserIDString), args[1]));
                        }
                        else
                            SendReply(player, msg("noPlayer", player.UserIDString));
                    }
                    else
                    {
                        StartCelestialEvent(player.transform.position, configData.IntensitySettings.Extreme, "Admin on Position", skipFpsCheck: true);
                        if (!isSpectating)
                            SendReply(player, msg("Extreme", player.UserIDString) + msg("onPos", player.UserIDString));
                    }
                    break;

                case "onplayer_medium":
                    if (args.Length == 2)
                    {
                        if (StartOnPlayer(args[1], configData.IntensitySettings.Medium, "Admin on Player", player, skipFpsCheck: true))
                        {
                            if (!isSpectating)
                                SendReply(player, msg("Medium", player.UserIDString) + string.Format(msg("calledOn", player.UserIDString), args[1]));
                        }
                        else
                            SendReply(player, msg("noPlayer", player.UserIDString));
                    }
                    else
                    {
                        StartCelestialEvent(player.transform.position, configData.IntensitySettings.Medium, "Admin on Position", skipFpsCheck: true);
                        if (!isSpectating)
                            SendReply(player, msg("Medium", player.UserIDString) + msg("onPos", player.UserIDString));
                    }
                    break;

                case "onplayer_mild":
                    if (args.Length == 2)
                    {
                        if (StartOnPlayer(args[1], configData.IntensitySettings.Mild, "Admin on Player", player, skipFpsCheck: true))
                        {
                            if (!isSpectating)
                                SendReply(player, msg("Mild", player.UserIDString) + string.Format(msg("calledOn", player.UserIDString), args[1]));
                        }
                        else
                            SendReply(player, msg("noPlayer", player.UserIDString));
                    }
                    else
                    {
                        StartCelestialEvent(player.transform.position, configData.IntensitySettings.Mild, "Admin on Position", skipFpsCheck: true);
                        if (!isSpectating)
                            SendReply(player, msg("Mild", player.UserIDString) + msg("onPos", player.UserIDString));
                    }
                    break;

                case "barrage":
                    StartBarrage(player.eyes.position + player.eyes.HeadForward() * 1f, player.eyes.HeadForward());
                    break;

                case "random":
                    StartRandomOnMap(skipPlayerCount: true, skipFpsCheck: true);
                    if (!isSpectating)
                        SendReply(player, msg("randomCall", player.UserIDString));
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
                
                StartRandomOnMap(skipPlayerCount: true, skipFpsCheck: true);
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
                    StartRandomOnPosition(GetGroundPosition(position), "Console Command", skipFpsCheck: true);
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
        private BasePlayer GetPlayerByName(string name, BasePlayer caller = null)
        {
            BasePlayer foundPlayer = null;
            name = name.ToLower();
            int bestMatchLength = int.MaxValue;

            foreach (BasePlayer p in BasePlayer.activePlayerList)
            {
                string currentName = p.displayName.ToLower();
                if (currentName.Contains(name))
                {
                    int remainingLength = currentName.Replace(name, "").Length;
                    if (remainingLength < bestMatchLength)
                    {
                        bestMatchLength = remainingLength;
                        foundPlayer = p;
                    }
                }
            }

            foreach (BasePlayer p in BasePlayer.sleepingPlayerList)
            {
                string currentName = p.displayName.ToLower();
                if (currentName.Contains(name))
                {
                    int remainingLength = currentName.Replace(name, "").Length;
                    if (remainingLength < bestMatchLength)
                    {
                        bestMatchLength = remainingLength;
                        foundPlayer = p;
                    }
                }
            }

            // Always check caller — covers vanish plugins that remove admin from activePlayerList
            if (caller != null)
            {
                string currentName = caller.displayName.ToLower();
                if (currentName.Contains(name))
                {
                    int remainingLength = currentName.Replace(name, "").Length;
                    if (remainingLength < bestMatchLength)
                        foundPlayer = caller;
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
            float worldSize = World.Size;
            float grids = Mathf.Floor(worldSize / (1024f / 7f));

            position += new Vector3(worldSize / 2f, 0, worldSize / 2f);

            int col = Mathf.FloorToInt(position.x / worldSize * grids) + 1;
            int row = Mathf.FloorToInt(grids - (position.z / worldSize * grids));

            string letter = col > 26
                ? "A" + (char)(64 + (col % 26 == 0 ? 26 : col % 26))
                : "" + (char)(64 + (col % 26 == 0 ? 26 : col % 26));

            return $"{letter}{row}";
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

        private class QueuedDiscordMessage
        {
            public string WebhookUrl { get; set; }
            public string Message { get; set; }
            public bool IsAdmin { get; set; }
            public string MessageType { get; set; }
            public float QueuedTime { get; set; }
        }
        #endregion

        #region Config        
        private ConfigData configData;
        
        class ConfigData
        {
            public BarrageOptions BarrageSettings { get; set; } = new BarrageOptions();
            public ConfigOptions Options { get; set; } = new ConfigOptions();
            public LoggingOptions Logging { get; set; } = new LoggingOptions();
            public IntensityOptions IntensitySettings { get; set; } = new IntensityOptions();

            public class BarrageOptions
            {
                public int NumberOfRockets { get; set; } = 20;
                public float RocketDelay { get; set; } = 0.33f;
                public float RocketSpread { get; set; } = 16f;
            }

            public class Drops
            {
                public bool EnableItemDrop { get; set; } = true;
                public ItemDrop[] ItemsToDrop { get; set; } = new ItemDrop[0];
            }

            public class ConfigOptions
            {
                public bool EnableAutomaticEvents { get; set; } = true;
                public Timers EventTimers { get; set; } = new Timers();
                public bool InGamePlayerEventNotifications { get; set; } = true;
                public int MinimumPlayerCount { get; set; } = 3;
                public PerformanceSettings PerformanceMonitoring { get; set; } = new PerformanceSettings();
                public EffectsSettings VisualEffects { get; set; } = new EffectsSettings();
            }

            public class LoggingOptions
            {
                public bool LogDebugToConsole { get; set; } = false;
                public PublicChannelOptions PublicChannel { get; set; } = new PublicChannelOptions();
                public AdminChannelOptions AdminChannel { get; set; } = new AdminChannelOptions();
                public DiscordRateLimitOptions DiscordRateLimit { get; set; } = new DiscordRateLimitOptions();
            }

            public class PublicChannelOptions
            {
                public bool Enabled { get; set; } = false;
                [JsonProperty(PropertyName = "Webhook URL")]
                public string PublicWebhookURL { get; set; } = "https://discord.com/api/webhooks/YOUR_WEBHOOK_ID/YOUR_WEBHOOK_TOKEN";
            }

            public class AdminChannelOptions
            {
                [JsonProperty(PropertyName = "Enabled?")]
                public bool Enabled { get; set; } = false;
                [JsonProperty(PropertyName = "Include Event Messages?")]
                public bool IncludeEventMessages { get; set; } = false;
                [JsonProperty(PropertyName = "Include Impact Messages?")]
                public bool IncludeImpactMessages { get; set; } = true;
                [JsonProperty(PropertyName = "Webhook URL")]
                public string PrivateAdminWebhookURL { get; set; } = "https://discord.com/api/webhooks/YOUR_ADMIN_WEBHOOK_ID/YOUR_ADMIN_WEBHOOK_TOKEN";
                [JsonProperty(PropertyName = "Impact Filtering")]
                public ImpactFilteringOptions ImpactFiltering { get; set; } = new ImpactFilteringOptions();
            }

            public class ImpactFilteringOptions
            {
                [JsonProperty(PropertyName = "Log Player Impacts?")]
                public bool LogPlayerImpacts { get; set; } = true;
                [JsonProperty(PropertyName = "Log Structure Impacts?")]
                public bool LogStructureImpacts { get; set; } = true;
                [JsonProperty(PropertyName = "Minimum Impact Damage Threshold")]
                public float MinimumDamageThreshold { get; set; } = 50.0f;
            }

            public class DiscordRateLimitOptions
            {
                public bool EnableRateLimit { get; set; } = true;
                public float ImpactMessageCooldown { get; set; } = 1.0f;
                // Discord webhook per-channel limit is 30 messages/minute. Set this lower to avoid rate limiting.
                // Default 15 provides safety margin (15 message buffer) for other webhooks/messages to the same channel.
                public int MaxImpactsPerMinute { get; set; } = 15;
            }

            public class PerformanceSettings
            {
                public bool EnableFPSCheck { get; set; } = true;
                public float MinimumFPS { get; set; } = 40f;
            }

            public class EffectsSettings
            {
                public bool EnableScreenShake { get; set; } = true;
                public bool EnableParticleTrails { get; set; } = true;
                public bool ShowEventMapMarkers { get; set; } = true;
            }

            public class Timers
            {
                public int EventIntervalMinutes { get; set; } = 360;
                public bool UseRandomTimers { get; set; } = true;
            }

            public class Settings
            {
                public float DamageMultiplier { get; set; }
                public int FireRocketChance { get; set; }
                public float Radius { get; set; }
                public int RocketAmount { get; set; }
                public float DurationSecondsMin { get; set; }
                public float DurationSecondsMax { get; set; }
                public Drops ItemDropControl { get; set; } = new Drops();
            }

            public class SpawnWeights
            {
                [JsonProperty("Mild Spawn Weight")]
                public float MildWeight { get; set; } = 80f;
                [JsonProperty("Medium Spawn Weight")]
                public float MediumWeight { get; set; } = 40f;
                [JsonProperty("Extreme Spawn Weight")]
                public float ExtremeWeight { get; set; } = 10f;
            }

            public class IntensityOptions
            {
                [JsonProperty(Order = 0)]
                public float ItemDropMultiplier { get; set; } = 1.0f;
                [JsonProperty(Order = 1)]
                public Settings Mild { get; set; } = new Settings
                {
                    DamageMultiplier = 0.25f, FireRocketChance = 30, Radius = 500f,
                    DurationSecondsMin = 180f, DurationSecondsMax = 300f, RocketAmount = 20,
                    ItemDropControl = new Drops
                    {
                        EnableItemDrop = true,
                        ItemsToDrop = new ItemDrop[]
                        {
                            new ItemDrop { Maximum = 500, Minimum = 250, Shortname = "stones" },
                            new ItemDrop { Maximum = 500, Minimum = 250, Shortname = "metal.ore" },
                            new ItemDrop { Maximum = 500, Minimum = 250, Shortname = "sulfur.ore" },
                            new ItemDrop { Maximum = 20, Minimum = 10, Shortname = "scrap" }
                        }
                    }
                };
                [JsonProperty(Order = 2)]
                public Settings Medium { get; set; } = new Settings
                {
                    DamageMultiplier = 0.5f, FireRocketChance = 20, Radius = 300f,
                    DurationSecondsMin = 240f, DurationSecondsMax = 480f, RocketAmount = 45,
                    ItemDropControl = new Drops
                    {
                        EnableItemDrop = true,
                        ItemsToDrop = new ItemDrop[]
                        {
                            new ItemDrop { Maximum = 800, Minimum = 500, Shortname = "stones" },
                            new ItemDrop { Maximum = 800, Minimum = 500, Shortname = "metal.fragments" },
                            new ItemDrop { Maximum = 30, Minimum = 15, Shortname = "hq.metal.ore" },
                            new ItemDrop { Maximum = 800, Minimum = 500, Shortname = "sulfur.ore" },
                            new ItemDrop { Maximum = 50, Minimum = 20, Shortname = "scrap" }
                        }
                    }
                };
                [JsonProperty(Order = 3)]
                public Settings Extreme { get; set; } = new Settings
                {
                    DamageMultiplier = 1.0f, FireRocketChance = 10, Radius = 100f,
                    DurationSecondsMin = 300f, DurationSecondsMax = 600f, RocketAmount = 70,
                    ItemDropControl = new Drops
                    {
                        EnableItemDrop = true,
                        ItemsToDrop = new ItemDrop[]
                        {
                            new ItemDrop { Maximum = 1000, Minimum = 500, Shortname = "stones" },
                            new ItemDrop { Maximum = 1000, Minimum = 500, Shortname = "metal.fragments" },
                            new ItemDrop { Maximum = 100, Minimum = 50, Shortname = "hq.metal.ore" },
                            new ItemDrop { Maximum = 1000, Minimum = 500, Shortname = "sulfur.ore" },
                            new ItemDrop { Maximum = 100, Minimum = 50, Shortname = "scrap" }
                        }
                    }
                };
                [JsonProperty(Order = 4)]
                public SpawnWeights SpawnWeights { get; set; } = new SpawnWeights();
            }
        }

        private void LoadVariables()
        {
            base.Config.Settings.ObjectCreationHandling = ObjectCreationHandling.Replace;
            configData = Config.ReadObject<ConfigData>() ?? new ConfigData();
            ValidateIntensitySettings();
            SaveConfig(configData);
        }

        private void ValidateIntensitySettings()
        {
            var def = new ConfigData().IntensitySettings;
            ValidateDuration(configData.IntensitySettings.Mild,    def.Mild);
            ValidateDuration(configData.IntensitySettings.Medium,  def.Medium);
            ValidateDuration(configData.IntensitySettings.Extreme, def.Extreme);

            var w  = configData.IntensitySettings.SpawnWeights;
            var dw = def.SpawnWeights;
            if (w.MildWeight    <= 0f) w.MildWeight    = dw.MildWeight;
            if (w.MediumWeight  <= 0f) w.MediumWeight  = dw.MediumWeight;
            if (w.ExtremeWeight <= 0f) w.ExtremeWeight = dw.ExtremeWeight;
        }

        private void ValidateDuration(ConfigData.Settings s, ConfigData.Settings def)
        {
            if (s.DurationSecondsMin <= 0f)
                s.DurationSecondsMin = def.DurationSecondsMin;
            if (s.DurationSecondsMax <= 0f)
                s.DurationSecondsMax = def.DurationSecondsMax;
            if (s.DurationSecondsMin >= s.DurationSecondsMax)
            {
                s.DurationSecondsMin = def.DurationSecondsMin;
                s.DurationSecondsMax = def.DurationSecondsMax;
            }
        }

        protected override void LoadDefaultConfig() => SaveConfig(new ConfigData());
        void SaveConfig(ConfigData config) => Config.WriteObject(config, true);
        #endregion

        #region Localization
        string msg(string key, string playerId = "") => lang.GetMessage(key, this, playerId);
        Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            {"warningIncoming", "[Celestial Barrage Event] In <color=#00BFFF>{0}</color> seconds!" },
            {"incoming", "Celestial Barrage Incoming" },
            {"help1", "/cb onplayer <opt:playername> - Calls a random event on your position, or the player specified"},
            {"help2", "/cb onplayer_extreme <opt:playername> - Starts a extreme event on your position, or the player specified"},
            {"help3", "/cb onplayer_medium <opt:playername> - Starts a medium event on your position, or the player specified"},
            {"help4", "/cb onplayer_mild <opt:playername> - Starts a mild event on your position, or the player specified"},
            {"help5", "/cb barrage - Fire a barrage of rockets from your position"},
            {"help6", "/cb random - Calls a event at a random postion"},
            {"help7", "Console: cb.random - Calls a random event"},
            {"help8", "Console: cb.onposition x z - Calls an event at coordinates"},
            {"calledOn", "Event called on {0}'s position"},
            {"noPlayer", "No player found with that name"},
            {"onPos", "Event called on your position"},
            {"Extreme", "Extreme"},
            {"Medium", "Medium"},
            {"Mild", "Mild" },
            {"randomCall", "Event called on random position"},
            {"invalidParam", "Invalid parameter '{0}'"},
            {"unknown", "Unknown parameter '{0}'"}
        };
        #endregion

    }
}
