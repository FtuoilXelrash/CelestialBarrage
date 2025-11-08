Celestial Barrage

Game: Rust
Framework: Umod
Version: 0.0.825
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

The plugin creates oxide/config/CelestialBarrage.json with these key settings:

Global Options:
{
  "Options": {
    "EnableAutomaticEvents": true,
    "EventTimers": {
      "EventInterval": 30,
      "UseRandomTimer": false,
      "RandomTimerMin": 15,
      "RandomTimerMax": 45
    },
    "GlobalDropMultiplier": 1.0,
    "NotifyEvent": true
  }
}

Warning Countdown:

The WarningCountdown feature adds a pre-event delay before meteor showers begin, giving
your server time to prepare and perform a final performance check:

{
  "Options": {
    "WarningCountdown": {
      "EnableWarning": true,
      "CountdownSeconds": 10
    }
  }
}

Settings:
- EnableWarning: Set to true to activate the countdown timer before events start
- CountdownSeconds: Number of seconds to wait before the actual meteor event begins (default: 10)

How It Works:
1. When a meteor event is triggered, if WarningCountdown is enabled, a countdown timer starts
2. During the countdown period, the server rechecks FPS (if PerformanceMonitoring.EnableFPSCheck is enabled)
3. If FPS remains above the minimum threshold → meteor event begins normally
4. If FPS drops below the minimum → event is cancelled and a Discord notification is sent (if configured)

Performance Benefit:
This feature provides an additional safety layer to prevent resource-intensive meteor events
from launching during periods of server lag, protecting your players from unexpected performance issues.

Intensity Settings:

Mild Settings (Beginner Friendly):
- Fire Rocket Chance: 30%
- Radius: 500m
- Duration: 240 seconds
- Rockets: 20
- Drops: Stones (80-120), Metal Ore (25-50)

Medium Settings (Balanced):
- Fire Rocket Chance: 20%
- Radius: 300m
- Duration: 120 seconds
- Rockets: 45
- Drops: Stones (160-250), Metal Fragments (60-120), HQ Metal Ore (20-50)

Extreme Settings (Hardcore):
- Fire Rocket Chance: 10%
- Radius: 100m
- Duration: 30 seconds
- Rockets: 70
- Drops: Stones (250-400), Metal Fragments (125-300), Refined Metal (20-50), Sulfur Ore (45-120)

Barrage Settings:
{
  "BarrageSettings": {
    "NumberOfRockets": 20,
    "RocketDelay": 0.33,
    "RocketSpread": 16.0
  }
}

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
- Damage Scaling: Global multiplier (default: 0.2x)
- Map Boundaries: 600m buffer from edges
- Safe Zones: Respects terrain and water levels
- Performance Tuning: Configurable for different server sizes

Server Recommendations:

High Population (100+ players):
{
  "DamageControl": { "DamageMultiplier": 0.1 },
  "BarrageSettings": { "RocketDelay": 0.5 }
}

Low Population (< 50 players):
{
  "DamageControl": { "DamageMultiplier": 0.3 },
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