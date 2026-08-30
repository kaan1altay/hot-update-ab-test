---
--- abtest bootstrap: the sandbox, the registry, and the only entry points C# calls.
---
--- A patch channel is a remote code execution channel. Whoever can publish a patch can run code on every
--- device that fetches it, so the environment a patch runs in is decided here, deliberately, rather than
--- being whatever Lua happens to provide.
---
--- Two rules shape it:
---
---   1. A behavior is pure. Same context in, same spec out. Anything that could make the same user see a
---      different treatment on a different frame - a random source, a clock - is removed, because an
---      experiment whose treatment varies within a user is measuring noise.
---
---   2. A behavior can only compute. No filesystem, no process control, no network, no C# bridge, no way
---      to reach the telemetry that the analysis rests on.
---
--- Everything C# calls is on the table returned at the bottom. Nothing else is global.
---

local M = {}

-- Behaviors and audience predicates, keyed as the server config names them.
local behaviors = {}
local predicates = {}

-- Staging for the current patch file. Registrations are collected here and committed only if the whole
-- file loads and runs without error, so a file that registers two variants and then throws leaves neither
-- behind. A half-applied patch is the same class of problem as a half-applied config.
local staged_behaviors = nil
local staged_predicates = nil

-- Set by C# to route print() and diagnostics into the framework's log.
local log_sink = nil

local sandbox = nil

--------------------------------------------------------------------------------------------------------
-- The sandbox
--------------------------------------------------------------------------------------------------------

--- Builds the environment every patch chunk runs in.
---
--- Absent on purpose, each one for a reason:
---
---   io, os            filesystem and process control. os also carries time(), clock() and date(), which
---                     would break purity even if the rest were harmless.
---   require, package  a patch may not pull in arbitrary modules; C# decides what source runs.
---   load, loadstring, a patch may not compile more code at runtime, which would route straight around
---   dofile, loadfile  this sandbox.
---   debug             getupvalue/setupvalue and friends reach into other closures, including this file's.
---   coroutine         no use case here, and suspended state across calls would undermine purity.
---   collectgarbage    a patch has no business tuning the VM.
---   CS, xlua          xLua's bridge to the entire C# type system. Left in, a patch could reach the
---                     analytics sink, the filesystem, UnityEngine - everything the rest of this list
---                     is trying to prevent. This is the single most important omission.
---   _G                the real global table. Exposing it would make the whole exercise decorative.
---   math.random,      the same user must resolve to the same presentation every time.
---   math.randomseed
local function build_sandbox()
    local env = {}

    -- Pure language built-ins.
    env.assert = assert
    env.error = error
    env.ipairs = ipairs
    env.next = next
    env.pairs = pairs
    env.pcall = pcall
    env.xpcall = xpcall
    env.select = select
    env.tonumber = tonumber
    env.tostring = tostring
    env.type = type
    env.rawequal = rawequal
    env.rawget = rawget
    env.rawlen = rawlen
    env.rawset = rawset
    env.setmetatable = setmetatable
    env.getmetatable = getmetatable
    env.unpack = table.unpack or unpack

    -- String and table libraries are pure and are what a copy-formatting behavior actually needs.
    env.string = string
    env.table = table

    -- math, minus the two functions that would break determinism. Copied rather than shared so that a
    -- patch assigning math.random back cannot affect anything outside its own environment.
    local safe_math = {}
    for key, value in pairs(math) do
        safe_math[key] = value
    end
    safe_math.random = nil
    safe_math.randomseed = nil
    env.math = safe_math

    -- Diagnostics only. Goes to the framework log, prefixed, never to stdout.
    env.print = function(...)
        if not log_sink then return end

        local parts = {}
        local count = select('#', ...)
        for i = 1, count do
            parts[i] = tostring((select(i, ...)))
        end

        log_sink(table.concat(parts, '\t'))
    end

    -- The two things a patch is actually here to do.
    env.register = function(key, fn)
        if type(key) ~= 'string' or key == '' then
            error('register expects a non-empty string key', 2)
        end
        if type(fn) ~= 'function' then
            error("register expects a function for '" .. tostring(key) .. "'", 2)
        end
        if not staged_behaviors then
            error('register may only be called while a patch file is loading', 2)
        end

        staged_behaviors[key] = fn
    end

    env.register_audience = function(key, fn)
        if type(key) ~= 'string' or key == '' then
            error('register_audience expects a non-empty string key', 2)
        end
        if type(fn) ~= 'function' then
            error("register_audience expects a function for '" .. tostring(key) .. "'", 2)
        end
        if not staged_predicates then
            error('register_audience may only be called while a patch file is loading', 2)
        end

        staged_predicates[key] = fn
    end

    -- A chunk's own _ENV, so `_ENV.foo = 1` stays inside the sandbox rather than escaping.
    env._ENV = env

    return env
end

--------------------------------------------------------------------------------------------------------
-- Loading
--------------------------------------------------------------------------------------------------------

--- Sets the function print() and diagnostics are routed to.
function M.set_log(sink)
    log_sink = sink
end

--- Forgets every registration. C# calls this before reloading, so a reload rebuilds from scratch.
---
--- Rebuilding rather than diffing is what makes reload idempotent and removal reverting, for free: loading
--- the same patch twice cannot double-register anything, and deleting a patch file and reloading returns
--- the affected keys to whatever the baseline defines.
function M.reset()
    behaviors = {}
    predicates = {}
    staged_behaviors = nil
    staged_predicates = nil
end

--- Loads and runs one chunk of Lua source in the sandbox.
---
--- Returns ok, error_message, behavior_count, predicate_count.
---
--- Mode 't' is deliberate: it refuses precompiled bytecode. The Lua bytecode verifier is not hardened, and
--- crafted bytecode can crash or subvert the VM outright, so a channel that accepts source only is a
--- materially smaller attack surface than one that accepts both.
function M.load_chunk(source, chunk_name)
    if type(source) ~= 'string' then
        return false, 'patch source must be a string', 0, 0
    end

    local chunk, compile_error = load(source, '@' .. tostring(chunk_name), 't', sandbox)
    if not chunk then
        return false, tostring(compile_error), 0, 0
    end

    staged_behaviors = {}
    staged_predicates = {}

    local ok, run_error = pcall(chunk)
    if not ok then
        -- Nothing this file staged is committed. Two variants registered before the throw are discarded
        -- along with the failure, rather than half-applying the file.
        staged_behaviors = nil
        staged_predicates = nil
        return false, tostring(run_error), 0, 0
    end

    local behavior_count = 0
    for key, fn in pairs(staged_behaviors) do
        behaviors[key] = fn
        behavior_count = behavior_count + 1
    end

    local predicate_count = 0
    for key, fn in pairs(staged_predicates) do
        predicates[key] = fn
        predicate_count = predicate_count + 1
    end

    staged_behaviors = nil
    staged_predicates = nil

    return true, nil, behavior_count, predicate_count
end

--------------------------------------------------------------------------------------------------------
-- Invocation
--------------------------------------------------------------------------------------------------------

--- True when a behavior is registered under this key.
function M.has_behavior(key)
    return behaviors[key] ~= nil
end

--- True when an audience predicate is registered under this key.
function M.has_predicate(key)
    return predicates[key] ~= nil
end

--- How many behaviors are registered. Diagnostic, for the debug panel.
function M.behavior_count()
    local count = 0
    for _ in pairs(behaviors) do
        count = count + 1
    end
    return count
end

--- Every registered behavior key, newline separated. Diagnostic.
function M.behavior_keys()
    local keys = {}
    for key in pairs(behaviors) do
        keys[#keys + 1] = key
    end
    table.sort(keys)
    return table.concat(keys, '\n')
end

--- Calls a behavior. Returns ok, result_table_or_error_message.
---
--- Every failure mode a patch can produce is turned into an ordinary return value here: an unregistered
--- key, a runtime error, or a return value that is not a table. None of them reach C# as an exception,
--- because the caller's job on any of them is identical - fall back to control and log once.
function M.invoke(key, ctx)
    local fn = behaviors[key]
    if not fn then
        return false, "no behavior is registered for '" .. tostring(key) .. "'"
    end

    local ok, result = pcall(fn, ctx)
    if not ok then
        return false, tostring(result)
    end

    if type(result) ~= 'table' then
        return false, 'the behavior returned ' .. type(result) .. ' rather than a table'
    end

    return true, result
end

--- Evaluates an audience predicate. Returns ok, matched, error_message.
---
--- The caller treats every not-ok as "does not match". Failing closed is the only safe reading: a broken
--- predicate that failed open would sweep users into a treatment nobody validated, on the strength of a
--- bug, and the experiment would then be measuring the bug.
function M.evaluate_audience(key, ctx)
    local fn = predicates[key]
    if not fn then
        return false, false, "no audience predicate is registered for '" .. tostring(key) .. "'"
    end

    local ok, result = pcall(fn, ctx)
    if not ok then
        return false, false, tostring(result)
    end

    if type(result) ~= 'boolean' then
        return false, false, 'the predicate returned ' .. type(result) .. ' rather than a boolean'
    end

    return true, result, nil
end

sandbox = build_sandbox()

return M
