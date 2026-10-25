"""Per-subsystem scorecard: exe code size, functions, studied share, dialog coverage, Unity status and conversion
estimate; writes codemap.json + codemap_functions.csv for the report and for continued work."""
import json, os, collections, csv

HERE = os.path.dirname(os.path.abspath(__file__))
C = json.load(open(os.path.join(HERE, 'classified.json')))
U = json.load(open(os.path.join(HERE, 'unity_usage.json')))

# status: converted | partial | missing | replaced (not converted on purpose: Unity / our server / libraries)
S = {
    'Vehicle physics': ('converted', .96, 'DriftCar.cs (assists, PvP multipliers, battle top speeds, durability), CarAssists.cs, ChaseCamera.cs (jump lift), Fuel/MittronFuel.cs (per-car tank) (PHYSICS.md)', 'camera shake 0x479050, the server-set boost-end chance'),
    'Traffic & HUV AI': ('converted', .95, 'TrafficSystem.cs (HUV pose modes), HuvRoutes.cs, ChaseRules.cs, Convoys.cs (kept escorts), Passerby.cs, Trains.cs, PvpTraffic.cs, ChaseMission.cs (TRAFFIC.md)', 'road-map interpretation points marked inferred'),
    'PvP races, channels & gates': ('converted', .96, 'Pvp*.cs (incl. PvpMissionBattle, PvpGateEffects, PvpTourRewardGm, PvpReplay, PvpGhost), LapTimeBattle.cs, Ladder.cs, Tournament.cs, RaceMission.cs, OnlineRace.cs, RealMatchSkin.cs, server race.js/laptime.js/pvptour.js (PVP.md)', 'mission-battle objects are client-side online'),
    'Missions (quests, delivery, patrol, stations)': ('converted', .96, 'QuestTracker.cs, StepQuests.cs, StepQuestRunner.cs, QsRankList.cs, StationPanel.cs (incl. INSTANT and event pair), DeliveryService.cs, InstantMissions.cs, PatrolMission.cs, MitaDrink.cs, MissionCommon.cs, Services/WorldMapPages.cs, server economy.js/partyplay.js/quests.js (MISSIONS.md)', '-'),
    'Events, gacha, lotto, coupons': ('converted', .92, 'Events/* (incl. GachaInven, PickEm reels, HarvestScreens, CouponStamp, ObEvent, PropEvent, EventArtFallback), server events.js (EVENTS.md)', 'art missing from this install (stand-ins)'),
    'Packet handlers (server replies)': ('converted', .93, 'Net/OnlineSession.cs, Net/ServerNotices.cs, Services/*, feature routers + DriftCityServer (HANDLERS.md: 365 covered, 22 missing)', 'sticker upload service, a few GM/debug replies'),
    'Effects': ('converted', .96, 'CarEffects.cs, NifParticleEffect.cs, GameEffects.cs, OrosAmbience.cs, PvpMissionBattle (t_ob_event_04) (EFFECTS.md)', '-'),
    'Custom shop (visual items)': ('converted', .95, 'CustomShop.cs, Shops/*, VShopCategories.cs, CustomMarks.cs (local import), server vshop.js (SHOPS.md)', 'G-Coin (no real money by design)'),
    'Inventory & items': ('converted', .96, 'Inventory/* (NDLG default, CarParts row 2 and options, SetParts, ItemPeriods, QuickSlots, BattleQuickSlots, BattleItemEffects, StickerPage), server items.js/carparts.js/trade.js (INVENTORY.md)', '-'),
    'Data tables': ('converted', .93, 'GameTables.cs, XltTable, CityMapInfo.cs, RewardGroups.cs, VShopCategories.cs, NameFilter.cs, CityAmbience EnvTable, server gamedata.js (TABLES.md)', 'tables of features the client never used'),
    'World streaming': ('converted', .97, 'CityLoader.cs (ground shadows), CityStreaming.cs (window, per-object fade), Core/CityMap.cs, RoadMapQuery.cs', '-'),
    'UI framework': ('converted', .92, 'UI/NuiView.cs (exe control states, UI.md), UiManager.cs, OriginalMenus.cs', 'IME composition, statics with six-digit colours, colour easing and per-state sounds'),
    'Vehicle visuals & parts': ('converted', .93, 'CarFactory.cs (_s fallback and LOD), CarPaint.cs, CustomMarks.cs, PartsCatalog.cs', 'mark placement on cars without paint-layout sides'),
    'Teams & team battles': ('converted', .95, 'Social/Teams.cs, TeamBattle.cs, ChannelBattle.cs (limits, leader, ghost tickets), CrewMarkUpload.cs, server teams.js/channelbattle.js (SOCIAL.md)', '-'),
    'UI screens (misc dialogs)': ('converted', .95, 'DriverInfoScreen.cs, Social/Community.cs, BasicBarSocial.cs, Mail.cs, HelpWindow.cs, CarAudio.cs, GuideText.cs, ChatBalloon.cs, AeroSet.cs, AutoFinder.cs, Fuel/*, Services/* (SCREENS.md, SERVICES.md)', '-'),
    'Front end (login, server, characters)': ('converted', .97, 'GameFlow.FrontEnd.cs, CharacterDeletion.cs, IntroScene.cs', '-'),
    'HUD': ('converted', .95, 'Hud.cs (ADV and basic bars), HudPanels.cs (notebook, item shop, speed items), Minimap.cs, NamePlate.cs, DriftCombo.cs, QuickSlots.cs, GuideText.cs, CarAudio.cs, BonusBadges', '-'),
    'Party & friends': ('converted', .92, 'Social/PartyWindow.cs, Friends.cs, BasicBarSocial.cs, PlayerMenu.cs, SocialCore.cs, Services/Recommend.cs, server social.js/world.js (SOCIAL.md)', 'Hangame friend invites (Korean service)'),
    'Car dealer': ('converted', .90, 'Shops/AdvancedDealer.cs, Menus.ClassicCarShop, Events/CouponBook (SHOPS.md)', 'growth chart 3290000, car-bonus rows, promotion notice'),
    'Chat & messages': ('converted', .95, 'Net/ChatBox.cs (modes incl. private channels, prefixes, whispers, word filter), UI/MsgBox.cs, NameFilter.cs (SCREENS.md, AUDIO.md)', '-'),
    'Progress & rewards': ('converted', .85, 'Progress.cs, Multipliers.cs (golden time, Santa, anniversary), BonusBadges, PlayerProfile.cs, server workshop.js/multipliers.js (PROGRESS.md)', 'PC-room bonus (no PC rooms)'),
    'Undercity': ('converted', .95, 'UndercityStage.cs (+Online), UndercityRoads.cs (0x716890 towers), UndercityHud.cs, UndercityEntrance.cs, UndercityParty.cs, UndercityData.cs, server undercity.js (UNDERCITY.md)', 'road-map points marked inferred'),
    'Parts shop, manufacturing & licences': ('converted', .93, 'WorkshopMenus.cs, Workshop.cs, Licences.cs (LicencePages: LICENSE 710000, OTHERLISENCEBASE 3070000)', '-'),
    'Renderer': ('converted', .80, 'Shaders/*, OriginalLook.cs', 'projected shadows, post effects (blur, DOF)'),
    'Audio': ('converted', .92, 'AudioLibrary.cs (substitutes), Audio/CityAmbience.cs (env.xlt), Audio/VoiceLines.cs, EngineAudio.cs, CarSounds.cs (AUDIO.md)', 'SID_STREET_AMBIENT (registered, never played by the exe)'),
    'Stages & flow': ('converted', .96, 'GameFlow.cs, ParkingLotStage.cs (tutorial car, arrival spots), UndercityStage.cs, EndingCredits.cs, Tutorial/*', '-'),
    'Input': ('converted', .90, 'Options/KeyBindings.cs, DriftCar input, UiManager hotkeys', '-'),
    'Auction': ('converted', .85, 'Auction/AuctionHouse.cs, AuctionMarket.cs, AuctionPage.cs, server auction.js/goods.js (SHOPS.md)', 'bidding UI (the client window has none), the original server bidding rules'),
    'Camera': ('partial', .75, 'ChaseCamera.cs, PvpCameras.cs (pvpready/pvpstart/pvpgoalin, VIEWING)', 'watch / spot-watch / ending cameras, rear and side views'),
    'HUV chases': ('converted', .95, 'ChaseMission.cs, ChaseScreens.cs, ChaseRules.cs, RushTime.cs, VirtualChase.cs (server-run with real players), AutoDrive.cs, server huvrules.js/huvevents.js/partyplay.js (CHASES.md)', 'V-HUV party notice and team points, 10961 coupons'),
    'Options & quit': ('converted', .95, 'Options/GameOptions.cs, OriginalMenus.Options.cs, OriginalMenus.ClassicOptions.cs, KeyBindings.cs, quit menu (OPTIONS.md)', 'DOF/environment maps stored but not drawn'),
    'Lighting, sky & weather': ('converted', .90, 'OriginalLook.cs, WeatherClock.cs, LensFlare.cs', '-'),
    'Garage': ('converted', .90, 'Menus.Garage, GarageLighting', '-'),
    'UI screens (other)': ('missing', 0, '-', 'delivery play info, licence pages'),
    'Unlabelled game code': ('partial', .25, '-', 'a few small runs (UNLABELLED.md)'),
    'Networking': ('replaced', None, 'Net/NetClient.cs (WebSocket JSON) + DriftCityServer', 'the original TCP protocol is replaced by our own'),
    'Platform & system': ('replaced', None, 'Unity player', 'GameGuard, Hangame auth, crypto, CPU detection, crash dumps'),
    'LIB ODE physics': ('replaced', None, 'Unity PhysX + DriftCar.cs', 'third-party library'),
    'LIB C runtime & compiler stubs': ('replaced', None, '.NET', 'compiler output'),
    'LIB XML parser': ('replaced', None, '.NET', 'third-party library'),
    'LIB OPCODE collision': ('replaced', None, 'Unity PhysX', 'third-party library'),
}

agg = collections.defaultdict(lambda: {'bytes': 0, 'fns': 0, 'studied': 0, 'ranges': collections.Counter()})
for c in C:
    a = agg[c['sub']]
    a['bytes'] += c['size']; a['fns'] += 1
    if c['studied']: a['studied'] += c['size']
    a['ranges'][int(c['entry'], 16) >> 16] += c['size']
dlg = collections.defaultdict(lambda: [0, 0, []])
for d in U['dialogs']:
    if d['exe_bytes'] and d['sub']:
        x = dlg[d['sub']]; x[0] += d['exe_bytes']
        if d['opened'] or d['used_controls']: x[1] += d['exe_bytes']
        else: x[2].append(f"{d['key']} {d['id']}")
rows = []
for sub, a in agg.items():
    st, frac, files, gaps = S.get(sub, ('partial', .25, '-', '-'))
    rows.append({'subsystem': sub, 'kb': round(a['bytes'] / 1024, 1), 'functions': a['fns'], 'studied': round(100 * a['studied'] / max(1, a['bytes']), 1),
                 'dialog_cov': round(100 * dlg[sub][1] / dlg[sub][0], 1) if dlg[sub][0] else None, 'missing_dialogs': dlg[sub][2][:12],
                 'status': st, 'converted': frac, 'unity': files, 'gaps': gaps,
                 'ranges': [f"0x{r << 16:06x}" for r, _ in a['ranges'].most_common(4)]})
rows.sort(key=lambda r: -r['kb'])
game = [r for r in rows if r['status'] != 'replaced']
gb = sum(r['kb'] for r in game)
conv = sum(r['kb'] * r['converted'] for r in game)
summary = {'functions': len(C), 'total_kb': round(sum(r['kb'] for r in rows), 1), 'game_kb': round(gb, 1),
           'replaced_kb': round(sum(r['kb'] for r in rows if r['status'] == 'replaced'), 1), 'converted_pct': round(100 * conv / gb, 1),
           'studied_functions': sum(1 for c in C if c['studied']),
           'dialogs_total': sum(1 for d in U['dialogs'] if d['exe_bytes']), 'dialogs_used': sum(1 for d in U['dialogs'] if d['exe_bytes'] and (d['opened'] or d['used_controls'])),
           'files_total': len(U['files']), 'files_used': sum(1 for f in U['files'] if f['used'])}
json.dump({'summary': summary, 'subsystems': rows}, open(os.path.join(HERE, 'codemap.json'), 'w'), indent=1)
with open(os.path.join(HERE, 'codemap_functions.csv'), 'w', newline='') as fh:
    w = csv.writer(fh); w.writerow(['entry', 'size', 'subsystem', 'evidence', 'studied_in'])
    for c in C: w.writerow([c['entry'], c['size'], c['sub'], c['how'], ';'.join(c['studied'])])
print(json.dumps(summary, indent=1))
for r in rows: print(f"{r['subsystem']:46s} {r['kb']:7.1f} KB {r['status']:9s} {('' if r['converted'] is None else str(int(r['converted']*100))+'%'):>5s}")
