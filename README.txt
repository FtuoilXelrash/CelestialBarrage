Celestial Barrage

Game: Rust
Framework: Umod
Version: 0.0.856
License: MIT
Downloads: Available on GitHub

Transform your Rust server with spectacular meteor shower events that rain down rockets from the sky! Celestial Barrage brings dynamic, configurable celestial events with three intensity levels, automatic scheduling, and rewarding loot drops that will keep your players engaged and coming back for more.

KEY FEATURES

- Dynamic Meteor Events - Automatic scheduling with configurable random or fixed timers
- Three Intensity Levels - Mild (beginner), Medium (balanced), Extreme (hardcore)
- Advanced Rocket System - Fire rockets, realistic physics, smart spread patterns
- Rewarding Loot System - Tiered rewards with stones, metal ore, fragments, and more
- Real-time Configuration - Change settings without server restart
- Smart Map Integration - Respects boundaries, terrain detection, safe zones
- Performance Optimized - Minimal server impact with automatic cleanup
- Discord Integration - Works with Discord webhooks

QUICK INSTALLATION

1. Download the latest release from GitHub
2. Copy CelestialBarrage.cs to your oxide/plugins/ directory
3. Restart your server - config auto-generates at oxide/config/CelestialBarrage.json
4. Configure your settings and reload the plugin

Pro Tip: The plugin works out-of-the-box with default settings optimized for most servers!

REQUIREMENTS

- Rust Dedicated Server
- Umod (Oxide) framework

COMMANDS & USAGE

Admin Commands (Chat):

/cb                          Show help menu
/cb onplayer                 Start optimal event on your position
/cb onplayer <player>        Start optimal event on target player
/cb random                   Start random map event
/cb barrage                  Fire rocket barrage from your position

Intensity Variants:

/cb onplayer_mild            20 rockets, 240s duration, 500m radius (New players, learning)
/cb onplayer_medium          45 rockets, 120s duration, 300m radius (Balanced gameplay)
/cb onplayer_extreme         70 rockets, 30s duration, 100m radius (Hardcore, high risk/reward)

Console Commands:

cb.random                    Start random meteor event
cb.onposition <x> <z>        Start event at coordinates

CONFIGURATION

The plugin creates oxide/config/CelestialBarrage.json with comprehensive configuration options.
Below is a complete reference of every available setting:

GLOBAL EVENT OPTIONS:

Core Event Settings:
{
  "Options": {
    "EnableAutomaticEvents": true,
    "MinimumPlayerCount": 1,
    "InGamePlayerEventNotifications": true,
    "EventTimers": {
      "EventInterval": 30,
      "UseRandomTimer": false,
      "RandomTimerMin": 15,
      "RandomTimerMax": 45
    }
  }
}

Option Descriptions:
- EnableAutomaticEvents (bool): Enables/disables automatic meteor events. When false, only manual commands work
- MinimumPlayerCount (int): Minimum number of players required online for events to trigger (default: 1)
- InGamePlayerEventNotifications (bool): When enabled, sends colored chat notifications to all players when events start/end
- EventTimers:
  - EventInterval (int): Minutes between automatic events in fixed mode (default: 30 minutes)
  - UseRandomTimer (bool): When true, events trigger at random intervals instead of fixed
  - RandomTimerMin (int): Minimum minutes for random timer (default: 15 minutes)
  - RandomTimerMax (int): Maximum minutes for random timer (default: 45 minutes)

PERFORMANCE MONITORING:

Monitor server performance and prevent events during lag:

{
  "Options": {
    "PerformanceMonitoring": {
      "EnableFPSCheck": true,
      "MinimumFPS": 30.0
    }
  }
}

Settings:
- EnableFPSCheck (bool): When true, the plugin checks server FPS before starting events
- MinimumFPS (float): Minimum acceptable FPS to allow events. Events cancelled if below this (default: 30.0)

VISUAL EFFECTS:

Control visual effects during events:

{
  "Options": {
    "VisualEffects": {
      "EnableScreenShake": true,
      "EnableParticleTrails": true,
      "ShowEventMapMarkers": true
    }
  }
}

Settings:
- EnableScreenShake (bool): When true, rockets create screen shake effects for nearby players
- EnableParticleTrails (bool): When true, rockets display particle effect trails as they fall
- ShowEventMapMarkers (bool): When true, map markers appear at meteor event locations to help players locate the action

LOGGING OPTIONS:

Control console logging:

{
  "Logging": {
    "LogToConsole": true
  }
}

Settings:
- LogToConsole (bool): When true, logs detailed information to server console

PUBLIC DISCORD CHANNEL:

Configure public Discord notifications (visible to entire server):

{
  "Logging": {
    "PublicChannel": {
      "Enabled": false,
      "Include Event Start End": true,
      "Webhook URL": "https://discord.com/api/webhooks/YOUR_WEBHOOK_ID/YOUR_WEBHOOK_TOKEN"
    }
  }
}

Settings:
- Enabled (bool): Enable/disable public channel Discord notifications
- Include Event Start End (bool): When true, sends event start and end messages to public channel
- Webhook URL (string): Discord webhook URL for public notifications

ADMIN DISCORD CHANNEL:

Configure private Discord notifications (visible only to admins):

{
  "Logging": {
    "AdminChannel": {
      "Enabled?": true,
      "Include Event Messages?": true,
      "Include Impact Messages?": true,
      "Webhook URL": "https://discord.com/api/webhooks/YOUR_ADMIN_WEBHOOK_ID/YOUR_ADMIN_WEBHOOK_TOKEN",
      "Impact Filtering": {
        "Log Player Impacts?": true,
        "Log Structure Impacts?": false,
        "Filter Smoke Rockets?": true,
        "Minimum Impact Damage Threshold": 50.0
      }
    }
  }
}

Admin Channel Settings:
- Enabled? (bool): Enable/disable admin channel Discord notifications
- Include Event Messages? (bool): When true, sends event start/end messages to admin channel
- Include Impact Messages? (bool): When true, sends meteor impact details to admin channel
- Webhook URL (string): Discord webhook URL for admin notifications

Impact Filtering Settings:
- Log Player Impacts? (bool): When true, logs when meteors hit players to Discord
- Log Structure Impacts? (bool): When true, logs when meteors hit structures/bases to Discord
- Filter Smoke Rockets? (bool): When true, prevents spam from smoke rocket impacts
- Minimum Impact Damage Threshold (float): Only logs impacts with damage of this amount or higher (default: 50.0). Prevents Discord spam from low-damage hits

DISCORD RATE LIMITING:

Prevent Discord API spam and rate limits:

{
  "Logging": {
    "DiscordRateLimit": {
      "EnableRateLimit": true,
      "ImpactMessageCooldown": 1.0,
      "MaxImpactsPerMinute": 20
    }
  }
}

Settings:
- EnableRateLimit (bool): When true, enforces rate limiting to avoid Discord API throttling
- ImpactMessageCooldown (float): Minimum seconds between impact messages (default: 1.0)
- MaxImpactsPerMinute (int): Maximum impact messages sent per minute (default: 20). Discord has a per-channel webhook limit of 30 messages/minute. Setting this to 20 provides a 10 message safety buffer for other webhooks/bots posting to the same channel, preventing rate limit errors

BARRAGE SETTINGS:

Control the /cb barrage command behavior:

{
  "BarrageSettings": {
    "NumberOfRockets": 20,
    "RocketDelay": 0.33,
    "RocketSpread": 16.0
  }
}

Settings:
- NumberOfRockets (int): How many rockets fire during barrage mode (default: 20)
- RocketDelay (float): Delay in seconds between each rocket (default: 0.33)
- RocketSpread (float): Spread angle in degrees for rocket pattern (default: 16.0)

INTENSITY SETTINGS:

Each intensity level is fully customizable. Global multiplier settings apply to all intensity levels.

Global Settings:
{
  "IntensitySettings": {
    "DamageMultiplier": 1.0,
    "ItemDropMultiplier": 1.0,
    "Mild": { ... },
    "Medium": { ... },
    "Extreme": { ... }
  }
}
- DamageMultiplier (float): Global multiplier for all rocket damage (default: 1.0 = 100% full damage). Applies to all intensity levels.
- ItemDropMultiplier (float): Global multiplier for all item drop quantities across all intensity levels (default: 1.0 = 100%). Scales the min/max ranges for all dropped items. Use 0.5 for 50% drops, 2.0 for double drops, etc.

Below are the default configurations for each intensity level:

Mild Settings (Beginner Friendly):
{
  "Mild": {
    "FireRocketChance": 30,
    "Radius": 500.0,
    "Duration": 240,
    "RocketAmount": 20,
    "ItemDropControl": {
      "EnableItemDrop": true,
      "ItemsToDrop": [
        { "Shortname": "stones", "Minimum": 250, "Maximum": 500 },
        { "Shortname": "metal.ore", "Minimum": 250, "Maximum": 500 },
        { "Shortname": "sulfur.ore", "Minimum": 250, "Maximum": 500 },
        { "Shortname": "scrap", "Minimum": 10, "Maximum": 20 }
      ]
    }
  }
}

- 20 rockets over 240 seconds (4 minutes)
- 500m event radius
- Fire rockets with 30% chance
- Beginner-friendly rewards with stones, ore, and scrap

Medium Settings (Balanced):
{
  "Medium": {
    "FireRocketChance": 20,
    "Radius": 300.0,
    "Duration": 120,
    "RocketAmount": 45,
    "ItemDropControl": {
      "EnableItemDrop": true,
      "ItemsToDrop": [
        { "Shortname": "stones", "Minimum": 160, "Maximum": 250 },
        { "Shortname": "metal.fragments", "Minimum": 60, "Maximum": 120 },
        { "Shortname": "metal.refined", "Minimum": 20, "Maximum": 50 },
        { "Shortname": "sulfur.ore", "Minimum": 10, "Maximum": 20 }
      ]
    }
  }
}

- 45 rockets over 120 seconds (2 minutes)
- 300m event radius
- Fire rockets with 20% chance
- Balanced rewards with better loot

Extreme Settings (Hardcore):
{
  "Extreme": {
    "FireRocketChance": 10,
    "Radius": 100.0,
    "Duration": 30,
    "RocketAmount": 70,
    "ItemDropControl": {
      "EnableItemDrop": true,
      "ItemsToDrop": [
        { "Shortname": "stones", "Minimum": 250, "Maximum": 400 },
        { "Shortname": "metal.fragments", "Minimum": 125, "Maximum": 300 },
        { "Shortname": "metal.refined", "Minimum": 20, "Maximum": 50 },
        { "Shortname": "sulfur.ore", "Minimum": 45, "Maximum": 120 }
      ]
    }
  }
}

- 70 rockets over 30 seconds (high intensity!)
- 100m event radius (smaller, more concentrated)
- Fire rockets with 10% chance
- Extreme rewards for hardcore players

Intensity Settings Details:
- FireRocketChance (int): Percentage of rockets that will be fire rockets (0-100)
- Radius (float): Event radius in meters (area of effect)
- Duration (int): Event duration in seconds
- RocketAmount (int): Total number of rockets in the event
- ItemDropControl:
  - EnableItemDrop (bool): When true, items drop at meteor impact locations
  - ItemsToDrop (array): List of items to drop with min/max quantities
    - Shortname (string): Rust item shortname
    - Minimum (int): Minimum quantity dropped per impact
    - Maximum (int): Maximum quantity dropped per impact

EVENT TYPES

Automatic Events:
- Fixed Intervals: Consistent timing (default: 30 minutes)
- Random Timers: Unpredictable events (15-45 minute range)
- Map-wide Coverage: Events spawn across the entire map

Manual Events:
- Player Targeting: Target specific players by name
- Position-based: Target exact coordinates
- Admin-initiated: Instant event creation with real-time adjustments

Barrage Mode:
- Concentrated Attack: 20 rockets in rapid succession
- Directional Fire: Rockets fire forward from admin position
- Customizable Spread: 16-degree pattern by default

PERFORMANCE & BALANCE

Optimizations:
- CPU Usage: < 1% during events
- Memory: 2-5 MB during active events
- Smart Cleanup: Automatic entity removal
- Lag Prevention: Controlled rocket intervals

Balance Features:
- Damage Scaling: Global multiplier (default: 1.0 = 100% full damage)
- Map Boundaries: 600m buffer from edges
- Safe Zones: Respects terrain and water levels
- Performance Tuning: Configurable for different server sizes

Server Recommendations:

High Population (100+ players):
{
  "IntensitySettings": { "DamageMultiplier": 0.1 },
  "BarrageSettings": { "RocketDelay": 0.5 }
}

Low Population (< 50 players):
{
  "IntensitySettings": { "DamageMultiplier": 0.3 },
  "BarrageSettings": { "RocketDelay": 0.25 }
}

PLUGIN INTEGRATION

Discord Integration:
Built-in Discord webhook support:
- Event start/end notifications
- Impact logging with rate limiting
- Configurable public and admin webhooks

Plugin Development Hook:

Celestial Barrage provides a hook for other plugin developers:

OnCelestialBarrageImpact Hook:
void OnCelestialBarrageImpact(BaseCombatEntity entity, HitInfo info, string entityType, string ownerInfo)

Parameters:
- entity: The entity that was hit by the meteor
- info: HitInfo containing damage and impact details
- entityType: Type of entity hit ("Player", structure name, etc.)
- ownerInfo: Player name or owner information

Example Usage:
void OnCelestialBarrageImpact(BaseCombatEntity entity, HitInfo info, string entityType, string ownerInfo)
{
    if (entityType == "Player")
    {
        Puts($"Player {ownerInfo} was hit by a meteor for {info.damageTypes.Total()} damage!");
        // Add custom effects, notifications, or logging
    }
    else if (entity.OwnerID != 0)
    {
        Puts($"Structure {entityType} owned by {ownerInfo} was hit by a meteor!");
        // Handle structure impacts
    }
}

TROUBLESHOOTING

Common Issues:

Events Not Starting:
- Verify "EnableAutomaticEvents": true
- Check console for timer messages
- Ensure minimum 4-minute intervals

No Rockets Spawning:
- Check damage multiplier settings
- Verify map boundaries
- Confirm rocket amount > 0

Items Not Dropping:
- Verify "EnableItemDrop": true
- Check global drop multiplier
- Ensure valid item shortnames

Performance Issues:
- Reduce rocket amounts
- Increase rocket delays
- Lower damage multiplier

SUPPORT & COMMUNITY

- Report Issues: https://github.com/FtuoilXelrash/CelestialBarrage/issues
- Discord Support: https://discord.gg/G8mfZH2TMp
- Download Latest: https://github.com/FtuoilXelrash/CelestialBarrage/releases

DEVELOPMENT & TESTING SERVER

Darktidia Solo Only - See CelestialBarrage and other custom plugins in action!
All players are welcome to join our development server where plugins are tested
and refined in a live environment.

- Server: Darktidia Solo Only | Monthly | 2x | 50% Upkeep | No BP Wipes
- Find Server: https://www.battlemetrics.com/servers/rust/33277489

Experience the plugin live, test configurations, and provide feedback in a real
gameplay setting.

CONTRIBUTING

We welcome contributions! To get started:

1. Fork the repository
2. Create a feature branch (git checkout -b feature/amazing-feature)
3. Follow Umod coding standards
4. Test thoroughly with all intensity levels
5. Submit a pull request with detailed description

LICENSE

This project is licensed under the MIT License - see the LICENSE file for details.

ACKNOWLEDGMENTS

Special thanks to the Rain of Fire plugin (https://umod.org/plugins/rain-of-fire) by k1lly0u, which served as inspiration and a valuable learning tool during the development of Celestial Barrage. While Celestial Barrage has evolved into its own unique implementation with advanced features like three intensity levels, Discord integration, and sophisticated configuration options, the foundational concept of meteor events was inspired by this original work.

AUTHOR

Ftuoil Xelrash
- GitHub: @FtuoilXelrash
- Discord: Plugin Support Server (https://discord.gg/G8mfZH2TMp)

Star this repository if Celestial Barrage enhances your Rust server!