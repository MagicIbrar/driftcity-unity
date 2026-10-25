"""Subsystem per function: rules over its strings, game.nui dialog keys and packet-handler role, then smoothing along
address order (MSVC keeps an object file's functions together). Writes classified.json and prints a summary."""
import json, re, os, collections

HERE = os.path.dirname(os.path.abspath(__file__))
F = json.load(open(os.path.join(HERE, 'funcs.json')))
EV = json.load(open(os.path.join(HERE, 'evidence.json')))

# dialog key -> subsystem
UI = [
    (r'^(LOGIN|SELECETSERVER|SELECTSERVER|SELECT|SELECTCHARACTER|CHARLICENSE\d?|CREATECHARACTER|CHARCREATESELECT|CREATECAR|GAMELOADING|GAMELOADINGBAR|USERCHAR)$', 'Front end (login, server, characters)'),
    (r'^(SPEEDMETER|MAINBUTTON|MINIMAP|VIEWMAP|NAME|SPEEDCOMBO|EXPBONUS|PCROOMBONUS|SANTABONUS|TREEBONUS|3RDBONUS|LAPTIMENOTICE|EMERGENCY|NOTICE)$', 'HUD'),
    (r'^(CHAT|CHATCLOSE|WHISPER|MSGBOX)$', 'Chat & messages'),
    (r'^(GARAGE|CARKEYINFO)$', 'Garage'),
    (r'^(AUTOSHOP\w*|CARSHOP)$', 'Car dealer'),
    (r'^(VSHOP|STICKER|DECAL)$', 'Custom shop (visual items)'),
    (r'^(PARTSSHOP|PARTSINFO|PARTSLIST|MAKELIST|MAKEDETAIL|COMBI|MODIFY|NLICENSE|LICENSE|SKILLBOOK)$', 'Parts shop, manufacturing & licences'),
    (r'^(INVEN|MYINVEN|INVENBASE|ITEMINFO|ITEMQUICKSLOT|TOKENBOOK|TRADE|POPUP\w*|GACHARESULT|PACKAGE|LUCKYBAG|SPEEDITEM)$', 'Inventory & items'),
    (r'^(MY|USERINFO2ND|NOTE|COMM|COMMAND|MSG|BALLOON|GUIDETEXT|HELPADV|CMN|DIALOG|FULLCOVER|SCROLL|VIDEOADV|VIEWING|CARAUDIO|AEROSET|VC)$', 'UI screens (misc dialogs)'),
    (r'^(LAPTIMEPLAY|LAPTIMERET|QSRANKLIST|LADDERRANKING|APPRENTICE)$', 'PvP races, channels & gates'),
    (r'^(RUSHINFO|CHASING|AUTOFINDER|AUTODRIVING)$', 'HUV chases'),
    (r'^(UCRESULT)$', 'Undercity'),
    (r'^(ARBEITPLAYINFO|QUESTPLAYINFO)$', 'Missions (quests, delivery, patrol, stations)'),
    (r'^(GARAGETOP)$', 'Garage'),
    (r'^(QUEST|MYQUEST|QUESTDIALOG|QUEST_POST|MYARBEIT|ARBEIT|INSTANT\w*|ENTRANCE|PATROL\w*|PARTYPATROL)$', 'Missions (quests, delivery, patrol, stations)'),
    (r'^(CHASE\w*|PARTYCHASERESULT|RUSHWAIT|RUSHRESULT|VIRTUAL|CHARGER\w*)$', 'HUV chases'),
    (r'^(PVP\w*|PVPENTRY|TOUR|SELGATE|SELCH|CHARGERPVP|LADDER)$', 'PvP races, channels & gates'),
    (r'^(TC|MYTEAM|TEAM\w*)$', 'Teams & team battles'),
    (r'^(PARTY\w*|FRIEND\w*|MYFRIEND|ADDFRIEND|USERINFO|NICNAME)$', 'Party & friends'),
    (r'^(AUCTION\w*)$', 'Auction'),
    (r'^(GACHA\w*|LOTTO|COUPON\w*|EVENT\w*|OBEVENT|SEASONEVENT|THANKSGIVING|XMAS\w*|VALEN\w*|HALLO\w*)$', 'Events, gacha, lotto, coupons'),
    (r'^(OPTION\w*|QUIT)$', 'Options & quit'),
    (r'^(UC)$', 'Undercity'),
]
RULES = [  # string pattern -> subsystem (first match wins)
    (r'ode[\\/]ode|dGeom|dBody|dJoint|Bad argument\(s\)|joint->node|collision_', 'LIB ODE physics'),
    (r'Temporal coherence|SkipPrimitiveTests|Closest hit doesn|Higher distance bound', 'LIB OPCODE collision'),
    (r'<!\[CDATA\[|encoding="|standalone', 'LIB XML parser'),
    (r'Advanced Micro Devices|Cx5x86|Duron|Windows 95|LANMANNT|GenuineIntel', 'Platform & system'),
    (r'Cryptographic Provider|GameGuard|npgame|HanAuth|Hangame|hangame|AuthTCP|Auth Server', 'Platform & system'),
    (r'GameTCP|AreaTCP|XiGameClientNet|SessionLog|Lobby|P2P|Packet|packet|Socket|socket', 'Networking'),
    (r'GameLog_|_GameLog|SymSetOptions|dbghelp|MiniDump|Log', 'Platform & system'),
    (r'^Init[/\\]|Open FAILED|\.xlt\b|GlobalText|Slang|LoadVehicleList', 'Data tables'),
    (r'IME|Imm[A-Z]|ScriptString|GetControl Fail|UI Sort|UI Render|FILEINFO|\.nui\b', 'UI framework'),
    (r'D3DERR|XrRenderer|shader|Shader|\.fx\b|AGCar|BumpZ|ProjShadow|g_m|Draw %d|Render', 'Renderer'),
    (r'terrain0|blockobj|object0\.loi|ThreadLoad|BlockObj|shadow\.dat|World\\|col_floor|Land |\.lof\b|\.lbf\b', 'World streaming'),
    (r'camera\.ini|camera_gi|AnimatedCamera|Spot-Watch|normalview|vCameraPos', 'Camera'),
    (r'\.wav\b|\.mp3\b|AIL|audio[\\/]|soundinst|Sound|BGM', 'Audio'),
    (r'skydome|SkyDome|weather|skycolor|lensflare|flare\.ini|light\.ini', 'Lighting, sky & weather'),
    (r'XiDynamicControl|isFlying|FL:%|LF:%|SW:%d|DIFF:|levelmodifier|car\.ini', 'Vehicle physics'),
    (r'traffic|Traffic|HUV|huv|Convoy|trainPath|chpath|\.tcs\b|pcs update|passerby|LookForWay', 'Traffic & HUV AI'),
    (r'vehicle[\\/]|_dm\b|spoiler|Spoiler|decal|Decal|sticker|Sticker|wheel|tire|booster|parts\d|_parts|neon', 'Vehicle visuals & parts'),
    (r'effects[\\/]|Effects[\\/]|\.nif\b|particle|explosion', 'Effects'),
    (r'MotherQuest|RQHouse|Quest|quest|InstantDrop|NewInstant|post_\d|Arbeit|arbeit|patrol', 'Missions (quests, delivery, patrol, stations)'),
    (r'UnderCity|UC_|uc_', 'Undercity'),
    (r'Pvp|PVP|pvp|Championship|championship|Replay|Ghost|ladder|Ladder|Gate|Channel', 'PvP races, channels & gates'),
    (r'Parkinglot|parkinglot|Garage|garage|Autoshop|Stage Leave|Stage Enter|Intro_', 'Stages & flow'),
    (r'Gacha|gacha|LOTTO|Lotto|Coupon|coupon|event_|xmas|valen|harvest|anniversary|Hallow', 'Events, gacha, lotto, coupons'),
    (r'Auction|auction', 'Auction'),
    (r'Team|team|TC_', 'Teams & team battles'),
    (r'VSItem|VShop|vshop|CarShop', 'Custom shop (visual items)'),
    (r'Exp\.|Mito|exp_icon|Lv %', 'Progress & rewards'),
]

# corrections from function-by-function study (DriftCity_RE/EVENTS.md, PVP.md, INVENTORY.md): (start, end inclusive, subsystem)
OVERRIDES = [
    (0x5e3b90, 0x5e5300, 'Events, gacha, lotto, coupons'), (0x62ee30, 0x63c900, 'Events, gacha, lotto, coupons'),
    (0x672f00, 0x673940, 'Events, gacha, lotto, coupons'), (0x68f130, 0x694e99, 'Events, gacha, lotto, coupons'),
    (0x6ccc60, 0x6ce4b0, 'Events, gacha, lotto, coupons'), (0x4fb280, 0x4fb280, 'Events, gacha, lotto, coupons'),
    (0x5fbf00, 0x5fbf00, 'Missions (quests, delivery, patrol, stations)'), (0x6a8e30, 0x6a8e30, 'Missions (quests, delivery, patrol, stations)'),
    (0x6fb8d0, 0x6fb8d0, 'Missions (quests, delivery, patrol, stations)'), (0x5f3fe0, 0x5f3fe0, 'Missions (quests, delivery, patrol, stations)'),
    (0x6a51a0, 0x6a51a0, 'Missions (quests, delivery, patrol, stations)'),
    (0x43a900, 0x43a900, 'World streaming'), (0x4bf750, 0x4bf750, 'Stages & flow'),
    (0x4d5e10, 0x4d5e10, 'Platform & system'), (0x7a72f0, 0x7a72f0, 'Platform & system'),   # MD5, not tournaments
]
# the formerly unlabelled code and the Traffic / Data tables mislabels (DriftCity_RE/UNLABELLED.md); appended after the
# list above so earlier entries win where ranges overlap
_M, _P, _T, _L = 'Missions (quests, delivery, patrol, stations)', 'PvP races, channels & gates', 'Traffic & HUV AI', 'LIB C runtime & compiler stubs'
_PS, _EV, _PA = 'Parts shop, manufacturing & licences', 'Events, gacha, lotto, coupons', 'Party & friends'
OVERRIDES += [
    # NPC "ment" markup parser (<pimg> <name> <text> <next> <ending ROO|OMD> ...) behind QUESTDIALOG; 0x70b830 = wide -> multibyte helper
    (0x70b7f0, 0x70b7f0, _M), (0x70b830, 0x70b830, 'Platform & system'), (0x70b890, 0x70db30, _M),
    # the story ending: orbit / watch_end cameras, team ROO/OMD lines 10670-10679, credits.dat roll, driver card 10680-10705 (packet 0x5dd)
    (0x4dd4b0, 0x4e11e0, 'Stages & flow'),
    # remote actor spawn queue (std::deque of create packets, max 0x80) and PlayerEvent stream helpers
    (0x50e410, 0x50e600, 'Networking'), (0x50ed90, 0x510060, 'Networking'), (0x510750, 0x5126e0, 'Networking'), (0x512910, 0x512910, 'Networking'),
    (0x4ce890, 0x4d0460, 'Platform & system'),   # developer profiler overlay (Min/Max/Avg/Rt/Tm)
    (0x6e7cd0, 0x6e9250, 'Inventory & items'),   # NewDockingParts: equip parts, re-apply car stats after server changes
    (0x6e3a50, 0x6e40f0, 'Inventory & items'), (0x6e2e80, 0x6e32c0, 'Inventory & items'), (0x6dd690, 0x6dd8a0, 'Inventory & items'),
    (0x6f47d0, 0x6f4830, 'Inventory & items'),   # 10113-10115 equipped / unequipped / used
    (0x73f8d0, 0x740450, _P), (0x740b00, 0x741250, _P), (0x720e90, 0x721720, _P), (0x71bd40, 0x71c0d0, _P), (0x720000, 0x720320, _P),
    (0x722a00, 0x722a50, _P), (0x722e60, 0x723280, _P), (0x72ba90, 0x72bff0, _P), (0x7294b0, 0x729882, _P), (0x627770, 0x627990, _P),
    (0x586290, 0x586560, _P), (0x588df0, 0x588e10, _P), (0x5823f0, 0x5823f0, _P), (0x7578c0, 0x757900, _P), (0x563550, 0x563550, _P),
    (0x4edf50, 0x4eec90, _L), (0x43d710, 0x43df30, _L),   # std::sort of result rows, std::map instantiation
    (0x607380, 0x607d00, _PA), (0x608f00, 0x609060, _PA), (0x60c360, 0x60c7b0, _PA), (0x60fa30, 0x60fee0, _PA), (0x611350, 0x6113f0, _PA),
    (0x6d89f0, 0x6d9110, _PS), (0x6d5220, 0x6d5550, _PS), (0x6d40d0, 0x6d41f0, _PS), (0x6d36f0, 0x6d3880, _PS), (0x6960e0, 0x6960e0, _PS),
    (0x5458a0, 0x5458a0, _PS),
    (0x6d70a0, 0x6d7138, _M), (0x584540, 0x584760, _M), (0x6d1f80, 0x6d2064, _M), (0x5eb800, 0x5eb800, _M), (0x5ea780, 0x5ea780, _M),
    (0x6e1080, 0x6e11f0, _EV), (0x596a70, 0x596af0, _EV), (0x597890, 0x597a5a, _EV), (0x4ccab0, 0x4ccbe0, _EV), (0x5d51b0, 0x5d51f0, _EV),
    (0x695340, 0x695340, _EV), (0x5e56b0, 0x5e56d0, _EV), (0x68b150, 0x68b150, _EV),
    (0x53a6b0, 0x53a7b0, 'Options & quit'), (0x53b7f0, 0x53be80, 'Options & quit'),
    (0x69e2f0, 0x69e530, 'Effects'), (0x69baa0, 0x69baa0, 'Effects'), (0x4b4080, 0x4b4510, 'Effects'), (0x732730, 0x7329d0, 'Effects'),
    (0x698aa0, 0x698aa0, 'HUD'), (0x5e66f0, 0x5e6950, 'HUD'),   # 3D guide arrow to the target (re-route beyond 120 m)
    (0x49d5b0, 0x49dbc0, 'Data tables'), (0x4bc350, 0x4bc350, 'Data tables'),
    (0x76c560, 0x76cb60, 'Front end (login, server, characters)'), (0x755b30, 0x755be0, 'Front end (login, server, characters)'),
    (0x422d00, 0x423240, 'World streaming'),   # per-frame cull: world, then the actor lists into the visible set
    (0x48e450, 0x48e760, 'World streaming'), (0x48d930, 0x48d9d0, 'World streaming'), (0x403e10, 0x403e80, 'World streaming'),
    (0x402d10, 0x403150, 'Vehicle visuals & parts'), (0x6bd5e0, 0x6bd6a0, 'Vehicle visuals & parts'), (0x425cd0, 0x425cd0, 'Vehicle visuals & parts'),
    (0x46b8f0, 0x46b8f0, 'Vehicle visuals & parts'),
    (0x440850, 0x440bc0, 'Renderer'), (0x40c8a0, 0x40c8f0, 'Renderer'), (0x752670, 0x752720, 'Renderer'),
    (0x448a70, 0x448d30, _T), (0x4b8120, 0x4b8330, _T), (0x4c7030, 0x4c7100, _T), (0x4a9630, 0x4a96a0, _T),
    (0x4ed19e, 0x4ed19e, 'Packet handlers (server replies)'), (0x50242c, 0x50242c, 'Packet handlers (server replies)'),
    (0x4c4870, 0x4c4a40, 'Platform & system'), (0x4765a0, 0x4765e0, 'Platform & system'),
    (0x52bf90, 0x52bf90, 'Audio'), (0x4c7c00, 0x4c7c00, 'Audio'), (0x422100, 0x422120, 'Audio'),
    (0x5ab7e0, 0x5ab890, 'Car dealer'), (0x6ece5d, 0x6ece5d, 'Stages & flow'), (0x51b9a0, 0x51b9a0, 'Stages & flow'),
    (0x4e6c40, 0x4e6c40, 'Teams & team battles'), (0x4a8e00, 0x4a8e10, 'Vehicle physics'),
    # mislabels: game code inside the "ODE" address band. PCS = the passerby system (TRAFFIC.md 10), then OPCODE, then the TCS road /
    # traffic network (.tcs reader 0x803e40, road lookup 0x7fb4d0, TcSStorage; ROADS.md 4); 0x82a308.. = CRT new/delete + import stubs
    (0x7e7cb1, 0x7e7cb1, 'Platform & system'), (0x7e8190, 0x7f3b40, _T), (0x7f3b80, 0x7f6f4f, 'LIB OPCODE collision'),
    (0x7f6f80, 0x80e21f, _T), (0x82a308, 0x82b4cf, _L),
    # mislabels in Traffic & HUV AI: skid marks / tyre smoke / splash / graze (EFFECTS.md), PvP race object, Undercity waves, rewards
    (0x6b5c90, 0x6b6570, 'Effects'), (0x6b7360, 0x6b8d50, 'Effects'), (0x6ba160, 0x6ba810, 'Effects'), (0x69c840, 0x69c900, 'Effects'),
    (0x6a11f0, 0x6a11f0, 'Effects'), (0x490820, 0x490820, 'Effects'), (0x6ae240, 0x6aeab0, 'Effects'),
    (0x4988b0, 0x4988b0, 'Camera'), (0x49c410, 0x49c410, _P), (0x71fc40, 0x71ff60, _P), (0x7238a0, 0x723fa0, _P), (0x729150, 0x729430, _P),
    (0x72d250, 0x72d250, _T), (0x72cd26, 0x72e820, _P), (0x72edf0, 0x72f190, _P), (0x6c0330, 0x6c06e0, _P),   # 0x72d250: PvP track traffic
    (0x717510, 0x717510, 'Undercity'), (0x71a170, 0x71a170, 'Undercity'), (0x71a9b0, 0x71aac0, 'Undercity'),
    (0x6c5e80, 0x6c63d0, _M), (0x560ac0, 0x560ad0, 'Progress & rewards'), (0x5612f0, 0x561350, 'Progress & rewards'),
    (0x4b8b90, 0x4b8b90, _L), (0x4b8d70, 0x4b8d70, _L), (0x4b8f50, 0x4b8f50, _L), (0x4b95c0, 0x4b95c0, _L), (0x4b9920, 0x4b9920, _L),
    # mislabels in Data tables: game init / loading / shutdown
    (0x4d2470, 0x4d2470, 'Stages & flow'), (0x439b40, 0x439b40, 'Stages & flow'), (0x4d4370, 0x4d4370, 'Platform & system'),
    # runs the call-vote / smoothing pass leaves unlabelled once the above are fixed (containers named by their callers)
    (0x5204e0, 0x5219e0, 'Audio'), (0x4356f0, 0x436800, _P), (0x442550, 0x442550, 'Platform & system'), (0x6296e0, 0x6299d0, _EV),
    (0x49bc90, 0x49c020, _T), (0x43c870, 0x43cb70, 'World streaming'), (0x52ef30, 0x52ef90, 'World streaming'),
    (0x4b5f00, 0x4b60a0, 'World streaming'), (0x434a90, 0x434b60, 'World streaming'), (0x4e68b0, 0x4e68e0, 'World streaming'),
    (0x48fba0, 0x48fc70, _T), (0x49e4c0, 0x49e570, _T), (0x4b5b20, 0x4b5b80, 'HUV chases'), (0x4b4f90, 0x4b4f90, 'HUV chases'),
    (0x53d518, 0x53d518, 'Car dealer'), (0x546f70, 0x546f70, _PS), (0x4cd860, 0x4cd860, _M), (0x42c1f0, 0x42c210, 'Undercity'),
]
# packet handlers by feature (DriftCity_RE/HANDLERS.md): the handler entries of the 0x519120 table; acks stay "Packet handlers"
OVERRIDES += [
    # Auction
    (0x4f5740, 0x4f5740, 'Auction'), (0x4f57c0, 0x4f57c0, 'Auction'), (0x4f57f0, 0x4f57f0, 'Auction'), (0x4f5860, 0x4f5860, 'Auction'),
    (0x4f58a0, 0x4f58a0, 'Auction'), (0x4f7730, 0x4f7730, 'Auction'), (0x4f77f0, 0x4f77f0, 'Auction'), (0x4f7820, 0x4f7820, 'Auction'),
    (0x4f78f0, 0x4f78f0, 'Auction'), (0x4f7910, 0x4f7910, 'Auction'), (0x4fa9d0, 0x4fa9d0, 'Auction'), (0x4faa00, 0x4faa00, 'Auction'),
    (0x4fc2b0, 0x4fc2b0, 'Auction'), (0x4fc2e0, 0x4fc2e0, 'Auction'),
    # Car dealer
    (0x4ef370, 0x4ef370, 'Car dealer'), (0x4efef0, 0x4efef0, 'Car dealer'), (0x4fa960, 0x4fa960, 'Car dealer'),
    # Chat & messages
    (0x4ec720, 0x4ec720, 'Chat & messages'), (0x4ef720, 0x4ef720, 'Chat & messages'), (0x4ef7e0, 0x4ef7e0, 'Chat & messages'), (0x4ef8a0, 0x4ef8a0, 'Chat & messages'),
    (0x4ef9d0, 0x4ef9d0, 'Chat & messages'), (0x4f1c30, 0x4f1c30, 'Chat & messages'), (0x4f1ee0, 0x4f1ee0, 'Chat & messages'), (0x4f2000, 0x4f2000, 'Chat & messages'),
    (0x4f2150, 0x4f2150, 'Chat & messages'), (0x4f22a0, 0x4f22a0, 'Chat & messages'), (0x4f2420, 0x4f2420, 'Chat & messages'), (0x502930, 0x502930, 'Chat & messages'),
    (0x50c990, 0x50c990, 'Chat & messages'),
    # Custom shop (visual items)
    (0x4f6840, 0x4f6840, 'Custom shop (visual items)'), (0x4f6ab0, 0x4f6ab0, 'Custom shop (visual items)'), (0x4f6c10, 0x4f6c10, 'Custom shop (visual items)'), (0x4f6d30, 0x4f6d30, 'Custom shop (visual items)'),
    (0x4f6df0, 0x4f6df0, 'Custom shop (visual items)'), (0x4f6e90, 0x4f6e90, 'Custom shop (visual items)'), (0x4f6ec0, 0x4f6ec0, 'Custom shop (visual items)'), (0x4f6fa0, 0x4f6fa0, 'Custom shop (visual items)'),
    (0x4f7030, 0x4f7030, 'Custom shop (visual items)'), (0x4fb230, 0x4fb230, 'Custom shop (visual items)'), (0x4fd350, 0x4fd350, 'Custom shop (visual items)'), (0x4fd530, 0x4fd530, 'Custom shop (visual items)'),
    (0x4fd670, 0x4fd670, 'Custom shop (visual items)'), (0x504330, 0x504330, 'Custom shop (visual items)'), (0x505e20, 0x505e20, 'Custom shop (visual items)'), (0x505e70, 0x505e70, 'Custom shop (visual items)'),
    (0x505ed0, 0x505ed0, 'Custom shop (visual items)'), (0x505f00, 0x505f00, 'Custom shop (visual items)'), (0x506480, 0x506480, 'Custom shop (visual items)'), (0x5069c0, 0x5069c0, 'Custom shop (visual items)'),
    (0x506a30, 0x506a30, 'Custom shop (visual items)'),
    # Events, gacha, lotto, coupons
    (0x4efe70, 0x4efe70, 'Events, gacha, lotto, coupons'), (0x4efe90, 0x4efe90, 'Events, gacha, lotto, coupons'), (0x4f66b0, 0x4f66b0, 'Events, gacha, lotto, coupons'), (0x4f6730, 0x4f6730, 'Events, gacha, lotto, coupons'),
    (0x4f6760, 0x4f6760, 'Events, gacha, lotto, coupons'), (0x4f7090, 0x4f7090, 'Events, gacha, lotto, coupons'), (0x4f75a0, 0x4f75a0, 'Events, gacha, lotto, coupons'), (0x4f7690, 0x4f7690, 'Events, gacha, lotto, coupons'),
    (0x4f8720, 0x4f8720, 'Events, gacha, lotto, coupons'), (0x4f87b0, 0x4f87b0, 'Events, gacha, lotto, coupons'), (0x4f8920, 0x4f8920, 'Events, gacha, lotto, coupons'), (0x4f8cb0, 0x4f8cb0, 'Events, gacha, lotto, coupons'),
    (0x4f8e60, 0x4f8e60, 'Events, gacha, lotto, coupons'), (0x4f8f10, 0x4f8f10, 'Events, gacha, lotto, coupons'), (0x4f8f70, 0x4f8f70, 'Events, gacha, lotto, coupons'), (0x4f8ff0, 0x4f8ff0, 'Events, gacha, lotto, coupons'),
    (0x4f9320, 0x4f9320, 'Events, gacha, lotto, coupons'), (0x4f9370, 0x4f9370, 'Events, gacha, lotto, coupons'), (0x4f9460, 0x4f9460, 'Events, gacha, lotto, coupons'), (0x4f9520, 0x4f9520, 'Events, gacha, lotto, coupons'),
    (0x4f95d0, 0x4f95d0, 'Events, gacha, lotto, coupons'), (0x4fb1b0, 0x4fb1b0, 'Events, gacha, lotto, coupons'), (0x4fb280, 0x4fb280, 'Events, gacha, lotto, coupons'), (0x4fc5c0, 0x4fc5c0, 'Events, gacha, lotto, coupons'),
    (0x4fc5f0, 0x4fc5f0, 'Events, gacha, lotto, coupons'), (0x4fc690, 0x4fc690, 'Events, gacha, lotto, coupons'), (0x4fc760, 0x4fc760, 'Events, gacha, lotto, coupons'), (0x505070, 0x505070, 'Events, gacha, lotto, coupons'),
    (0x509d60, 0x509d60, 'Events, gacha, lotto, coupons'), (0x509d90, 0x509d90, 'Events, gacha, lotto, coupons'),
    # Front end (login, server, characters)
    (0x4ed6c0, 0x4ed6c0, 'Front end (login, server, characters)'), (0x4ed8a0, 0x4ed8a0, 'Front end (login, server, characters)'), (0x4edc40, 0x4edc40, 'Front end (login, server, characters)'), (0x4edd60, 0x4edd60, 'Front end (login, server, characters)'),
    (0x4ede80, 0x4ede80, 'Front end (login, server, characters)'), (0x4eef80, 0x4eef80, 'Front end (login, server, characters)'), (0x4ef1a0, 0x4ef1a0, 'Front end (login, server, characters)'), (0x4ef430, 0x4ef430, 'Front end (login, server, characters)'),
    (0x4efda0, 0x4efda0, 'Front end (login, server, characters)'), (0x4f13d0, 0x4f13d0, 'Front end (login, server, characters)'), (0x4f1420, 0x4f1420, 'Front end (login, server, characters)'), (0x4f69f0, 0x4f69f0, 'Front end (login, server, characters)'),
    (0x4fc430, 0x4fc430, 'Front end (login, server, characters)'), (0x5057a0, 0x5057a0, 'Front end (login, server, characters)'), (0x505d10, 0x505d10, 'Front end (login, server, characters)'), (0x505f50, 0x505f50, 'Front end (login, server, characters)'),
    (0x5063a0, 0x5063a0, 'Front end (login, server, characters)'), (0x506d60, 0x506d60, 'Front end (login, server, characters)'), (0x506e20, 0x506e20, 'Front end (login, server, characters)'),
    # Garage
    (0x4eb080, 0x4eb080, 'Garage'), (0x4f14d0, 0x4f14d0, 'Garage'), (0x4f1550, 0x4f1550, 'Garage'), (0x4f16a0, 0x4f16a0, 'Garage'),
    (0x4f17a0, 0x4f17a0, 'Garage'), (0x4f9a20, 0x4f9a20, 'Garage'), (0x4f9a90, 0x4f9a90, 'Garage'), (0x4fcd60, 0x4fcd60, 'Garage'),
    (0x502850, 0x502850, 'Garage'), (0x503ff0, 0x503ff0, 'Garage'),
    # HUV chases
    (0x4f2500, 0x4f2500, 'HUV chases'), (0x4f27a0, 0x4f27a0, 'HUV chases'), (0x4f2c00, 0x4f2c00, 'HUV chases'), (0x4f2e40, 0x4f2e40, 'HUV chases'),
    (0x4f2ec0, 0x4f2ec0, 'HUV chases'), (0x4f2fc0, 0x4f2fc0, 'HUV chases'), (0x4f30a0, 0x4f30a0, 'HUV chases'), (0x4f3110, 0x4f3110, 'HUV chases'),
    (0x4f3270, 0x4f3270, 'HUV chases'), (0x4f3340, 0x4f3340, 'HUV chases'), (0x4f45b0, 0x4f45b0, 'HUV chases'), (0x4f9b50, 0x4f9b50, 'HUV chases'),
    (0x4f9da0, 0x4f9da0, 'HUV chases'), (0x4fa510, 0x4fa510, 'HUV chases'), (0x4fe260, 0x4fe260, 'HUV chases'), (0x4fe580, 0x4fe580, 'HUV chases'),
    (0x4fe760, 0x4fe760, 'HUV chases'), (0x500d90, 0x500d90, 'HUV chases'), (0x5012a0, 0x5012a0, 'HUV chases'), (0x503380, 0x503380, 'HUV chases'),
    (0x504910, 0x504910, 'HUV chases'), (0x505200, 0x505200, 'HUV chases'), (0x50b8c0, 0x50b8c0, 'HUV chases'), (0x50b990, 0x50b990, 'HUV chases'),
    (0x50bab0, 0x50bab0, 'HUV chases'), (0x50bc40, 0x50bc40, 'HUV chases'), (0x50bc70, 0x50bc70, 'HUV chases'), (0x50bde0, 0x50bde0, 'HUV chases'),
    (0x50be20, 0x50be20, 'HUV chases'), (0x50be40, 0x50be40, 'HUV chases'), (0x50c040, 0x50c040, 'HUV chases'), (0x50c0d0, 0x50c0d0, 'HUV chases'),
    (0x50c110, 0x50c110, 'HUV chases'), (0x50c220, 0x50c220, 'HUV chases'), (0x50c500, 0x50c500, 'HUV chases'),
    # Inventory & items
    (0x4efba0, 0x4efba0, 'Inventory & items'), (0x4efc50, 0x4efc50, 'Inventory & items'), (0x4efd40, 0x4efd40, 'Inventory & items'), (0x4f4ea0, 0x4f4ea0, 'Inventory & items'),
    (0x4f4ee0, 0x4f4ee0, 'Inventory & items'), (0x4f4f60, 0x4f4f60, 'Inventory & items'), (0x4f4f90, 0x4f4f90, 'Inventory & items'), (0x4f50d0, 0x4f50d0, 'Inventory & items'),
    (0x4f5160, 0x4f5160, 'Inventory & items'), (0x4f55d0, 0x4f55d0, 'Inventory & items'), (0x4f5630, 0x4f5630, 'Inventory & items'), (0x4f5680, 0x4f5680, 'Inventory & items'),
    (0x4f56d0, 0x4f56d0, 'Inventory & items'), (0x4f5ca0, 0x4f5ca0, 'Inventory & items'), (0x4f6a50, 0x4f6a50, 'Inventory & items'), (0x4f6f20, 0x4f6f20, 'Inventory & items'),
    (0x4f6f60, 0x4f6f60, 'Inventory & items'), (0x4f7210, 0x4f7210, 'Inventory & items'), (0x4fa720, 0x4fa720, 'Inventory & items'), (0x4fa760, 0x4fa760, 'Inventory & items'),
    (0x4fa8b0, 0x4fa8b0, 'Inventory & items'), (0x4fa8f0, 0x4fa8f0, 'Inventory & items'), (0x4fcf10, 0x4fcf10, 'Inventory & items'), (0x4fcfe0, 0x4fcfe0, 'Inventory & items'),
    (0x4fd160, 0x4fd160, 'Inventory & items'), (0x4fd2c0, 0x4fd2c0, 'Inventory & items'), (0x508290, 0x508290, 'Inventory & items'),
    # Missions (quests, delivery, patrol, stations)
    (0x4efa10, 0x4efa10, 'Missions (quests, delivery, patrol, stations)'), (0x4efa30, 0x4efa30, 'Missions (quests, delivery, patrol, stations)'), (0x4f2fd0, 0x4f2fd0, 'Missions (quests, delivery, patrol, stations)'), (0x4f33b0, 0x4f33b0, 'Missions (quests, delivery, patrol, stations)'),
    (0x4f33d0, 0x4f33d0, 'Missions (quests, delivery, patrol, stations)'), (0x4f3520, 0x4f3520, 'Missions (quests, delivery, patrol, stations)'), (0x4f35c0, 0x4f35c0, 'Missions (quests, delivery, patrol, stations)'), (0x4f3730, 0x4f3730, 'Missions (quests, delivery, patrol, stations)'),
    (0x4f3960, 0x4f3960, 'Missions (quests, delivery, patrol, stations)'), (0x4f3a10, 0x4f3a10, 'Missions (quests, delivery, patrol, stations)'), (0x4f3b20, 0x4f3b20, 'Missions (quests, delivery, patrol, stations)'), (0x4f3da0, 0x4f3da0, 'Missions (quests, delivery, patrol, stations)'),
    (0x4f3ed0, 0x4f3ed0, 'Missions (quests, delivery, patrol, stations)'), (0x4f3ff0, 0x4f3ff0, 'Missions (quests, delivery, patrol, stations)'), (0x4f4140, 0x4f4140, 'Missions (quests, delivery, patrol, stations)'), (0x4f4210, 0x4f4210, 'Missions (quests, delivery, patrol, stations)'),
    (0x4f42e0, 0x4f42e0, 'Missions (quests, delivery, patrol, stations)'), (0x4f4430, 0x4f4430, 'Missions (quests, delivery, patrol, stations)'), (0x4f4800, 0x4f4800, 'Missions (quests, delivery, patrol, stations)'), (0x4f4990, 0x4f4990, 'Missions (quests, delivery, patrol, stations)'),
    (0x4f4b50, 0x4f4b50, 'Missions (quests, delivery, patrol, stations)'), (0x4f4ba0, 0x4f4ba0, 'Missions (quests, delivery, patrol, stations)'), (0x4f4c00, 0x4f4c00, 'Missions (quests, delivery, patrol, stations)'), (0x4f4ce0, 0x4f4ce0, 'Missions (quests, delivery, patrol, stations)'),
    (0x4f4d50, 0x4f4d50, 'Missions (quests, delivery, patrol, stations)'), (0x4f8310, 0x4f8310, 'Missions (quests, delivery, patrol, stations)'), (0x4f83c0, 0x4f83c0, 'Missions (quests, delivery, patrol, stations)'), (0x4f85e0, 0x4f85e0, 'Missions (quests, delivery, patrol, stations)'),
    (0x4f9180, 0x4f9180, 'Missions (quests, delivery, patrol, stations)'), (0x4fa5e0, 0x4fa5e0, 'Missions (quests, delivery, patrol, stations)'), (0x4fa640, 0x4fa640, 'Missions (quests, delivery, patrol, stations)'), (0x4fa690, 0x4fa690, 'Missions (quests, delivery, patrol, stations)'),
    (0x4fa6e0, 0x4fa6e0, 'Missions (quests, delivery, patrol, stations)'), (0x4feae0, 0x4feae0, 'Missions (quests, delivery, patrol, stations)'), (0x4fec70, 0x4fec70, 'Missions (quests, delivery, patrol, stations)'), (0x501d20, 0x501d20, 'Missions (quests, delivery, patrol, stations)'),
    (0x504fe0, 0x504fe0, 'Missions (quests, delivery, patrol, stations)'),
    # Networking
    (0x4eb0f0, 0x4eb0f0, 'Networking'), (0x4eb1a0, 0x4eb1a0, 'Networking'), (0x4eb490, 0x4eb490, 'Networking'), (0x4eb560, 0x4eb560, 'Networking'),
    (0x4eb5e0, 0x4eb5e0, 'Networking'), (0x4ebe50, 0x4ebe50, 'Networking'), (0x4ebf30, 0x4ebf30, 'Networking'), (0x4ec010, 0x4ec010, 'Networking'),
    (0x4ec100, 0x4ec100, 'Networking'), (0x4ec1e0, 0x4ec1e0, 'Networking'), (0x4ec250, 0x4ec250, 'Networking'), (0x4ec2c0, 0x4ec2c0, 'Networking'),
    (0x4ec330, 0x4ec330, 'Networking'), (0x4ec3a0, 0x4ec3a0, 'Networking'), (0x4ec410, 0x4ec410, 'Networking'), (0x4ec480, 0x4ec480, 'Networking'),
    (0x4ec4f0, 0x4ec4f0, 'Networking'), (0x4ec560, 0x4ec560, 'Networking'), (0x4ec8e0, 0x4ec8e0, 'Networking'), (0x4ece70, 0x4ece70, 'Networking'),
    (0x4ecf50, 0x4ecf50, 'Networking'), (0x4ef550, 0x4ef550, 'Networking'), (0x4ef580, 0x4ef580, 'Networking'), (0x4ef680, 0x4ef680, 'Networking'),
    (0x4efd30, 0x4efd30, 'Networking'), (0x4f1c10, 0x4f1c10, 'Networking'), (0x4f1db0, 0x4f1db0, 'Networking'), (0x4f1dd0, 0x4f1dd0, 'Networking'),
    (0x4f1e00, 0x4f1e00, 'Networking'), (0x4faa50, 0x4faa50, 'Networking'), (0x4fc330, 0x4fc330, 'Networking'), (0x503e50, 0x503e50, 'Networking'),
    (0x503ee0, 0x503ee0, 'Networking'), (0x504ef0, 0x504ef0, 'Networking'), (0x504f40, 0x504f40, 'Networking'), (0x505cd0, 0x505cd0, 'Networking'),
    (0x50c920, 0x50c920, 'Networking'), (0x50c960, 0x50c960, 'Networking'),
    # Parts shop, manufacturing & licences
    (0x4ef4a0, 0x4ef4a0, 'Parts shop, manufacturing & licences'), (0x4efc90, 0x4efc90, 'Parts shop, manufacturing & licences'), (0x4efd10, 0x4efd10, 'Parts shop, manufacturing & licences'), (0x4f1880, 0x4f1880, 'Parts shop, manufacturing & licences'),
    (0x4f1b10, 0x4f1b10, 'Parts shop, manufacturing & licences'), (0x4f4ff0, 0x4f4ff0, 'Parts shop, manufacturing & licences'), (0x4f5060, 0x4f5060, 'Parts shop, manufacturing & licences'), (0x4f53a0, 0x4f53a0, 'Parts shop, manufacturing & licences'),
    (0x4f5580, 0x4f5580, 'Parts shop, manufacturing & licences'), (0x4f6fe0, 0x4f6fe0, 'Parts shop, manufacturing & licences'), (0x4f9660, 0x4f9660, 'Parts shop, manufacturing & licences'), (0x4f96c0, 0x4f96c0, 'Parts shop, manufacturing & licences'),
    (0x4f9720, 0x4f9720, 'Parts shop, manufacturing & licences'), (0x4f9780, 0x4f9780, 'Parts shop, manufacturing & licences'), (0x4fc8c0, 0x4fc8c0, 'Parts shop, manufacturing & licences'), (0x4fde60, 0x4fde60, 'Parts shop, manufacturing & licences'),
    (0x5044b0, 0x5044b0, 'Parts shop, manufacturing & licences'), (0x5045a0, 0x5045a0, 'Parts shop, manufacturing & licences'),
    # Party & friends
    (0x4eb880, 0x4eb880, 'Party & friends'), (0x4ed280, 0x4ed280, 'Party & friends'), (0x4efeb0, 0x4efeb0, 'Party & friends'), (0x4f3040, 0x4f3040, 'Party & friends'),
    (0x4f3070, 0x4f3070, 'Party & friends'), (0x4f59a0, 0x4f59a0, 'Party & friends'), (0x4f62b0, 0x4f62b0, 'Party & friends'), (0x4f6330, 0x4f6330, 'Party & friends'),
    (0x4f6400, 0x4f6400, 'Party & friends'), (0x4f65b0, 0x4f65b0, 'Party & friends'), (0x4f6600, 0x4f6600, 'Party & friends'), (0x4f6630, 0x4f6630, 'Party & friends'),
    (0x4f74d0, 0x4f74d0, 'Party & friends'), (0x4f74f0, 0x4f74f0, 'Party & friends'), (0x4f9040, 0x4f9040, 'Party & friends'), (0x4fa280, 0x4fa280, 'Party & friends'),
    (0x4fa3a0, 0x4fa3a0, 'Party & friends'), (0x4fad80, 0x4fad80, 'Party & friends'), (0x4faf20, 0x4faf20, 'Party & friends'), (0x4fafe0, 0x4fafe0, 'Party & friends'),
    (0x4fb0b0, 0x4fb0b0, 'Party & friends'), (0x4fc1d0, 0x4fc1d0, 'Party & friends'), (0x4fc620, 0x4fc620, 'Party & friends'), (0x5008b0, 0x5008b0, 'Party & friends'),
    (0x500c10, 0x500c10, 'Party & friends'), (0x501570, 0x501570, 'Party & friends'), (0x501950, 0x501950, 'Party & friends'), (0x501c90, 0x501c90, 'Party & friends'),
    (0x501df0, 0x501df0, 'Party & friends'), (0x501f10, 0x501f10, 'Party & friends'), (0x502010, 0x502010, 'Party & friends'), (0x502140, 0x502140, 'Party & friends'),
    (0x5046f0, 0x5046f0, 'Party & friends'), (0x5047d0, 0x5047d0, 'Party & friends'),
    # Platform & system
    (0x4f75f0, 0x4f75f0, 'Platform & system'), (0x4f9a00, 0x4f9a00, 'Platform & system'),
    # Progress & rewards
    (0x4f4d80, 0x4f4d80, 'Progress & rewards'), (0x4f4e00, 0x4f4e00, 'Progress & rewards'), (0x4f67d0, 0x4f67d0, 'Progress & rewards'), (0x4f7bb0, 0x4f7bb0, 'Progress & rewards'),
    (0x4f7ce0, 0x4f7ce0, 'Progress & rewards'), (0x4f7e60, 0x4f7e60, 'Progress & rewards'), (0x4f7f80, 0x4f7f80, 'Progress & rewards'),
    # PvP races, channels & gates
    (0x4eb8b0, 0x4eb8b0, 'PvP races, channels & gates'), (0x4f5900, 0x4f5900, 'PvP races, channels & gates'), (0x4f5bc0, 0x4f5bc0, 'PvP races, channels & gates'), (0x4f7630, 0x4f7630, 'PvP races, channels & gates'),
    (0x4f9140, 0x4f9140, 'PvP races, channels & gates'), (0x4f99b0, 0x4f99b0, 'PvP races, channels & gates'), (0x505b80, 0x505b80, 'PvP races, channels & gates'), (0x505bf0, 0x505bf0, 'PvP races, channels & gates'),
    (0x505c10, 0x505c10, 'PvP races, channels & gates'), (0x505c30, 0x505c30, 'PvP races, channels & gates'), (0x505c50, 0x505c50, 'PvP races, channels & gates'), (0x505c80, 0x505c80, 'PvP races, channels & gates'),
    (0x507380, 0x507380, 'PvP races, channels & gates'), (0x5076c0, 0x5076c0, 'PvP races, channels & gates'), (0x507700, 0x507700, 'PvP races, channels & gates'), (0x507870, 0x507870, 'PvP races, channels & gates'),
    (0x5078c0, 0x5078c0, 'PvP races, channels & gates'), (0x5078f0, 0x5078f0, 'PvP races, channels & gates'), (0x507930, 0x507930, 'PvP races, channels & gates'), (0x507950, 0x507950, 'PvP races, channels & gates'),
    (0x507990, 0x507990, 'PvP races, channels & gates'), (0x507a30, 0x507a30, 'PvP races, channels & gates'), (0x507a90, 0x507a90, 'PvP races, channels & gates'), (0x507ae0, 0x507ae0, 'PvP races, channels & gates'),
    (0x507b90, 0x507b90, 'PvP races, channels & gates'), (0x507d70, 0x507d70, 'PvP races, channels & gates'), (0x507db0, 0x507db0, 'PvP races, channels & gates'), (0x507e10, 0x507e10, 'PvP races, channels & gates'),
    (0x507e80, 0x507e80, 'PvP races, channels & gates'), (0x507ed0, 0x507ed0, 'PvP races, channels & gates'), (0x507f00, 0x507f00, 'PvP races, channels & gates'), (0x508050, 0x508050, 'PvP races, channels & gates'),
    (0x508080, 0x508080, 'PvP races, channels & gates'), (0x5080e0, 0x5080e0, 'PvP races, channels & gates'), (0x508130, 0x508130, 'PvP races, channels & gates'), (0x5081f0, 0x5081f0, 'PvP races, channels & gates'),
    (0x5083a0, 0x5083a0, 'PvP races, channels & gates'), (0x5083c0, 0x5083c0, 'PvP races, channels & gates'), (0x5085c0, 0x5085c0, 'PvP races, channels & gates'), (0x5085f0, 0x5085f0, 'PvP races, channels & gates'),
    (0x508b20, 0x508b20, 'PvP races, channels & gates'), (0x508b90, 0x508b90, 'PvP races, channels & gates'), (0x508c00, 0x508c00, 'PvP races, channels & gates'), (0x508f80, 0x508f80, 'PvP races, channels & gates'),
    (0x509260, 0x509260, 'PvP races, channels & gates'), (0x509360, 0x509360, 'PvP races, channels & gates'), (0x509460, 0x509460, 'PvP races, channels & gates'), (0x509550, 0x509550, 'PvP races, channels & gates'),
    (0x509810, 0x509810, 'PvP races, channels & gates'), (0x509a40, 0x509a40, 'PvP races, channels & gates'), (0x509af0, 0x509af0, 'PvP races, channels & gates'), (0x509ba0, 0x509ba0, 'PvP races, channels & gates'),
    (0x509d40, 0x509d40, 'PvP races, channels & gates'), (0x509ec0, 0x509ec0, 'PvP races, channels & gates'), (0x509fe0, 0x509fe0, 'PvP races, channels & gates'), (0x50a040, 0x50a040, 'PvP races, channels & gates'),
    (0x50a170, 0x50a170, 'PvP races, channels & gates'), (0x50a4b0, 0x50a4b0, 'PvP races, channels & gates'), (0x50a590, 0x50a590, 'PvP races, channels & gates'), (0x50ac40, 0x50ac40, 'PvP races, channels & gates'),
    (0x50b140, 0x50b140, 'PvP races, channels & gates'),
    # Stages & flow
    (0x4f1e70, 0x4f1e70, 'Stages & flow'), (0x4f5e80, 0x4f5e80, 'Stages & flow'), (0x4f60f0, 0x4f60f0, 'Stages & flow'), (0x4f61a0, 0x4f61a0, 'Stages & flow'),
    (0x4f93c0, 0x4f93c0, 'Stages & flow'), (0x505d60, 0x505d60, 'Stages & flow'),
    # Teams & team battles
    (0x4ff020, 0x4ff020, 'Teams & team battles'), (0x508f20, 0x508f20, 'Teams & team battles'), (0x5095d0, 0x5095d0, 'Teams & team battles'), (0x50cb20, 0x50cb20, 'Teams & team battles'),
    (0x50ccc0, 0x50ccc0, 'Teams & team battles'), (0x50cd10, 0x50cd10, 'Teams & team battles'), (0x50cd40, 0x50cd40, 'Teams & team battles'), (0x50cd90, 0x50cd90, 'Teams & team battles'),
    (0x50ce40, 0x50ce40, 'Teams & team battles'), (0x50cea0, 0x50cea0, 'Teams & team battles'), (0x50cf00, 0x50cf00, 'Teams & team battles'), (0x50d020, 0x50d020, 'Teams & team battles'),
    (0x50d0f0, 0x50d0f0, 'Teams & team battles'), (0x50d4d0, 0x50d4d0, 'Teams & team battles'), (0x50d5c0, 0x50d5c0, 'Teams & team battles'), (0x50d5d0, 0x50d5d0, 'Teams & team battles'),
    (0x50d610, 0x50d610, 'Teams & team battles'), (0x50d640, 0x50d640, 'Teams & team battles'), (0x50d680, 0x50d680, 'Teams & team battles'), (0x50d6a0, 0x50d6a0, 'Teams & team battles'),
    (0x50d6c0, 0x50d6c0, 'Teams & team battles'), (0x50d7a0, 0x50d7a0, 'Teams & team battles'), (0x50d990, 0x50d990, 'Teams & team battles'), (0x50db10, 0x50db10, 'Teams & team battles'),
    (0x50dc70, 0x50dc70, 'Teams & team battles'), (0x50dd70, 0x50dd70, 'Teams & team battles'), (0x50dde0, 0x50dde0, 'Teams & team battles'), (0x50df20, 0x50df20, 'Teams & team battles'),
    (0x50dfb0, 0x50dfb0, 'Teams & team battles'), (0x50dfe0, 0x50dfe0, 'Teams & team battles'), (0x50e030, 0x50e030, 'Teams & team battles'), (0x50e060, 0x50e060, 'Teams & team battles'),
    # Undercity
    (0x4f2990, 0x4f2990, 'Undercity'), (0x4f2a20, 0x4f2a20, 'Undercity'), (0x4f7a00, 0x4f7a00, 'Undercity'), (0x4f7a20, 0x4f7a20, 'Undercity'),
    (0x4f7a40, 0x4f7a40, 'Undercity'), (0x4f7a60, 0x4f7a60, 'Undercity'), (0x4f7aa0, 0x4f7aa0, 'Undercity'), (0x4f7fd0, 0x4f7fd0, 'Undercity'),
    (0x4f7ff0, 0x4f7ff0, 'Undercity'), (0x4f8030, 0x4f8030, 'Undercity'), (0x4f8050, 0x4f8050, 'Undercity'), (0x4f80d0, 0x4f80d0, 'Undercity'),
    (0x4f81f0, 0x4f81f0, 'Undercity'), (0x50e090, 0x50e090, 'Undercity'), (0x50e0f0, 0x50e0f0, 'Undercity'), (0x50e130, 0x50e130, 'Undercity'),
    (0x50e1a0, 0x50e1a0, 'Undercity'), (0x50e1d0, 0x50e1d0, 'Undercity'), (0x50e320, 0x50e320, 'Undercity'),
]
# table loaders by the subsystem that uses the table (DriftCity_RE/TABLES.md); the generic xlt manager
# (0x7e0980..0x7e1a40), VehicleList and the localize / GlobalText readers stay Data tables
OVERRIDES += [
    (0x4b4710, 0x4b4710, 'Vehicle visuals & parts'), (0x4b48e0, 0x4b48e0, 'Vehicle visuals & parts'),   # AirIntakeList, TireList
    (0x4b4a10, 0x4b4a10, 'Vehicle visuals & parts'), (0x71b6b0, 0x71b6b0, 'Vehicle visuals & parts'),   # SpoilerList, VisualItem
    (0x7e2430, 0x7e2430, 'Vehicle visuals & parts'),
    (0x4bdae0, 0x4bdae0, 'Car dealer'), (0x4bdbc0, 0x4bdbc0, 'Car dealer'), (0x4bdd60, 0x4bdd60, 'Car dealer'),   # VehicleDisplay, VehicleUpgrade, UpgradeCondList
    (0x4be670, 0x4be670, 'Traffic & HUV AI'),   # TrafficFlowControl
    (0x452fa0, 0x452fa0, 'HUV chases'), (0x4b86a0, 0x4b86a0, 'HUV chases'), (0x7d49a0, 0x7d49a0, 'HUV chases'),   # BossHuvInfo, ChaseSpawn, AutoFinder
    (0x4ac5e0, 0x4ac5e0, 'Undercity'),   # UCHuvParam
    (0x6c8a30, 0x6c8a30, _PS), (0x7dddc0, 0x7dddc0, _PS), (0x7de120, 0x7de120, _PS), (0x7de470, 0x7de470, _PS),   # Alchemy_*, License_GC/ME, License
    (0x7d3be0, 0x7d3cf0, _EV),   # TokenBook
    (0x7d4dd0, 0x7d4dd0, _P), (0x7d7990, 0x7d7990, _P),   # Emoticon, PVPTrackInfo
    (0x49dfe0, 0x49e1b0, _P),    # AICarParam: loader, row sink, 0xc4-byte record vector
    (0x70dcf0, 0x70de00, _P),    # effects/realmatch/visual.xlt (Real Match looks)
    (0x7e3b40, 0x7e3b40, _M), (0x7e4770, 0x7e4770, _M),   # MotherQuest, RewardGroup
    (0x7d4360, 0x7d4360, 'Audio'),   # env (positional ambient sounds)
    (0x7e0200, 0x7e0200, 'Chat & messages'), (0x7e0340, 0x7e0340, 'Chat & messages'),   # Slang, OperatorMisrepresent (name / title filter)
    (0x7deea0, 0x7deea0, 'Custom shop (visual items)'), (0x7df090, 0x7df090, 'Custom shop (visual items)'),   # VShopCategory, VShopItem
]
# game.nui ids that are bit-flag constants in code, not dialogs (DIALOG130000 has no controls: 0x20000 flags)
NOT_DIALOGS = {'DIALOG130000'}

def classify(i):
    f, e = F[i], EV[i]
    a = int(f['entry'], 16)
    for lo, hi, sub in OVERRIDES:
        if lo <= a <= hi: return sub, 'override'
    if a >= 0x830000: return 'LIB C runtime & compiler stubs', 'range'
    if e.get('handler') is not None: return 'Packet handlers (server replies)', 'handler'
    ui = {k: v for k, v in e['ui'].items() if not k.startswith('@') and k not in NOT_DIALOGS}
    if ui:
        key = max(ui, key=ui.get)
        for pat, sub in UI:
            if re.match(pat, key): return sub, 'ui:' + key
        return 'UI screens (other)', 'ui:' + key
    for s in f['strings']:
        for pat, sub in RULES:
            if re.search(pat, s): return sub, 'str'
    if e['ui'].get('@servermsg'): return 'Packet handlers (server replies)', 'servermsg'
    for imp in f['icalls']:
        if imp.lower().startswith(('ws2_32', 'wsock32')): return 'Networking', 'imp'
        if imp.lower().startswith(('advapi32', 'user32', 'imm32', 'gdi32', 'shell32', 'ole', 'version', 'wininet', 'winmm', 'dbghelp')): return 'Platform & system', 'imp'
        if imp.lower().startswith('dinput') or 'niinput' in imp.lower(): return 'Input', 'imp'
    if 0x7f0000 <= a < 0x830000: return 'LIB ODE physics', 'range'
    if 0x7b0000 <= a < 0x7d0000: return 'UI framework', 'range'
    if 0x7d0000 <= a < 0x7f0000: return 'Data tables', 'range'
    if 0x780000 <= a < 0x7b0000: return 'Networking', 'range'
    if 0x770000 <= a < 0x780000: return 'Platform & system', 'range'
    return None, None

labels = [classify(i) for i in range(len(F))]
# call-graph votes: an unlabelled function takes the main subsystem among its game-code callers and callees
GENERIC = {'UI framework', 'Data tables', 'Platform & system', 'Networking', 'Renderer', 'Input', 'Audio',
           'LIB C runtime & compiler stubs', 'LIB ODE physics', 'LIB OPCODE collision', 'LIB XML parser', 'Packet handlers (server replies)', 'Progress & rewards'}
idx = {f['entry']: i for i, f in enumerate(F)}
callers = collections.defaultdict(list)
for i, f in enumerate(F):
    for c in f['calls']:
        if c in idx: callers[idx[c]].append(i)
for it in range(4):
    changed = 0
    snap = [l[0] for l in labels]
    for i in range(len(F)):
        if snap[i] is not None: continue
        votes = collections.Counter()
        for c in F[i]['calls']:
            j = idx.get(c)
            if j is not None and snap[j] and snap[j] not in GENERIC: votes[snap[j]] += 1
        for j in callers[i]:
            if snap[j] and snap[j] not in GENERIC: votes[snap[j]] += 2
        if votes:
            top, n = votes.most_common(1)[0]
            if n >= 2 and n >= 0.6 * sum(votes.values()): labels[i] = (top, 'calls'); changed += 1
    if not changed: break
# smoothing: an unlabelled function takes the label shared by its nearest labelled neighbours (both sides), or the
# nearer one when they are within 3 functions
n = len(F)
final = [l[0] for l in labels]
src = [l[1] for l in labels]
for i in range(n):
    if final[i] is not None: continue
    j = i - 1
    while j >= 0 and labels[j][0] is None: j -= 1
    k = i + 1
    while k < n and labels[k][0] is None: k += 1
    L = labels[j][0] if j >= 0 else None
    R = labels[k][0] if k < n else None
    if L and L == R: final[i], src[i] = L, 'between'
    elif L and (i - j) <= 3 and (not R or (i - j) <= (k - i)): final[i], src[i] = L, 'near'
    elif R and (k - i) <= 3: final[i], src[i] = R, 'near'
    else: final[i], src[i] = 'Unlabelled game code', 'none'

out = []
for i in range(n):
    out.append({'entry': F[i]['entry'], 'size': max(0, min(F[i]['size'], 0x859000 - int(F[i]['entry'], 16))), 'sub': final[i], 'how': src[i], 'studied': EV[i].get('studied', [])})
json.dump(out, open(os.path.join(HERE, 'classified.json'), 'w'))
tot = collections.Counter(); cnt = collections.Counter(); stud = collections.Counter()
for o in out:
    tot[o['sub']] += o['size']; cnt[o['sub']] += 1
    if o['studied']: stud[o['sub']] += o['size']
all_bytes = sum(tot.values())
for sub, b in tot.most_common():
    print(f"{sub:48s} {b/1024:8.1f} KB  {cnt[sub]:5d} fns  studied {stud[sub]/max(b,1)*100:5.1f}%")
print('total', all_bytes / 1024, 'KB')
