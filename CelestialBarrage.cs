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
    [Info("Celestial Barrage", "Ftuoil Xelrash", "0.0.856")]
    [Description("Create a Celestial Barrage falling from the sky")]
    class CelestialBarrage : RustPlugin
    {
        #region Fields

        private Timer EventTimer = null;
        private List<Timer> RocketTimers = new List<Timer>();
        private HashSet<BaseEntity> meteorRockets = new HashSet<BaseEntity>(); // Track our meteor rockets
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
            // Debug logging to help identify the issue
            if (configData?.Logging?.LogToConsole == true)
            {
                // Log all damage events during meteor showers for debugging
                if (meteorRockets.Count > 0)
                {
                    string weaponInfo = info?.WeaponPrefab?.ShortPrefabName ?? "Unknown";
                    string attackerInfo = info?.Initiator?.ShortPrefabName ?? "Unknown";
                    Puts($"[DEBUG] Damage event - Weapon: {weaponInfo}, Attacker: {attackerInfo}, Damage: {info?.damageTypes?.Total():F1}, Entity: {entity?.ShortPrefabName}");
                }
            }
            
            // Check if there are any active meteor rockets nearby first (performance optimization)
            bool nearMeteorRocket = false;
            BaseEntity closestRocket = null;
            float closestDistance = float.MaxValue;
            
            foreach (var rocket in meteorRockets)
            {
                if (rocket != null && !rocket.IsDestroyed)
                {
                    float distance = Vector3.Distance(rocket.transform.position, entity.transform.position);
                    if (distance < 50f) // Within reasonable blast radius
                    {
                        nearMeteorRocket = true;
                        if (distance < closestDistance)
                        {
                            closestDistance = distance;
                            closestRocket = rocket;
                        }
                    }
                }
            }
            
            // Only process if near a meteor rocket
            if (nearMeteorRocket)
            {
                if (configData?.Logging?.LogToConsole == true)
                {
                    Puts($"[DEBUG] Meteor impact detected - Distance: {closestDistance:F1}m, Rocket: {closestRocket?.ShortPrefabName}");
                }
                LogMeteorImpact(entity, info);
            }
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            // Clean up tracking when our rockets explode/die
            if (entity != null && meteorRockets.Contains(entity))
            {
                if (configData?.Logging?.LogToConsole == true)
                {
                    Puts($"[DEBUG] Removing tracked meteor projectile: {entity.ShortPrefabName} (Remaining: {meteorRockets.Count - 1})");
                }
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
                    if (configData.Logging.LogToConsole)
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
            if (queueDelay > 5f && configData.Logging.LogToConsole)
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
                    if (configData.Options.EventTimers.UseRandomTimer)
                    {
                        var random = RandomRange(configData.Options.EventTimers.RandomIntervalMinutesMin, configData.Options.EventTimers.RandomIntervalMinutesMax);
                        EventTimer = timer.Once(random * 60, () => { StartRandomOnMap(); StartEventTimer(); });
                    }
                    else EventTimer = timer.Repeat(configData.Options.EventTimers.EventIntervalMinutes * 60, 0, () => StartRandomOnMap());
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
                if (configData.Logging.LogToConsole)
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
                    if (configData.Logging.LogToConsole)
                    {
                        Puts($"CELESTIAL BARRAGE CANCELLED - Low FPS detected ({currentFPS:F1} < {configData.Options.PerformanceMonitoring.MinimumFPS}) - {eventType}");
                    }
                    return false;
                }
            }

            return true;
        }

        private void StartRandomOnMap()
        {
            // Use centralized performance and player count check
            if (!CheckPerformanceAndPlayerCount("Automatic Event"))
                return;

            float mapsize = (TerrainMeta.Size.x / 2) - 600f;

            float randomX = UnityEngine.Random.Range(-mapsize, mapsize);
            float randomY = UnityEngine.Random.Range(-mapsize, mapsize);

            Vector3 callAt = new Vector3(randomX, 0f, randomY);

            // Use the random intensity selection wrapper
            var selectedSetting = GetRandomIntensitySetting();
            StartRainOfFire(callAt, selectedSetting.setting, "Automatic Event");
        }

        private (ConfigData.Settings setting, string intensity) GetRandomIntensitySetting()
        {
            // Randomly select intensity for events
            // 50% Mild, 30% Medium, 20% Extreme
            int randomIntensity = UnityEngine.Random.Range(1, 101);

            if (randomIntensity <= 50)
                return (configData.IntensitySettings.Mild, "Mild");
            else if (randomIntensity <= 80)
                return (configData.IntensitySettings.Medium, "Medium");
            else
                return (configData.IntensitySettings.Extreme, "Extreme");
        }

        private bool StartOnPlayer(string playerName, ConfigData.Settings setting, string eventType)
        {
            // Check performance and player count before starting
            if (!CheckPerformanceAndPlayerCount(eventType))
                return false;

            BasePlayer player = GetPlayerByName(playerName);

            if (player == null)
                return false;

            StartRainOfFire(player.transform.position, setting, eventType);
            return true;
        }

        private bool StartRandomOnPlayer(string playerName, string eventType)
        {
            // Check performance and player count before starting
            if (!CheckPerformanceAndPlayerCount(eventType))
                return false;

            BasePlayer player = GetPlayerByName(playerName);

            if (player == null)
                return false;

            var randomSetting = GetRandomIntensitySetting();
            StartRainOfFire(player.transform.position, randomSetting.setting, eventType);
            return true;
        }

        private void StartRandomOnPosition(Vector3 position, string eventType)
        {
            // Check performance and player count before starting
            if (!CheckPerformanceAndPlayerCount(eventType))
                return;

            var randomSetting = GetRandomIntensitySetting();
            StartRainOfFire(position, randomSetting.setting, eventType);
        }

        private void StartBarrage(Vector3 origin, Vector3 direction) => timer.Repeat(configData.BarrageSettings.RocketDelay, configData.BarrageSettings.NumberOfRockets, () => SpreadRocket(origin, direction));

        private void StartRainOfFire(Vector3 origin, ConfigData.Settings setting, string eventType = "Manual")
        {
            // ALWAYS check FPS before starting ANY event
            if (configData?.Options?.PerformanceMonitoring?.EnableFPSCheck == true)
            {
                float currentFPS = 1f / UnityEngine.Time.unscaledDeltaTime;
                if (currentFPS < configData.Options.PerformanceMonitoring.MinimumFPS)
                {
                    LogMessage($"CELESTIAL BARRAGE BLOCKED\nLow FPS detected ({currentFPS:F1} < {configData.Options.PerformanceMonitoring.MinimumFPS})\n{eventType}", "admin");
                    return; // Block the event completely
                }
            }

            float radius = setting.Radius;
            int numberOfRockets = setting.RocketAmount;
            float duration = setting.Duration;
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
                                $"Stats: {numberOfRockets} rockets, {duration}s duration, {radius}m radius\n" +
                                $"Teleport: {teleportCmd}\n" +
                                $"==========================================";

            // Send enhanced Discord embeds to admin
            SendEnhancedDiscordMessage(true, eventType, intensity, gridRef, numberOfRockets, duration, radius, origin, teleportCmd);

            // Send public Discord message if enabled
            if (configData.Logging.PublicChannel.Enabled && configData.Logging.PublicChannel.IncludeEventStartEnd && IsValidWebhookUrl(configData.Logging.PublicChannel.PublicWebhookURL))
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
                if (configData.Logging.PublicChannel.Enabled && configData.Logging.PublicChannel.IncludeEventStartEnd && IsValidWebhookUrl(configData.Logging.PublicChannel.PublicWebhookURL))
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
                        new { name = "", value = $"⏱️ **Duration:** `{duration}s`", inline = false },
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
                    footer = new
                    {
                        text = "Celestial Barrage v0.0.516 • Live Event Monitoring",
                        icon_url = "https://cdn.discordapp.com/emojis/1234567890123456789.png"
                    }
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
                    footer = new
                    {
                        text = "Celestial Barrage v0.0.570 • Event Complete",
                        icon_url = "https://cdn.discordapp.com/emojis/1234567890123456789.png"
                    }
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
                    if (configData.Logging.LogToConsole)
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

                BaseEntity rocket = CreateRocket(launchPos, direction, isFireRocket);

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
            BaseEntity rocket = CreateRocket(origin, direction, false);
            
            // Check if rocket creation failed
            if (rocket == null)
            {
                PrintWarning("Failed to create barrage rocket - skipping this rocket spawn");
                return;
            }
        }

        private BaseEntity CreateRocket(Vector3 startPoint, Vector3 direction, bool isFireRocket)
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
            if (configData?.Logging?.LogToConsole == true)
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
            ScaleAllDamage(timedExplosive.damageTypes, configData.IntensitySettings.DamageMultiplier);

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
            if (configData?.Logging?.LogToConsole == true)
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
            
            // Apply randomization to ALL projectile types - each rocket gets unique timing
            float frequency = Mathf.Max(0f, baseFrequency + UnityEngine.Random.Range(-0.3f, 0.3f));
            
            timer.Repeat(frequency, iterations, () => {
                if (rocket != null && !rocket.IsDestroyed)
                {
                    Effect.server.Run(effectPath, rocket.transform.position, Vector3.up, null, false);
                }
            });
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
            
            // Check if damage is too low to log (configurable threshold)
            // ALWAYS allow player impacts (any damage) and grenade impacts (special mechanics)
            float totalDamage = info?.damageTypes?.Total() ?? 0f;
            bool isCatapultImpact = damageSource.Contains("boulder") || damageSource.Contains("catapult");
            bool isGrenadeImpact = damageSource.Contains("40mm") || damageSource.Contains("grenade");
            bool isSmokeRocket = damageSource.Contains("smoke");

            // Filter smoke rockets from structures/entities if configured (always 1.0 damage spam) but keep for players
            if (isSmokeRocket && !isPlayer && configData.Logging.AdminChannel.ImpactFiltering.FilterSmokeRockets)
            {
                if (configData?.Logging?.LogToConsole == true)
                {
                    string impactType = isPlayerStructure ? "Structure" : "Entity";
                    Puts($"[DEBUG] Smoke rocket impact filtered on {impactType} - {damageSource} - Damage: {totalDamage:F1} (FilterSmokeRockets enabled)");
                }
                return;
            }

            if (totalDamage < configData.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold && !isPlayer)
            {
                // Damage too low and not a player impact - silently filter this impact (applies to all projectile types)
                return;
            }
            
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

            // Log and fire hook for players or player structures only
            if (isPlayer || isPlayerStructure)
            {
                string damageInfo = $"Damage: {totalDamage:F1}";
                Vector3 pos = entity.transform.position;
                string teleportCmd = $"teleportpos {pos.x:F1} {pos.y:F1} {pos.z:F1}";
                
                // Enhanced debug logging
                if (configData?.Logging?.LogToConsole == true)
                {
                    string projectileType = "Unknown";
                    if (damageSource.Contains("rocket")) projectileType = "Rocket";
                    else if (isCatapultImpact) projectileType = "Catapult";
                    else if (isGrenadeImpact) projectileType = "Grenade";

                    Puts($"[DEBUG] LOGGING IMPACT - Type: {projectileType}, Weapon: {damageSource}, Damage: {totalDamage:F1}");
                }

                // Console logging for admin monitoring
                if (configData.Logging.LogToConsole)
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
            if (configData.Logging.LogToConsole)
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
                footer = new
                {
                    text = "Celestial Barrage v0.0.570",
                    icon_url = "https://i.imgur.com/meteor.png"
                }
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
                    if (configData.Logging.LogToConsole)
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
                fields = fieldsList.ToArray()
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
                    if (configData.Logging.LogToConsole)
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
                }
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
                    if (configData.Logging.LogToConsole)
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
                footer = new
                {
                    text = "Celestial Barrage v0.0.570",
                    icon_url = "https://i.imgur.com/meteor.png"
                }
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
                    if (configData.Logging.LogToConsole)
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
                footer = new
                {
                    text = "Celestial Barrage v0.0.510",
                    icon_url = "https://i.imgur.com/meteor.png"
                }
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
                    if (configData.Logging.LogToConsole)
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
                

            switch (args[0].ToLower())
            {
                case "onplayer":
                    if (args.Length == 2)
                    {
                        if (StartRandomOnPlayer(args[1], "Admin on Player"))
                            SendReply(player, string.Format(msg("calledOn", player.UserIDString), args[1]));
                        else
                            SendReply(player, msg("noPlayer", player.UserIDString));
                    }
                    else
                    {
                        StartRandomOnPosition(player.transform.position, "Admin on Position");
                        SendReply(player, msg("onPos", player.UserIDString));
                    }
                    break;

                case "onplayer_extreme":
                    if (args.Length == 2)
                    {
                        if (StartOnPlayer(args[1], configData.IntensitySettings.Extreme, "Admin on Player"))
                            SendReply(player, msg("Extreme", player.UserIDString) + string.Format(msg("calledOn", player.UserIDString), args[1]));
                        else
                            SendReply(player, msg("noPlayer", player.UserIDString));
                    }
                    else
                    {
                        StartRainOfFire(player.transform.position, configData.IntensitySettings.Extreme, "Admin on Position");
                        SendReply(player, msg("Extreme", player.UserIDString) + msg("onPos", player.UserIDString));
                    }
                    break;

                case "onplayer_medium":
                    if (args.Length == 2)
                    {
                        if (StartOnPlayer(args[1], configData.IntensitySettings.Medium, "Admin on Player"))
                            SendReply(player, msg("Medium", player.UserIDString) + string.Format(msg("calledOn", player.UserIDString), args[1]));
                        else
                            SendReply(player, msg("noPlayer", player.UserIDString));
                    }
                    else
                    {
                        StartRainOfFire(player.transform.position, configData.IntensitySettings.Medium, "Admin on Position");
                        SendReply(player, msg("Medium", player.UserIDString) + msg("onPos", player.UserIDString));
                    }
                    break;

                case "onplayer_mild":
                    if (args.Length == 2)
                    {
                        if (StartOnPlayer(args[1], configData.IntensitySettings.Mild, "Admin on Player"))
                            SendReply(player, msg("Mild", player.UserIDString) + string.Format(msg("calledOn", player.UserIDString), args[1]));
                        else
                            SendReply(player, msg("noPlayer", player.UserIDString));
                    }
                    else
                    {
                        StartRainOfFire(player.transform.position, configData.IntensitySettings.Mild, "Admin on Position");
                        SendReply(player, msg("Mild", player.UserIDString) + msg("onPos", player.UserIDString));
                    }
                    break;

                case "barrage":
                    StartBarrage(player.eyes.position + player.eyes.HeadForward() * 1f, player.eyes.HeadForward());
                    break;

                case "random":
                    // Check performance before starting manual random command
                    if (!CheckPerformanceAndPlayerCount("Manual Random Command"))
                    {
                        SendReply(player, "Random event cancelled due to performance or player count restrictions");
                        return;
                    }
                    StartRandomOnMap();
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
                
                // Check performance before starting console command
                if (!CheckPerformanceAndPlayerCount("Console Random Command"))
                {
                    Puts("Random event cancelled due to performance or player count restrictions");
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
                    // Check performance before starting console command
                    if (!CheckPerformanceAndPlayerCount("Console Position Command"))
                    {
                        Puts("Position event cancelled due to performance or player count restrictions");
                        return;
                    }
                    
                    var position = new Vector3(x, 0, z);
                    StartRandomOnPosition(GetGroundPosition(position), "Console Command");
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
            BasePlayer foundPlayer = null;
            name = name.ToLower();
            int bestMatchLength = int.MaxValue;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                string currentName = player.displayName.ToLower();
                
                if (currentName.Contains(name))
                {
                    int remainingLength = currentName.Replace(name, "").Length;
                    
                    if (remainingLength < bestMatchLength)
                    {
                        bestMatchLength = remainingLength;
                        foundPlayer = player;
                    }
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
            return MapHelper.GridToString(MapHelper.PositionToGrid(position));
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
            public BarrageOptions BarrageSettings { get; set; }
            public ConfigOptions Options { get; set; }
            public LoggingOptions Logging { get; set; }
            public IntensityOptions IntensitySettings { get; set; }

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
                public bool InGamePlayerEventNotifications { get; set; }
                public int MinimumPlayerCount { get; set; }
                public PerformanceSettings PerformanceMonitoring { get; set; }
                public EffectsSettings VisualEffects { get; set; }
            }

            public class LoggingOptions
            {
                public bool LogToConsole { get; set; }
                public PublicChannelOptions PublicChannel { get; set; }
                public AdminChannelOptions AdminChannel { get; set; }
                public DiscordRateLimitOptions DiscordRateLimit { get; set; }
            }

            public class PublicChannelOptions
            {
                public bool Enabled { get; set; }
                public bool IncludeEventStartEnd { get; set; }
                [JsonProperty(PropertyName = "Webhook URL")]
                public string PublicWebhookURL { get; set; }
            }

            public class AdminChannelOptions
            {
                [JsonProperty(PropertyName = "Enabled?")]
                public bool Enabled { get; set; }
                [JsonProperty(PropertyName = "Include Event Messages?")]
                public bool IncludeEventMessages { get; set; }
                [JsonProperty(PropertyName = "Include Impact Messages?")]
                public bool IncludeImpactMessages { get; set; }
                [JsonProperty(PropertyName = "Webhook URL")]
                public string PrivateAdminWebhookURL { get; set; }
                [JsonProperty(PropertyName = "Impact Filtering")]
                public ImpactFilteringOptions ImpactFiltering { get; set; }
            }

            public class ImpactFilteringOptions
            {
                [JsonProperty(PropertyName = "Log Player Impacts?")]
                public bool LogPlayerImpacts { get; set; }
                [JsonProperty(PropertyName = "Log Structure Impacts?")]
                public bool LogStructureImpacts { get; set; }
                [JsonProperty(PropertyName = "Filter Smoke Rockets?")]
                public bool FilterSmokeRockets { get; set; }
                [JsonProperty(PropertyName = "Minimum Impact Damage Threshold")]
                public float MinimumDamageThreshold { get; set; }
            }

            public class DiscordRateLimitOptions
            {
                public bool EnableRateLimit { get; set; }
                public float ImpactMessageCooldown { get; set; }
                // Discord webhook per-channel limit is 30 messages/minute. Set this lower to avoid rate limiting.
                // Default 20 provides safety margin (10 message buffer) for other webhooks/messages to the same channel.
                public int MaxImpactsPerMinute { get; set; }
            }

            public class PerformanceSettings
            {
                public bool EnableFPSCheck { get; set; }
                public float MinimumFPS { get; set; }
            }

            public class EffectsSettings
            {
                public bool EnableScreenShake { get; set; }
                public bool EnableParticleTrails { get; set; }
                public bool ShowEventMapMarkers { get; set; }
            }

            public class Timers
            {
                public int EventIntervalMinutes { get; set; }
                public bool UseRandomTimer { get; set; }
                public int RandomIntervalMinutesMin { get; set; }
                public int RandomIntervalMinutesMax { get; set; }
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
                [JsonProperty(Order = 0)]
                public float DamageMultiplier { get; set; }
                [JsonProperty(Order = 1)]
                public float ItemDropMultiplier { get; set; }
                [JsonProperty(Order = 2)]
                public Settings Mild { get; set; }
                [JsonProperty(Order = 3)]
                public Settings Medium { get; set; }
                [JsonProperty(Order = 4)]
                public Settings Extreme { get; set; }
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

            // Create default config for reference
            var defaultConfig = new ConfigData
            {
                BarrageSettings = new ConfigData.BarrageOptions
                {
                    NumberOfRockets = 20,
                    RocketDelay = 0.33f,
                    RocketSpread = 16f
                },
                Options = new ConfigData.ConfigOptions
                {
                    EnableAutomaticEvents = true,
                    EventTimers = new ConfigData.Timers
                    {
                        EventIntervalMinutes = 120,
                        RandomIntervalMinutesMax = 240,
                        RandomIntervalMinutesMin = 120,
                        UseRandomTimer = false
                    },
                    InGamePlayerEventNotifications = true,
                    MinimumPlayerCount = 1,
                    PerformanceMonitoring = new ConfigData.PerformanceSettings
                    {
                        EnableFPSCheck = true,
                        MinimumFPS = 40f
                    },
                    VisualEffects = new ConfigData.EffectsSettings
                    {
                        EnableScreenShake = true,
                        EnableParticleTrails = true,
                        ShowEventMapMarkers = true
                    }
                },
                Logging = new ConfigData.LoggingOptions
                {
                    LogToConsole = true,
                    PublicChannel = new ConfigData.PublicChannelOptions
                    {
                        Enabled = false,
                        IncludeEventStartEnd = true,
                        PublicWebhookURL = "https://discord.com/api/webhooks/YOUR_WEBHOOK_ID/YOUR_WEBHOOK_TOKEN"
                    },
                    AdminChannel = new ConfigData.AdminChannelOptions
                    {
                        Enabled = true,
                        IncludeEventMessages = true,
                        IncludeImpactMessages = true,
                        PrivateAdminWebhookURL = "https://discord.com/api/webhooks/YOUR_ADMIN_WEBHOOK_ID/YOUR_ADMIN_WEBHOOK_TOKEN",
                        ImpactFiltering = new ConfigData.ImpactFilteringOptions
                        {
                            LogPlayerImpacts = true,
                            LogStructureImpacts = false,
                            FilterSmokeRockets = true,
                            MinimumDamageThreshold = 50.0f
                        }
                    },
                    DiscordRateLimit = new ConfigData.DiscordRateLimitOptions
                    {
                        EnableRateLimit = true,
                        ImpactMessageCooldown = 1.0f,
                        MaxImpactsPerMinute = 20
                    }
                },
                IntensitySettings = new ConfigData.IntensityOptions
                {
                    DamageMultiplier = 1.0f,
                    ItemDropMultiplier = 1.0f,
                    Mild = new ConfigData.Settings
                    {
                        FireRocketChance = 30,
                        Radius = 500f,
                        Duration = 240,
                        RocketAmount = 20,
                        ItemDropControl = new ConfigData.Drops
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
                    },
                    Medium = new ConfigData.Settings
                    {
                        FireRocketChance = 20,
                        Radius = 300f,
                        Duration = 120,
                        RocketAmount = 45,
                        ItemDropControl = new ConfigData.Drops
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
                    },
                    Extreme = new ConfigData.Settings
                    {
                        FireRocketChance = 10,
                        Radius = 100f,
                        Duration = 30,
                        RocketAmount = 70,
                        ItemDropControl = new ConfigData.Drops
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
                    }
                }
            };

            // Validate BarrageSettings
            if (configData.BarrageSettings == null)
            {
                Puts("Adding missing BarrageSettings section");
                configData.BarrageSettings = defaultConfig.BarrageSettings;
                configChanged = true;
            }
            else
            {
                if (configData.BarrageSettings.NumberOfRockets == 0)
                {
                    Puts("Adding missing BarrageSettings.NumberOfRockets");
                    configData.BarrageSettings.NumberOfRockets = defaultConfig.BarrageSettings.NumberOfRockets;
                    configChanged = true;
                }
                if (configData.BarrageSettings.RocketDelay == 0)
                {
                    Puts("Adding missing BarrageSettings.RocketDelay");
                    configData.BarrageSettings.RocketDelay = defaultConfig.BarrageSettings.RocketDelay;
                    configChanged = true;
                }
                if (configData.BarrageSettings.RocketSpread == 0)
                {
                    Puts("Adding missing BarrageSettings.RocketSpread");
                    configData.BarrageSettings.RocketSpread = defaultConfig.BarrageSettings.RocketSpread;
                    configChanged = true;
                }
            }

            // Validate IntensitySettings.DamageMultiplier
            if (configData.IntensitySettings.DamageMultiplier == 0)
            {
                Puts("Adding missing IntensitySettings.DamageMultiplier");
                configData.IntensitySettings.DamageMultiplier = defaultConfig.IntensitySettings.DamageMultiplier;
                configChanged = true;
            }

            // Validate Options
            if (configData.Options == null)
            {
                Puts("Adding missing Options section");
                configData.Options = defaultConfig.Options;
                configChanged = true;
            }
            else
            {
                if (configData.IntensitySettings.ItemDropMultiplier == 0)
                {
                    Puts("Adding missing IntensitySettings.ItemDropMultiplier");
                    configData.IntensitySettings.ItemDropMultiplier = defaultConfig.IntensitySettings.ItemDropMultiplier;
                    configChanged = true;
                }
                if (configData.Options.MinimumPlayerCount == 0)
                {
                    Puts("Adding missing Options.MinimumPlayerCount");
                    configData.Options.MinimumPlayerCount = defaultConfig.Options.MinimumPlayerCount;
                    configChanged = true;
                }

                if (configData.Options.EventTimers == null)
                {
                    Puts("Adding missing Options.EventTimers section");
                    configData.Options.EventTimers = defaultConfig.Options.EventTimers;
                    configChanged = true;
                }
                else
                {
                    if (configData.Options.EventTimers.EventIntervalMinutes == 0)
                    {
                        Puts("Adding missing EventTimers.EventIntervalMinutes");
                        configData.Options.EventTimers.EventIntervalMinutes = defaultConfig.Options.EventTimers.EventIntervalMinutes;
                        configChanged = true;
                    }
                    if (configData.Options.EventTimers.RandomIntervalMinutesMin == 0)
                    {
                        Puts("Adding missing EventTimers.RandomIntervalMinutesMin");
                        configData.Options.EventTimers.RandomIntervalMinutesMin = defaultConfig.Options.EventTimers.RandomIntervalMinutesMin;
                        configChanged = true;
                    }
                    if (configData.Options.EventTimers.RandomIntervalMinutesMax == 0)
                    {
                        Puts("Adding missing EventTimers.RandomIntervalMinutesMax");
                        configData.Options.EventTimers.RandomIntervalMinutesMax = defaultConfig.Options.EventTimers.RandomIntervalMinutesMax;
                        configChanged = true;
                    }
                }

                if (configData.Options.PerformanceMonitoring == null)
                {
                    Puts("Adding missing Options.PerformanceMonitoring section");
                    configData.Options.PerformanceMonitoring = defaultConfig.Options.PerformanceMonitoring;
                    configChanged = true;
                }
                else
                {
                    if (configData.Options.PerformanceMonitoring.MinimumFPS == 0)
                    {
                        Puts("Adding missing PerformanceMonitoring.MinimumFPS");
                        configData.Options.PerformanceMonitoring.MinimumFPS = defaultConfig.Options.PerformanceMonitoring.MinimumFPS;
                        configChanged = true;
                    }
                }

                if (configData.Options.VisualEffects == null)
                {
                    Puts("Adding missing Options.VisualEffects section");
                    configData.Options.VisualEffects = defaultConfig.Options.VisualEffects;
                    configChanged = true;
                }
            }

            // Validate Logging
            if (configData.Logging == null)
            {
                Puts("Adding missing Logging section");
                configData.Logging = defaultConfig.Logging;
                configChanged = true;
            }
            else
            {
                if (string.IsNullOrEmpty(configData.Logging.PublicChannel?.PublicWebhookURL))
                {
                    Puts("Adding missing Logging.PublicChannel.PublicWebhookURL");
                    configData.Logging.PublicChannel.PublicWebhookURL = defaultConfig.Logging.PublicChannel.PublicWebhookURL;
                    configChanged = true;
                }
                if (string.IsNullOrEmpty(configData.Logging.AdminChannel?.PrivateAdminWebhookURL))
                {
                    Puts("Adding missing Logging.AdminChannel.PrivateAdminWebhookURL");
                    configData.Logging.AdminChannel.PrivateAdminWebhookURL = defaultConfig.Logging.AdminChannel.PrivateAdminWebhookURL;
                    configChanged = true;
                }

                if (configData.Logging.DiscordRateLimit == null)
                {
                    Puts("Adding missing Logging.DiscordRateLimit section");
                    configData.Logging.DiscordRateLimit = defaultConfig.Logging.DiscordRateLimit;
                    configChanged = true;
                }
                else
                {
                    if (configData.Logging.DiscordRateLimit.ImpactMessageCooldown == 0)
                    {
                        Puts("Adding missing DiscordRateLimit.ImpactMessageCooldown");
                        configData.Logging.DiscordRateLimit.ImpactMessageCooldown = defaultConfig.Logging.DiscordRateLimit.ImpactMessageCooldown;
                        configChanged = true;
                    }
                    if (configData.Logging.DiscordRateLimit.MaxImpactsPerMinute == 0)
                    {
                        Puts("Adding missing DiscordRateLimit.MaxImpactsPerMinute");
                        configData.Logging.DiscordRateLimit.MaxImpactsPerMinute = defaultConfig.Logging.DiscordRateLimit.MaxImpactsPerMinute;
                        configChanged = true;
                    }
                }

                if (configData.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold == 0)
                {
                    Puts("Adding missing AdminChannel.ImpactFiltering.MinimumDamageThreshold");
                    configData.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold = defaultConfig.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold;
                    configChanged = true;
                }

                // Validate MinimumDamageThreshold range and handle invalid values
                if (configData.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold < 0f ||
                    configData.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold > 1000f ||
                    float.IsNaN(configData.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold) ||
                    float.IsInfinity(configData.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold))
                {
                    float oldValue = configData.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold;
                    configData.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold = defaultConfig.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold;
                    Puts($"Invalid MinimumDamageThreshold value ({oldValue}), resetting to default ({configData.Logging.AdminChannel.ImpactFiltering.MinimumDamageThreshold})");
                    configChanged = true;
                }

                // Validate PublicChannel settings
                if (configData.Logging.PublicChannel == null)
                {
                    Puts("Adding missing Logging.PublicChannel section");
                    configData.Logging.PublicChannel = defaultConfig.Logging.PublicChannel;
                    configChanged = true;
                }

                // Validate AdminChannel settings
                if (configData.Logging.AdminChannel == null)
                {
                    Puts("Adding missing Logging.AdminChannel section");
                    configData.Logging.AdminChannel = defaultConfig.Logging.AdminChannel;
                    configChanged = true;
                }
                else
                {
                    // Validate ImpactFiltering within AdminChannel
                    if (configData.Logging.AdminChannel.ImpactFiltering == null)
                    {
                        Puts("Adding missing Logging.AdminChannel.ImpactFiltering section");
                        configData.Logging.AdminChannel.ImpactFiltering = defaultConfig.Logging.AdminChannel.ImpactFiltering;
                        configChanged = true;
                    }
                }
            }

            // Validate IntensitySettings
            if (configData.IntensitySettings == null)
            {
                Puts("Adding missing IntensitySettings section");
                configData.IntensitySettings = defaultConfig.IntensitySettings;
                configChanged = true;
            }
            else
            {
                if (configData.IntensitySettings.Mild == null)
                {
                    Puts("Adding missing IntensitySettings.Mild section");
                    configData.IntensitySettings.Mild = defaultConfig.IntensitySettings.Mild;
                    configChanged = true;
                }
                else
                {
                    if (configData.IntensitySettings.Mild.FireRocketChance == 0)
                    {
                        Puts("Adding missing Mild.FireRocketChance");
                        configData.IntensitySettings.Mild.FireRocketChance = defaultConfig.IntensitySettings.Mild.FireRocketChance;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Mild.Radius == 0)
                    {
                        Puts("Adding missing Mild.Radius");
                        configData.IntensitySettings.Mild.Radius = defaultConfig.IntensitySettings.Mild.Radius;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Mild.RocketAmount == 0)
                    {
                        Puts("Adding missing Mild.RocketAmount");
                        configData.IntensitySettings.Mild.RocketAmount = defaultConfig.IntensitySettings.Mild.RocketAmount;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Mild.Duration == 0)
                    {
                        Puts("Adding missing Mild.Duration");
                        configData.IntensitySettings.Mild.Duration = defaultConfig.IntensitySettings.Mild.Duration;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Mild.ItemDropControl == null)
                    {
                        Puts("Adding missing Mild.ItemDropControl section");
                        configData.IntensitySettings.Mild.ItemDropControl = defaultConfig.IntensitySettings.Mild.ItemDropControl;
                        configChanged = true;
                    }
                }

                if (configData.IntensitySettings.Medium == null)
                {
                    Puts("Adding missing IntensitySettings.Medium section");
                    configData.IntensitySettings.Medium = defaultConfig.IntensitySettings.Medium;
                    configChanged = true;
                }
                else
                {
                    if (configData.IntensitySettings.Medium.FireRocketChance == 0)
                    {
                        Puts("Adding missing Medium.FireRocketChance");
                        configData.IntensitySettings.Medium.FireRocketChance = defaultConfig.IntensitySettings.Medium.FireRocketChance;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Medium.Radius == 0)
                    {
                        Puts("Adding missing Medium.Radius");
                        configData.IntensitySettings.Medium.Radius = defaultConfig.IntensitySettings.Medium.Radius;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Medium.RocketAmount == 0)
                    {
                        Puts("Adding missing Medium.RocketAmount");
                        configData.IntensitySettings.Medium.RocketAmount = defaultConfig.IntensitySettings.Medium.RocketAmount;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Medium.Duration == 0)
                    {
                        Puts("Adding missing Medium.Duration");
                        configData.IntensitySettings.Medium.Duration = defaultConfig.IntensitySettings.Medium.Duration;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Medium.ItemDropControl == null)
                    {
                        Puts("Adding missing Medium.ItemDropControl section");
                        configData.IntensitySettings.Medium.ItemDropControl = defaultConfig.IntensitySettings.Medium.ItemDropControl;
                        configChanged = true;
                    }
                }

                if (configData.IntensitySettings.Extreme == null)
                {
                    Puts("Adding missing IntensitySettings.Extreme section");
                    configData.IntensitySettings.Extreme = defaultConfig.IntensitySettings.Extreme;
                    configChanged = true;
                }
                else
                {
                    if (configData.IntensitySettings.Extreme.FireRocketChance == 0)
                    {
                        Puts("Adding missing Extreme.FireRocketChance");
                        configData.IntensitySettings.Extreme.FireRocketChance = defaultConfig.IntensitySettings.Extreme.FireRocketChance;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Extreme.Radius == 0)
                    {
                        Puts("Adding missing Extreme.Radius");
                        configData.IntensitySettings.Extreme.Radius = defaultConfig.IntensitySettings.Extreme.Radius;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Extreme.RocketAmount == 0)
                    {
                        Puts("Adding missing Extreme.RocketAmount");
                        configData.IntensitySettings.Extreme.RocketAmount = defaultConfig.IntensitySettings.Extreme.RocketAmount;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Extreme.Duration == 0)
                    {
                        Puts("Adding missing Extreme.Duration");
                        configData.IntensitySettings.Extreme.Duration = defaultConfig.IntensitySettings.Extreme.Duration;
                        configChanged = true;
                    }
                    if (configData.IntensitySettings.Extreme.ItemDropControl == null)
                    {
                        Puts("Adding missing Extreme.ItemDropControl section");
                        configData.IntensitySettings.Extreme.ItemDropControl = defaultConfig.IntensitySettings.Extreme.ItemDropControl;
                        configChanged = true;
                    }
                }
            }

            // Rest of validation code remains the same...
            if (configData.IntensitySettings?.Mild?.ItemDropControl?.ItemsToDrop != null)
            {
                // Only update if the drops don't match our new structure
                var currentMild = configData.IntensitySettings.Mild.ItemDropControl.ItemsToDrop;
                bool needsUpdate = currentMild.Length != 4 || 
                                 !System.Array.Exists(currentMild, x => x.Shortname == "scrap") ||
                                 !System.Array.Exists(currentMild, x => x.Shortname == "sulfur.ore");
                
                if (needsUpdate)
                {
                    Puts("Updating Mild intensity drops to new values");
                    configData.IntensitySettings.Mild.ItemDropControl.ItemsToDrop = new ItemDrop[]
                    {
                        new ItemDrop { Maximum = 500, Minimum = 250, Shortname = "stones" },
                        new ItemDrop { Maximum = 500, Minimum = 250, Shortname = "metal.ore" },
                        new ItemDrop { Maximum = 500, Minimum = 250, Shortname = "sulfur.ore" },
                        new ItemDrop { Maximum = 20, Minimum = 10, Shortname = "scrap" }
                    };
                    configChanged = true;
                }
            }

            if (configData.IntensitySettings?.Medium?.ItemDropControl?.ItemsToDrop != null)
            {
                var currentMedium = configData.IntensitySettings.Medium.ItemDropControl.ItemsToDrop;
                bool needsUpdate = currentMedium.Length != 5 || 
                                 !System.Array.Exists(currentMedium, x => x.Shortname == "scrap") ||
                                 !System.Array.Exists(currentMedium, x => x.Shortname == "sulfur.ore");
                
                if (needsUpdate)
                {
                    Puts("Updating Medium intensity drops to new values");
                    configData.IntensitySettings.Medium.ItemDropControl.ItemsToDrop = new ItemDrop[]
                    {
                        new ItemDrop { Maximum = 800, Minimum = 500, Shortname = "stones" },
                        new ItemDrop { Maximum = 800, Minimum = 500, Shortname = "metal.fragments" },
                        new ItemDrop { Maximum = 30, Minimum = 15, Shortname = "hq.metal.ore" },
                        new ItemDrop { Maximum = 800, Minimum = 500, Shortname = "sulfur.ore" },
                        new ItemDrop { Maximum = 50, Minimum = 20, Shortname = "scrap" }
                    };
                    configChanged = true;
                }
            }

            if (configData.IntensitySettings?.Extreme?.ItemDropControl?.ItemsToDrop != null)
            {
                var currentExtreme = configData.IntensitySettings.Extreme.ItemDropControl.ItemsToDrop;
                bool needsUpdate = currentExtreme.Length != 5 || 
                                 System.Array.Exists(currentExtreme, x => x.Shortname == "metal.refined") ||
                                 !System.Array.Exists(currentExtreme, x => x.Shortname == "scrap");
                
                if (needsUpdate)
                {
                    Puts("Updating Extreme intensity drops to new values");
                    configData.IntensitySettings.Extreme.ItemDropControl.ItemsToDrop = new ItemDrop[]
                    {
                        new ItemDrop { Maximum = 1000, Minimum = 500, Shortname = "stones" },
                        new ItemDrop { Maximum = 1000, Minimum = 500, Shortname = "metal.fragments" },
                        new ItemDrop { Maximum = 100, Minimum = 50, Shortname = "hq.metal.ore" },
                        new ItemDrop { Maximum = 1000, Minimum = 500, Shortname = "sulfur.ore" },
                        new ItemDrop { Maximum = 100, Minimum = 50, Shortname = "scrap" }
                    };
                    configChanged = true;
                }
            }

            if (configChanged)
            {
                Puts("Config validation completed - updated drop tables to v0.0.570 values");
                SaveConfig(configData);
            }
            else
            {
                Puts("Config validation completed - no issues found");
            }
            Puts("======================================");
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
                Options = new ConfigData.ConfigOptions
                {
                    EnableAutomaticEvents = true,
                    EventTimers = new ConfigData.Timers
                    {
                        EventIntervalMinutes = 120,
                        RandomIntervalMinutesMax = 240,
                        RandomIntervalMinutesMin = 120,
                        UseRandomTimer = false
                    },
                    InGamePlayerEventNotifications = true,
                    MinimumPlayerCount = 1,
                    PerformanceMonitoring = new ConfigData.PerformanceSettings
                    {
                        EnableFPSCheck = true,
                        MinimumFPS = 40f
                    },
                    VisualEffects = new ConfigData.EffectsSettings
                    {
                        EnableScreenShake = true,
                        EnableParticleTrails = true,
                        ShowEventMapMarkers = true
                    }
                },
                Logging = new ConfigData.LoggingOptions
                {
                    LogToConsole = true,
                    PublicChannel = new ConfigData.PublicChannelOptions
                    {
                        Enabled = false,
                        IncludeEventStartEnd = true,
                        PublicWebhookURL = "https://discord.com/api/webhooks/YOUR_WEBHOOK_ID/YOUR_WEBHOOK_TOKEN"
                    },
                    AdminChannel = new ConfigData.AdminChannelOptions
                    {
                        Enabled = true,
                        IncludeEventMessages = true,
                        IncludeImpactMessages = true,
                        PrivateAdminWebhookURL = "https://discord.com/api/webhooks/YOUR_ADMIN_WEBHOOK_ID/YOUR_ADMIN_WEBHOOK_TOKEN",
                        ImpactFiltering = new ConfigData.ImpactFilteringOptions
                        {
                            LogPlayerImpacts = true,
                            LogStructureImpacts = false,
                            FilterSmokeRockets = true,
                            MinimumDamageThreshold = 50.0f
                        }
                    },
                    DiscordRateLimit = new ConfigData.DiscordRateLimitOptions
                    {
                        EnableRateLimit = true,
                        ImpactMessageCooldown = 1.0f,
                        MaxImpactsPerMinute = 20
                    }
                },
                IntensitySettings = new ConfigData.IntensityOptions
                {
                    DamageMultiplier = 1.0f,
                    ItemDropMultiplier = 1.0f,
                    Mild = new ConfigData.Settings
                    {
                        FireRocketChance = 30,
                        Radius = 500f,
                        Duration = 240,
                        RocketAmount = 20,
                        ItemDropControl = new ConfigData.Drops
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
                    },
                    Medium = new ConfigData.Settings
                    {
                        FireRocketChance = 20,
                        Radius = 300f,
                        Duration = 120,
                        RocketAmount = 45,
                        ItemDropControl = new ConfigData.Drops
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
                    },
                    Extreme = new ConfigData.Settings
                    {
                        FireRocketChance = 10,
                        Radius = 100f,
                        Duration = 30,
                        RocketAmount = 70,
                        ItemDropControl = new ConfigData.Drops
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
                    }
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

        #region MapHelper
        internal static class MapHelper
        {
            internal static Vector2Int PositionToGrid(Vector3 position)
            {
                float mapSize = TerrainMeta.Size.x;
                float gridSize = mapSize / 26f;
                int x = Mathf.FloorToInt((position.x + mapSize / 2f) / gridSize);
                int z = Mathf.FloorToInt((mapSize / 2f - position.z) / gridSize); // Fix Z coordinate calculation
                return new Vector2Int(x, z);
            }

            internal static string GridToString(Vector2Int grid)
            {
                if (grid.x < 0 || grid.x > 25 || grid.y < 0 || grid.y > 25) return "??";
                return $"{(char)('A' + grid.x)}{grid.y}";
            }
        }
        #endregion

    }
}