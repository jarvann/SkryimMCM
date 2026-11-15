# Skyrim MCP — Feature Roadmap

Based on a comprehensive analysis of CommonLibSSE-NG's API surface (~200+ classes, 50+ singletons).

## Release plan (decided 2026-11-15)

Each open item in `project_work_items.json` has a `targetRelease` field: `1.3`, `1.3-stretch`, `2.0` or `parked`.

- **1.3** improves SkyLink as it is: Claude Desktop, one connection to the game, the same Claude Desktop config. Nothing players or fork authors rely on changes.
- **2.0** puts the AI inside the game: new background processes, pipe protocol v2 (push events, several clients at once) and many more tools. That breaks fork add-ons and Claude Desktop configs, so it gets a major version.
- **1.4** takes whatever slips out of 1.3.

### 1.3: fixes, appearance, safety

| Area | Items |
|------|-------|
| Player-reported bugs | BUG-013 plugin-not-loaded error, BUG-014 blank weather name, BUG-015 magicka maximum, BUG-016 "unarmed" as an item, BUG-012 GOG (waiting on details) |
| Usability | FEAT-205 equip/unequip tools, FEAT-210 wait for Skyrim instead of failing |
| Responses | INFRA-015 smaller event pages, INFRA-016 load-order paging, INFRA-017 menu-state wording, INFRA-018 reconnect test |
| Appearance | FEAT-226 hair and skin colour (no RaceMenu), FEAT-227 RaceMenu integration, FEAT-208 appearance writes, FEAT-209 slider and part indexes |
| Blueprint, first half | FEAT-206 portable form IDs, FEAT-207 one-call apply with dry run; FEAT-121 full apply as a stretch |
| Crash safety | FEAT-220 SEH around every game-thread task, FEAT-219 crash recorder, FEAT-217 crash-log reader |
| Setup | INFRA-020 configurable paths (MO2, Wabbajack), INFRA-006 config file |
| Small additions | FEAT-218 loot by category, FEAT-221 guidebook, FEAT-224 notes and goals, FEAT-150 Papyrus stats |
| Housekeeping | INFRA-019 scripted smoke test (build early); test FEAT-131 volume, FEAT-133 screenshots, INFRA-014 form search |

Estimate: 50-80 human hours; Claude 4-6 hours and 1.5-2.5M tokens.

### 2.0: the AI inside Skyrim

| Area | Items |
|------|-------|
| In-game AI | FEAT-212 text chat, FEAT-213 chat window, FEAT-214 voice; other providers including Ollama |
| Protocol v2 (breaking) | INFRA-008 push events, INFRA-009 several clients at once |
| On screen | FEAT-225 live quest tracker, FEAT-223 waypoints, FEAT-112 map markers, FEAT-222 resource alerts |
| AI acting in the world | FEAT-215 NPC orders, FEAT-216 player autopilot, FEAT-122 spawn NPC from blueprint, FEAT-125 scripted scenes, FEAT-120 quest completion scripts |
| Plugin framework | INFRA-010 detect SKSE plugins, FEAT-203/204 per-plugin tools (with generic list-and-run tools, since Claude Desktop only shows new tools in a new chat) |
| Runtime creators | FEAT-126 spells, FEAT-127 enchantments (saves must keep the new forms) |

Estimate: 150-250 human hours; Claude 12-18 hours and 4-6M tokens. Open decision: KringleKiller's fork already has the in-game client, quest tracker, waypoints and notes. Take their code, build our own, or build it with them.

### Parked

FEAT-013 teleport without console commands, FEAT-015 force a new game, FEAT-113 journal, INFRA-005 VPrint hook, INFRA-007 caching.

---

## Current State (v0.2)

### Working (Direct Engine API)
- Player info (name, race, level, stats, position, cell)
- Full inventory read (items, types, weights, values)
- Gold count
- Add/remove items
- Set actor values (health, magicka, stamina, all 18 skills)
- Equip/unequip items
- Add/remove spells and perks
- Active quest list with objectives
- Nearby NPC detection (name, race, level, distance, hostility)
- Actor info query by FormID
- Move actor to player/reference
- Set relationship rank
- Active magical effects
- Game time read
- Combat status
- Load order query
- Mod FormID prefix lookup
- Form search by name/editor ID (replacement for `help` command)
- Save game (via BGSSaveLoadManager)
- Start/Stop quests (direct API)

### Broken (Depends on CompileAndRun)
- Console command execution
- Teleportation (coc)
- Set quest stage (setstage)
- Complete quest objectives
- Set game time
- Toggle god mode / collision

---

## Phase 1 — Fix Console Commands & Core Stability

**Priority: Critical**

- [x] Fix `CompileAndRun` crash — ConsoleUtilSSE-style relocation fix (RELOCATION_ID 441582 for patch >= 1130)
- [x] Direct API: God mode via static bool flip, Collision via SetCollision(), Time via Calendar
- [x] SEH crash protection — __try/__except catches access violations safely
- [x] Connection health monitoring — ping action
- [ ] Teleport via direct API (works via console command for now)

## Phase 2 — Deep Player Management

**Priority: High** | **API: RE::PlayerCharacter, RE::TESNPC, RE::ActorValueOwner**

- [x] **Skill Management** — Read all 18 skills via GetSkillLevels, write via SetActorValue
- [x] **Perk Browser** — List acquired perks via GetPerks, add/remove perks
- [x] **Spell Book** — List all known spells via GetKnownSpells with school/type classification
- [x] **Shout Management** — List shouts via GetKnownShouts with per-word detail, unlock via UnlockShout
- [x] **Equipped Items** — GetEquippedItems shows all biped slots + wielded weapons
- [x] **Character Blueprint** — GetCharacterBlueprint exports everything in one call (skills, perks, spells, shouts, equipment, inventory, gold, active effects)
- [ ] **Level Management** — Set level, add XP, show XP to next level
- [ ] **Appearance Query** — Read race, sex, head parts, face morphs (19 facial values via `TESNPC::FaceData`)
- [ ] **Carry Weight** — Read/set carry weight, show encumbrance status
- [ ] **Favorites Management** — Read/write spell and item favorites via `MagicFavorites`

## Phase 3 — World Interaction

**Priority: High** | **API: RE::TESObjectCELL, RE::TESWorldSpace, RE::Calendar, RE::TESWeather**

- [ ] **Weather Control** — Get current weather, set weather (clear, rain, snow, storm), wind speed/direction
- [ ] **Time Control** — Get/set game hour, day, month, year, day of week, time scale multiplier
- [ ] **Cell Info** — Current cell details (interior/exterior, water, lighting, encounter zone)
- [ ] **World Position** — Exact coordinates, worldspace name, region
- [ ] **Object Interaction** — Lock/unlock doors, activate objects, read container contents
- [ ] **Map Markers** — Discover/undiscover map markers, list discovered locations
- [ ] **Placed Object Query** — Find specific objects in current cell (chests, crafting stations, NPCs)

## Phase 4 — Quest Intelligence

**Priority: High** | **API: RE::TESQuest, RE::BGSQuestObjective, RE::BGSStoryEvent**

- [ ] **Quest Stage Browser** — List all stages for a quest, show which are completed
- [ ] **Quest Variable Read** — Read quest script variables (safe alternative to `sqv`)
- [ ] **Objective Management** — Display/complete/fail individual objectives
- [ ] **Quest Item Tracking** — List items flagged as quest objects, which quest they belong to
- [ ] **Story Event Tracking** — Monitor major story milestones
- [ ] **Quest Alias Resolution** — Resolve quest aliases to actual NPCs/objects
- [ ] **Quest Dependency Mapping** — Show quest prerequisites and follow-up quests

## Phase 5 — NPC & Faction Deep Dive

**Priority: Medium** | **API: RE::Actor, RE::TESFaction, RE::BGSRelationship, RE::CombatController**

- [ ] **Faction Membership** — List player's factions with ranks, join/leave factions, set rank
- [ ] **NPC Detailed Info** — Class, combat style, voice type, outfit, home location
- [ ] **NPC Disposition** — Read/set relationship rank, faction reactions
- [ ] **NPC Inventory** — Read any NPC's inventory (not just player)
- [ ] **NPC State** — Is essential, is protected, is ghost, is child, is follower
- [ ] **Follower Management** — List current followers, dismiss, recruit (via faction/relationship)
- [ ] **Combat AI** — Read aggression level, confidence, combat style, current target
- [ ] **Detection** — Player detection state per NPC (unaware, suspicious, detected)

## Phase 6 — Magic & Effects Deep Dive

**Priority: Medium** | **API: RE::MagicTarget, RE::ActiveEffect, RE::EffectSetting, RE::EnchantmentItem**

- [ ] **Active Effects Browser** — Detailed view of all active effects with remaining duration
- [ ] **Disease/Cure Management** — List diseases, cure specific diseases, check vampirism/lycanthropy
- [ ] **Enchantment Info** — Read enchantments on equipped items, charge levels
- [ ] **Magic Resistance** — Read all resistance values (fire, frost, shock, poison, magic, disease)
- [ ] **Spell Details** — Cost, casting type, delivery, school, effect list per spell
- [ ] **Power Management** — Daily powers usage tracking, lesser powers

## Phase 7 — Economy & Trade

**Priority: Medium** | **API: RE::InventoryChanges, RE::TESFaction**

- [ ] **Merchant Detection** — Find nearby merchants, their specializations
- [ ] **Merchant Inventory** — Read what a merchant has for sale
- [ ] **Price Calculation** — Calculate buy/sell prices accounting for Speech skill and perks
- [ ] **Bounty Management** — Read/clear bounties per hold
- [ ] **Property Ownership** — List owned houses, available properties

## Phase 8 — Combat Analysis

**Priority: Medium** | **API: RE::CombatController, RE::CombatGroup, RE::CombatInventory**

- [ ] **Combat Status** — Detailed combat state (target, group members, fleeing status)
- [ ] **Damage Analysis** — Current weapon damage, armor rating, resistances
- [ ] **Combat Log** — Track recent hits, kills, spell casts during combat
- [ ] **Threat Assessment** — Nearby hostile actors with levels and equipment
- [ ] **Kill Stats** — Total kills by type

## Phase 9 — UI & Notifications

**Priority: Low** | **API: RE::UI, RE::BGSMessage**

- [ ] **Menu State Query** — Which menus are open, is game paused
- [ ] **Notification System** — Send custom notifications to the player's HUD
- [ ] **Map Marker Management** — Add/remove custom map markers
- [ ] **Journal Integration** — Read journal entries, add custom notes

## Phase 10 — Advanced Automation

**Priority: Low** | **Composite features built on earlier phases**

- [ ] **Quest Completion Scripts** — Multi-step quest resolution (set stages, add items, move NPCs, update factions)
- [ ] **Character Build Planner** — Set all skills, perks, spells to match a build template
- [ ] **Apply Character Blueprint** — Import a blueprint JSON and apply it: set skills → add perks → add spells → teach shouts → add items → equip items → set stats. Orchestrated in C# as sequential pipe calls with error-per-item handling
- [ ] **Spawn NPC From Blueprint** — Create a new NPC at runtime using a character blueprint's face morphs, stats, skills, perks, spells, and equipment. Add to `CurrentFollowerFaction` (0x0005C84E) + set relationship rank for follower behavior. Assign a voice type. Steps: create TESNPC form → set face data (19 morphs + head parts) → set race/sex → place reference in world → apply stats/perks/spells → add/equip items → configure follower AI. Optionally use NFF template NPC as base for cleaner persistence. Runtime NPCs get `FFxxxxxx` FormIDs — persistence across saves needs testing.
- [ ] **Inventory Optimizer** — Suggest items to sell/keep based on weight/value ratio
- [ ] **Loot Scanner** — Scan nearby containers and report valuable items
- [ ] **Mod Conflict Detector** — Check for FormID conflicts across loaded mods
- [ ] **Game State Snapshot** — Full dump of player state for backup/analysis
- [ ] **Scripted Scenes** — Move NPCs, set relationships, trigger events for custom scenarios

## Phase 11 — Audio & Animation (Experimental)

**Priority: Low** | **API: RE::BSAudioManager, RE::BSMusicManager, RE::BipedAnim**

- [ ] **Music Control** — Change music track, battle music state
- [ ] **Volume Control** — Master/category volume adjustment
- [ ] **Play Idle Animations** — Trigger specific idles on player or NPCs
- [ ] **Screenshot Trigger** — Take screenshots programmatically

---

## Technical Debt & Infrastructure

- [ ] **VPrint Hook** — Full console output capture for multi-line command results
- [ ] **Config File** — `.ini` or `.toml` for pipe name, log level, feature toggles
- [ ] **Error Recovery** — Automatic Script object cleanup after SEH exceptions
- [ ] **Performance** — Cache frequently queried data (inventory, quest list) to reduce game thread blocking
- [ ] **Pipe Protocol v2** — Batch requests, subscriptions (push notifications for events like combat start, quest update)
- [ ] **Event System** — Subscribe to game events (level up, quest complete, combat start/end, cell change) via SKSE event sinks
- [ ] **Multi-client** — Support multiple MCP clients simultaneously (Claude + other tools)

---

## Key Singletons Reference

| Singleton | Access | Purpose |
|-----------|--------|---------|
| `RE::PlayerCharacter::GetSingleton()` | Player actor | Stats, inventory, spells, position |
| `RE::TESDataHandler::GetSingleton()` | Form database | All forms, mods, load order |
| `RE::UI::GetSingleton()` | UI state | Menu queries, pause state |
| `RE::Calendar::GetSingleton()` | Time | Game hour, date, day of week |
| `RE::BGSSaveLoadManager::GetSingleton()` | Save system | Save/load game files |
| `RE::ProcessLists::GetSingleton()` | Actor lists | Nearby loaded actors |
| `RE::BSAudioManager::GetSingleton()` | Audio | Volume, music control |
| `RE::BSInputDeviceManager::GetSingleton()` | Input | Key/mouse/gamepad state |
| `RE::PlayerControls::GetSingleton()` | Movement | Player input state |
| `RE::ActorEquipManager::GetSingleton()` | Equipment | Equip/unequip items |
| `RE::ConsoleLog::GetSingleton()` | Console | Output capture |
| `RE::MagicFavorites::GetSingleton()` | Favorites | Spell/item favorites |
