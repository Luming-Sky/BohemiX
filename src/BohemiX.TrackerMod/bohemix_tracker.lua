-- BohemiX Tracker bridge for KCD2 / CryEngine Lua.
-- Compliance boundary: append-only JSONL output, no memory reads, no DLL injection, no save mutation.

BohemiXTracker = BohemiXTracker or {}

local tracker = BohemiXTracker
tracker.session_id = tracker.session_id or tostring(os.time()) .. "-" .. tostring(math.random(100000, 999999))
tracker.position_interval_seconds = 5
tracker.position_distance_meters = 50
tracker.last_position = tracker.last_position or nil
tracker.last_position_time = tracker.last_position_time or 0
tracker.started = tracker.started or false
tracker.polling_started = tracker.polling_started or false
tracker.event_sequence = tracker.event_sequence or 0

local function default_bridge_path()
    local ok, local_app_data = pcall(function()
        return os.getenv("LOCALAPPDATA")
    end)

    if ok and type(local_app_data) == "string" and local_app_data ~= "" then
        return local_app_data .. "\\BohemiX\\tracker\\bridge_events.jsonl"
    end

    return "bridge_events.jsonl"
end

tracker.bridge_path = tracker.bridge_path or default_bridge_path()

local function escape_json(value)
    value = tostring(value or "")
    value = value:gsub("\\", "\\\\")
    value = value:gsub("\"", "\\\"")
    value = value:gsub("\b", "\\b")
    value = value:gsub("\f", "\\f")
    value = value:gsub("\n", "\\n")
    value = value:gsub("\r", "\\r")
    value = value:gsub("\t", "\\t")
    return value
end

local function event_id(event_type, entity_id)
    tracker.event_sequence = tracker.event_sequence + 1
    return tracker.session_id .. ":" .. tostring(event_type) .. ":" .. tostring(entity_id or "none") .. ":" .. tostring(tracker.event_sequence)
end

local function log(message)
    if System and System.LogAlways then
        pcall(System.LogAlways, "[BohemiXTracker] " .. tostring(message))
    end
end

local function log_event(payload)
    if System and System.LogAlways then
        pcall(System.LogAlways, "[BohemiXTrackerEvent] " .. tostring(payload))
    end
end

local function append_jsonl(payload)
    local ok, err = pcall(function()
        local file, open_error = io.open(tracker.bridge_path, "a")
        if file == nil then
            error("unable to open bridge file: " .. tostring(open_error))
        end

        file:write(payload)
        file:write("\n")
        file:close()
    end)

    if not ok then
        log("append failed: " .. tostring(err))
    end
end

local function emit(event_type, entity_id, entity_name, x, y, z, character)
    local timestamp_unix = os.time()
    local payload = "{"
        .. "\"event_id\":\"" .. escape_json(event_id(event_type, entity_id)) .. "\","
        .. "\"session_id\":\"" .. escape_json(tracker.session_id) .. "\","
        .. "\"type\":\"" .. escape_json(event_type) .. "\","
        .. "\"timestamp_unix\":" .. string.format("%.0f", timestamp_unix) .. ","
        .. "\"entity_id\":\"" .. escape_json(entity_id or "") .. "\","
        .. "\"entity_name\":\"" .. escape_json(entity_name or entity_id or "") .. "\""

    if x ~= nil and y ~= nil then
        payload = payload
            .. ",\"x\":" .. tostring(x)
            .. ",\"y\":" .. tostring(y)
            .. ",\"z\":" .. tostring(z or 0)
    end

    if character ~= nil then
        if character.henry_level ~= nil then
            payload = payload .. ",\"henry_level\":" .. tostring(character.henry_level)
        end
        if character.groschen ~= nil then
            payload = payload .. ",\"groschen\":" .. tostring(character.groschen)
        end
    end

    payload = payload .. "}"
    log_event(payload)
    append_jsonl(payload)
end

local function safe_emit(event_type, entity_id, entity_name, x, y, z, character)
    local ok, err = pcall(emit, event_type, entity_id, entity_name, x, y, z, character)
    if not ok then
        log("emit failed for " .. tostring(event_type) .. ": " .. tostring(err))
    end
end

local function distance(a, b)
    local dx = (a.x or 0) - (b.x or 0)
    local dy = (a.y or 0) - (b.y or 0)
    local dz = (a.z or 0) - (b.z or 0)
    return math.sqrt((dx * dx) + (dy * dy) + (dz * dz))
end

local function read_position_from_entity(entity)
    if entity == nil or entity.GetWorldPos == nil then
        return nil
    end

    local ok, pos = pcall(function()
        return entity:GetWorldPos()
    end)

    if ok and type(pos) == "table" and pos.x ~= nil and pos.y ~= nil then
        return pos
    end

    return nil
end

local function read_player_position()
    local candidates = {}

    if _G.g_localActor ~= nil then
        candidates[#candidates + 1] = _G.g_localActor
    end

    if _G.player ~= nil then
        candidates[#candidates + 1] = _G.player
    end

    if System and System.GetEntityByName then
        local names = { "Henry", "Player", "player" }
        for _, name in ipairs(names) do
            local ok, entity = pcall(System.GetEntityByName, name)
            if ok and entity ~= nil then
                candidates[#candidates + 1] = entity
            end
        end
    end

    for _, candidate in ipairs(candidates) do
        local pos = read_position_from_entity(candidate)
        if pos ~= nil then
            return pos
        end
    end

    return nil
end

local function read_character_snapshot()
    local candidates = {}
    if _G.player ~= nil then
        candidates[#candidates + 1] = _G.player
    end
    if _G.g_localActor ~= nil then
        candidates[#candidates + 1] = _G.g_localActor
    end
    for _, candidate in ipairs(candidates) do
        if candidate ~= nil then
            local snapshot = {}
            local level_ok, level = pcall(function()
                return candidate.soul:GetStatLevel("storyProgress")
            end)
            if not level_ok or type(level) ~= "number" or level <= 0 then
                local total = 0
                local count = 0
                for _, stat_name in ipairs({ "strength", "agility", "vitality", "speech" }) do
                    local stat_ok, stat_level = pcall(function()
                        return candidate.soul:GetStatLevel(stat_name)
                    end)
                    if stat_ok and type(stat_level) == "number" and stat_level > 0 then
                        total = total + stat_level
                        count = count + 1
                    end
                end
                if count > 0 then
                    level_ok = true
                    level = total / count
                end
            end
            if level_ok and type(level) == "number" and level > 0 then
                snapshot.henry_level = math.floor(level)
            end

            local money_ok, money = pcall(function()
                return candidate.inventory:GetMoney()
            end)
            if money_ok and type(money) == "number" and money >= 0 then
                snapshot.groschen = math.floor(money)
            end

            if snapshot.henry_level ~= nil or snapshot.groschen ~= nil then
                return snapshot
            end
        end
    end

    return nil
end

local function emit_character_event(event_type, entity_id, entity_name)
    safe_emit(event_type, entity_id, entity_name, nil, nil, nil, read_character_snapshot())
end

function tracker.ConfigureBridgePath(path)
    if type(path) == "string" and path ~= "" then
        tracker.bridge_path = path
    end
end

function tracker.OnQuestStarted(quest_id, quest_name)
    safe_emit("QUEST_START", quest_id, quest_name)
end

function tracker.OnQuestCompleted(quest_id, quest_name)
    safe_emit("QUEST_COMPLETED", quest_id, quest_name)
end

function tracker.OnItemAcquired(item_id, item_name)
    safe_emit("ITEM_ACQUIRED", item_id, item_name)
end

function tracker.OnItemRemoved(item_id, item_name)
    safe_emit("ITEM_REMOVED", item_id, item_name)
end

function tracker.OnGameSaved()
    emit_character_event("GAME_SAVED", "save", "Game saved")
end

function tracker.OnPlayerPosition(x, y, z)
    local now = os.time()
    local current = { x = x, y = y, z = z or 0 }
    local should_emit = tracker.last_position == nil
        or (now - tracker.last_position_time) >= tracker.position_interval_seconds
        or distance(current, tracker.last_position) >= tracker.position_distance_meters

    if should_emit then
        tracker.last_position = current
        tracker.last_position_time = now
        safe_emit("POS_UPDATE", "player", "Player", x, y, z)
    end
end

function tracker.SamplePlayerPosition()
    local pos = read_player_position()
    if pos ~= nil then
        tracker.OnPlayerPosition(pos.x, pos.y, pos.z or 0)
    end
end

function tracker.PollPlayerPosition()
    tracker.SamplePlayerPosition()
    emit_character_event("CHARACTER_SNAPSHOT", "player", "Henry")

    if Script and Script.SetTimer then
        pcall(Script.SetTimer, tracker.position_interval_seconds * 1000, function()
            tracker.PollPlayerPosition()
        end)
    end
end

function tracker.OnGameplayStarted(action_name, event_name, arg_table)
    if tracker.started then
        return
    end

    tracker.started = true
    emit_character_event("SESSION_START", "session", "BohemiX Tracker session")
    tracker.SamplePlayerPosition()
    emit_character_event("CHARACTER_SNAPSHOT", "player", "Henry")

    if Script and Script.SetTimer then
        tracker.polling_started = true
        pcall(Script.SetTimer, tracker.position_interval_seconds * 1000, function()
            tracker.PollPlayerPosition()
        end)
    end

    log("gameplay started; bridge=" .. tostring(tracker.bridge_path))
end

function tracker.OnGameplayEnded(action_name, event_name, arg_table)
    emit_character_event("SESSION_END", "session", "BohemiX Tracker session ended")
end

function tracker.EventSystemListener(action_name, event_name, arg_table)
    if action_name == "System" and event_name == "OnGameplayStarted" then
        tracker.OnGameplayStarted(action_name, event_name, arg_table)
        return
    end

    if action_name == "System" and (event_name == "OnGameplayEnded" or event_name == "OnGameEnded") then
        tracker.OnGameplayEnded(action_name, event_name, arg_table)
        return
    end

    if action_name == "System" and (event_name == "OnGameSaved" or event_name == "OnSaveGameDone") then
        tracker.OnGameSaved()
    end
end

function tracker.Init()
    safe_emit("TRACKER_LOADED", "tracker", "BohemiX Tracker loaded")

    if UIAction and UIAction.RegisterEventSystemListener then
        pcall(UIAction.RegisterEventSystemListener, tracker, "System", "OnGameplayStarted", "OnGameplayStarted")
        pcall(UIAction.RegisterEventSystemListener, tracker, "", "", "EventSystemListener")
        log("registered KCD2 event listeners")
    else
        log("UIAction event system unavailable; waiting for manual callbacks")
    end
end

tracker.Init()
