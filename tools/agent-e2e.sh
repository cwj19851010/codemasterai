#!/usr/bin/env bash
# Automated end-to-end harness for the CodeMaster Agent generation flow.
#
# Drives the whole loop unattended over HTTP, so a failing run can be replayed
# without a human clicking through the UI:
#
#   reset -> new conversation -> requirement turn -> confirmation reply
#         -> switch to Code mode -> generation turn -> drive plan batches
#         -> execute client actions -> report outcome
#
# Usage:
#   bash tools/agent-e2e.sh run        # full loop (default)
#   bash tools/agent-e2e.sh run-fresh  # full loop against a brand-new, empty project
#   bash tools/agent-e2e.sh newproject # create an empty project and print its id
#   bash tools/agent-e2e.sh reset      # wipe agent records + generated metadata
#   bash tools/agent-e2e.sh status     # current plan / metadata snapshot
#
# Prefer run-fresh when you need to know whether the pipeline itself is correct. `reset` only
# soft-deletes, so a plain `run` starts on top of the previous run's leftovers and can fail on
# idempotency guards even when nothing is actually wrong with the flow.
#
# SKIP_RESET=1 keeps the project's existing metadata instead of wiping it. That is the iteration
# case — a user asking for more on a project that already has modules and entities — and the only
# way to check that the "already exists" guards are re-entrant rather than merely absent.
#
# Requires a running WebApi, and a model config that has passed its connection
# test (supportsTools = true). In Server execution mode every "client action"
# is a plain HTTP call, which is what makes this script possible.

set -uo pipefail

BASE="${BASE:-http://localhost:5000}"
PROJECT_ID="${PROJECT_ID:-2110384845296373760}"
PROVIDER_NAME="${PROVIDER_NAME:-qwen3.8max}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REQ_FILE="${REQ_FILE:-$HERE/agent-e2e-requirement.txt}"
MAX_DRIVE_STEPS="${MAX_DRIVE_STEPS:-60}"
DRIVE_TIMEOUT_S="${DRIVE_TIMEOUT_S:-2400}"
# The generation turn makes the model compile a whole business plan and can legitimately run for
# many minutes. A short client timeout aborts the request while the server is still working
# (visible as HTTP 499 in the server log), so keep this generous.
# A Code-mode planning turn can stream for a long time; cutting it off early wastes the whole run
# because the server has usually already built the plan by then.
HTTP_TIMEOUT="${HTTP_TIMEOUT:-1800}"
TOKEN_FILE="${TOKEN_FILE:-${TMPDIR:-/tmp}/cm-e2e-token}"

NODE_BIN="$(command -v node 2>/dev/null || true)"
[ -z "$NODE_BIN" ] && NODE_BIN="C:/Users/caiwenjie/.workbuddy/binaries/node/versions/22.22.2-6/node.exe"

TOKEN=""

# ---------------------------------------------------------------- primitives

say()  { printf '%s\n' "$*"; }
step() { printf '\n=== %s ===\n' "$*"; }

# curl wrapper: always bypass the proxy, or localhost calls get intercepted.
req() {
  local verb="$1" path="$2" body="${3:-}"
  if [ -n "$body" ]; then
    curl -s --noproxy '*' --max-time "${HTTP_TIMEOUT:-300}" -X "$verb" "$BASE$path" \
      -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$body"
  else
    curl -s --noproxy '*' --max-time "${HTTP_TIMEOUT:-300}" -X "$verb" "$BASE$path" \
      -H "Authorization: Bearer $TOKEN"
  fi
}

# jget <dot.path>  — reads JSON on stdin, prints the value (empty when absent).
jget() {
  "$NODE_BIN" -e '
let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{
  let j; try{ j=JSON.parse(d) }catch(e){ process.exit(0) }
  let cur=j;
  for(const k of process.argv[1].split(".")){
    if(cur==null) break;
    cur = (Array.isArray(cur) && /^[0-9]+$/.test(k)) ? cur[+k] : cur[k];
  }
  process.stdout.write(cur==null?"":String(cur));
});' "$1" 2>/dev/null
}

# jlen <dot.path> — reads JSON on stdin, prints the array length ("?" if absent).
jlen() {
  "$NODE_BIN" -e '
let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{
  let j; try{ j=JSON.parse(d) }catch(e){ process.stdout.write("?"); return }
  let cur=j;
  for(const k of process.argv[1].split(".")){
    if(cur==null) break;
    cur = (Array.isArray(cur) && /^[0-9]+$/.test(k)) ? cur[+k] : cur[k];
  }
  process.stdout.write(Array.isArray(cur) ? String(cur.length) : (cur==null ? "?" : "n/a"));
});' "$1" 2>/dev/null
}

# chat_turn <convId> <requestIdBase> <content>
# Sends one chat turn, retrying transient stream/network failures with a fresh
# request id (reusing one would hit the server's idempotency cache).
chat_turn() {
  local convId="$1" requestIdBase="$2" content="$3"
  local attempt=0 max="${CHAT_RETRIES:-2}" reply code msg body
  while :; do
    attempt=$((attempt + 1))
    body=$("$NODE_BIN" -e 'process.stdout.write(JSON.stringify({conversationId:process.argv[1],requestId:process.argv[2],content:process.argv[3]}))' \
      "$convId" "$requestIdBase-$attempt" "$content")
    reply=$(req POST "/api/agent/chat" "$body")
    code=$(printf '%s' "$reply" | jget code)
    if [ "$code" = "200" ]; then
      printf '%s' "$reply"
      return 0
    fi

    msg=$(printf '%s' "$reply" | jget message)
    case "$msg" in
      *"idle timeout"*|*"Timeout"*|*"timed out"*|*"timed-out"*)
        if [ "$attempt" -le "$max" ]; then
          # Log to stderr: stdout carries the JSON reply back to the caller.
          say "  transient failure (attempt $attempt/$max): $(printf '%s' "$msg" | head -c 140)" >&2
          say "  retrying with a fresh request id..." >&2
          sleep 5
          continue
        fi ;;
    esac

    say "  CHAT ERROR: $msg" >&2
    return 1
  done
}

# Prints one line per client action: <action>\t<entityId>\t<projectId>\t<pageId>\t<incremental>
# The execution payload stores the action list as a JSON string inside JSON.
jactions() {
  "$NODE_BIN" -e '
let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{
  let j; try{ j=JSON.parse(d) }catch(e){ return }
  let raw = j && j.data && j.data.execution && j.data.execution.outputJson;
  if(!raw) return;
  let out = raw;
  for(let i=0;i<2 && typeof out==="string";i++){ try{ out=JSON.parse(out) }catch(e){ break } }
  const list = out && out.clientActions;
  if(!Array.isArray(list)) return;
  for(const a of list){
    // Use an explicit null check: `x || ""` turns a legitimate 0 into an empty string, which the
    // server then cannot bind and reports as "Project page 0 was not found" / a null input.
    const s = v => (v === undefined || v === null) ? "" : String(v);
    console.log([s(a.action), s(a.entityId), s(a.projectId), s(a.pageId), a.incremental===false?"false":"true"].join("\t"));
  }
});' 2>/dev/null
}

login() {
  TOKEN=$(curl -s --noproxy '*' --max-time 60 -X POST "$BASE/api/auth/login" \
    -H 'Content-Type: application/json' \
    -d '{"userName":"admin","password":"admin123"}' | jget data.accessToken)
  if [ -z "$TOKEN" ]; then
    say "FATAL: login failed - is the WebApi running on $BASE ?"
    return 1
  fi
  printf '%s' "$TOKEN" > "$TOKEN_FILE"
  return 0
}

# ------------------------------------------------------------------- reporting

report_state() {
  step "state"
  local plan planid pstatus done total batch
  plan=$(req GET "/api/agent/work-plans/active?projectId=$PROJECT_ID")
  planid=$(printf '%s' "$plan" | jget data.id)
  if [ -z "$planid" ]; then
    say "  active plan : none"
  else
    pstatus=$(printf '%s' "$plan" | jget data.status)
    done=$(printf '%s' "$plan" | jget data.completedTasks)
    total=$(printf '%s' "$plan" | jget data.totalTasks)
    batch=$(printf '%s' "$plan" | jget data.activeBatchId)
    say "  active plan : $planid  status=$pstatus  tasks=$done/$total  batch=${batch:-none}"

    # A plan can legitimately finish with tasks the model itself cancelled through CancelTaskKeys —
    # typically the page tasks when the pages already exist. Reporting only completed/total then
    # reads like 36 failures when nothing failed, so break the statuses out.
    local breakdown
    breakdown=$(printf '%s' "$plan" | "$NODE_BIN" -e '
let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{
  let j; try{ j=JSON.parse(d) }catch(e){ return }
  const plan = j.data || j;
  const tasks = (plan.stages || []).flatMap(s => s.tasks || []);
  if (!tasks.length) return;
  const by = {};
  for (const t of tasks) by[t.status] = (by[t.status] || 0) + 1;
  const order = ["Completed","Cancelled","Failed","Blocked","Ready","Pending"];
  const parts = order.filter(k => by[k]).map(k => k + "=" + by[k]);
  for (const k of Object.keys(by)) if (!order.includes(k)) parts.push(k + "=" + by[k]);
  process.stdout.write(parts.join("  "));
});' 2>/dev/null)
    if [ -n "$breakdown" ]; then say "  task status : $breakdown"; fi
  fi

  local bundle
  bundle=$(req GET "/api/codegen/project/$PROJECT_ID/generation-bundle")
  say "  metadata    : modules=$(printf '%s' "$bundle" | jlen data.modules) entities=$(printf '%s' "$bundle" | jlen data.entities) fields=$(printf '%s' "$bundle" | jlen data.fields)"
}

# ----------------------------------------------------------------------- reset

# keep-metadata skips the module/entity sweep so the next run iterates on top of the existing
# model. Plans and conversations are always cleared: the active-plan lookup is project scoped, so
# a leftover Completed plan would be handed straight back to the next run and report success
# before any work happened.
cmd_reset() {
  local keep_metadata="${1:-0}"
  step "reset"

  # Plans first: ArchiveAsync does not cancel a work plan, and the active-plan
  # lookup is project scoped, so a leftover plan would block the next run.
  # Note Failed is NOT terminal here, so it has to be cancelled too.
  local plan planid
  plan=$(req GET "/api/agent/work-plans/active?projectId=$PROJECT_ID")
  planid=$(printf '%s' "$plan" | jget data.id)
  if [ -n "$planid" ]; then
    req POST "/api/agent/work-plans/$planid/cancel?superseded=true" >/dev/null
    say "  cancelled plan $planid"
  else
    say "  no active plan"
  fi

  local convs
  convs=$(req GET "/api/agent/conversations?projectId=$PROJECT_ID")
  local ids
  ids=$("$NODE_BIN" -e '
let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{
  let j; try{ j=JSON.parse(d) }catch(e){ return }
  for(const c of (j.data||[])) console.log(c.id);
});' <<<"$convs" 2>/dev/null)
  if [ -z "$ids" ]; then
    say "  no conversations"
  else
    while read -r id; do
      [ -z "$id" ] && continue
      req DELETE "/api/agent/conversations/$id" >/dev/null
      say "  archived conversation $id"
    done <<<"$ids"
  fi

  if [ "$keep_metadata" = "1" ]; then
    say "  kept existing modules and entities (iteration run)"
    return 0
  fi

  # Generated metadata: deleting the module cascades its entities and fields.
  # This endpoint returns a bare JSON array, unlike most others.
  local mods mids n
  mods=$(req GET "/api/codegen/projectmodule/getbyprojectid/$PROJECT_ID")
  mids=$("$NODE_BIN" -e '
let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{
  let j; try{ j=JSON.parse(d) }catch(e){ return }
  const list = Array.isArray(j) ? j
             : Array.isArray(j.data) ? j.data
             : (j.data && j.data.items) || [];
  for(const m of list) console.log(m.id);
});' <<<"$mods" 2>/dev/null)
  n=0
  if [ -n "$mids" ]; then
    while read -r id; do
      [ -z "$id" ] && continue
      req DELETE "/api/codegen/projectmodule/delete/$id" >/dev/null
      n=$((n+1))
    done <<<"$mids"
  fi

  # Deleting a module does not reliably remove the entities created inside it, so sweep any that
  # are left behind. Without this a "fresh" run starts against stale metadata.
  local bundle eids en
  bundle=$(req GET "/api/codegen/project/$PROJECT_ID/generation-bundle")
  eids=$(printf '%s' "$bundle" | "$NODE_BIN" -e '
let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{
  let j; try{ j=JSON.parse(d) }catch(e){ return }
  for(const e of ((j.data && j.data.entities) || [])){
    const id = e.id || e.entityId || (e.entity && e.entity.id);
    if(id) console.log(id);
  }
});' 2>/dev/null)
  en=0
  if [ -n "$eids" ]; then
    while read -r id; do
      [ -z "$id" ] && continue
      req DELETE "/api/codegen/moduleentity/delete/$id" >/dev/null
      en=$((en+1))
    done <<<"$eids"
  fi
  # Project pages survive the module sweep — they are keyed by PageKey and are not tied to the
  # module row. Left behind, the next run plans pages that already exist and the complex-page
  # planner rejects the whole plan with "页面 PageKey 已存在", which reads as four unrelated failures.
  # This endpoint also returns a bare JSON array.
  local pages pids pn
  pages=$(req GET "/api/codegen/projectpage/getlist?projectId=$PROJECT_ID")
  pids=$("$NODE_BIN" -e '
let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{
  let j; try{ j=JSON.parse(d) }catch(e){ return }
  const list = Array.isArray(j) ? j : (Array.isArray(j.data) ? j.data : []);
  for(const p of list) if(p && p.id) console.log(p.id);
});' <<<"$pages" 2>/dev/null)
  pn=0
  if [ -n "$pids" ]; then
    while read -r id; do
      [ -z "$id" ] && continue
      req DELETE "/api/codegen/projectpage/delete/$id" >/dev/null
      pn=$((pn+1))
    done <<<"$pids"
  fi

  say "  deleted $n module(s), $en leftover entity(ies) and $pn page(s)"
}

# ------------------------------------------------------------- client actions

# Distinguishes "this machine cannot run it" from "the product is broken".
# The sandbox cannot start .NET child processes (hostfxr reports 0x80070005) and cannot load
# some native libraries, so testProject/buildProject fail here for reasons that have nothing to
# do with the change under test. Counting those as FAIL made the whole report untrustworthy:
# real regressions got lost in environment noise.
is_environment_failure() {
  local haystack="$1 $2"
  case "$haystack" in
    *hostfxr*|*0x80070005*|*winmm.dll*|*"Failed to load"*|*"framework was not found"*)
      return 0 ;;
  esac
  return 1
}

run_client_action() {
  local action="$1" entityId="$2" projectId="$3" pageId="$4" incremental="$5"
  # Some client actions omit the project id, send 0, or send a placeholder string. An id the server
  # cannot parse as a number does not merely default to zero: it fails the whole body binding, the
  # action receives a null input, and the caller sees an ArgumentNullException instead of a message.
  # Anything that is not plain digits therefore falls back to the project under test.
  case "$projectId" in ""|0|null|undefined) projectId="$PROJECT_ID" ;; esac
  case "$projectId" in *[!0-9]*) projectId="$PROJECT_ID" ;; esac
  local res payload=""
  case "$action" in
    generateCode)
      res=$(req POST "/api/codegen/moduleentity/generatecode/$entityId") ;;
    generateIncrementalCode)
      res=$(req POST "/api/codegen/moduleentity/generateincrementalcode/$entityId") ;;
    generateSmartCode)
      res=$(req POST "/api/codegen/moduleentity/generatesmartcode/$entityId") ;;
    generateProjectCode)
      payload="{\"projectId\":\"$projectId\",\"entityIds\":[]}"
      res=$(req POST "/api/codegen/moduleentity/generateprojectcode" "$payload") ;;
    generateProjectIncrementalCode)
      payload="{\"projectId\":\"$projectId\",\"entityIds\":[]}"
      res=$(req POST "/api/codegen/moduleentity/generateprojectincrementalcode" "$payload") ;;
    generateProjectSmartCode)
      payload="{\"projectId\":\"$projectId\",\"entityIds\":[]}"
      res=$(req POST "/api/codegen/moduleentity/generateprojectsmartcode" "$payload") ;;
    buildProject)
      payload="{\"projectId\":\"$projectId\"}"
      res=$(req POST "/api/codegen/project/build" "$payload") ;;
    migrateDatabase)
      payload="{\"projectId\":\"$projectId\"}"
      res=$(req POST "/api/codegen/project/migratedatabase" "$payload") ;;
    testProject)
      payload="{\"projectId\":\"$projectId\"}"
      res=$(req POST "/api/codegen/project/test" "$payload") ;;
    generateComplexPage)
      res=$(req POST "/api/codegen/complexpagegeneration/generate/$pageId?incremental=$incremental") ;;
    *)
      say "  UNSUPPORTED client action: $action"
      return 9 ;;
  esac

  local code msg success bare
  code=$(printf '%s' "$res" | jget code)
  msg=$(printf '%s' "$res" | jget message)
  success=$(printf '%s' "$res" | jget success)
  bare=$(printf '%s' "$res" | tr -d '[:space:]')
  # These endpoints answer in three different shapes: an envelope carrying `code`, an object
  # carrying `success`, or a bare boolean. Only the first counts as success in an envelope-only
  # check, which reported working calls as failures and made the whole summary untrustworthy.
  if [ "$code" = "200" ] || [ "$success" = "true" ] || [ "$bare" = "true" ]; then
    say "  action ok   : $action ${entityId:+entity=$entityId}${pageId:+page=$pageId}"
  else
    # 环境不可用不计入失败，但仍要显式报出来，否则"跳过"会变成"看起来全绿"。
    if is_environment_failure "$msg" "$res"; then
      say "  action ENV  : $action -> 环境不可用，不计入失败"
      printf '%s' "$msg" | head -c 200 | sed 's/^/      /'
      printf '\n'
      return 2
    fi
    say "  action FAIL : $action -> code=$code message=$msg"
    # Surface both the request and the response so the failure is diagnosable from the log alone.
    # Without the body it is impossible to tell a rejected payload from a rejected handler.
    if [ -n "$payload" ]; then say "      payload: $payload"; fi
    printf '%s' "$res" | head -c 600 | sed 's/^/      /'
    printf '\n'
    return 1
  fi
  return 0
}

# Executes every action of an execution and reports the result back, mirroring
# what the browser does in executeClientActions().
complete_client_actions() {
  local executionId="$1" response="$2"
  local actions failed=0 env_skipped=0 count=0
  actions=$(printf '%s' "$response" | jactions)
  if [ -z "$actions" ]; then
    say "  WARNING: execution $executionId is AwaitingClientExecution but carries no client actions"
    return 1
  fi
  while IFS=$'\t' read -r a e p pg inc; do
    [ -z "$a" ] && continue
    count=$((count+1))
    # rc=2 means the environment refused to run the action (see is_environment_failure); it is
    # reported but must not be counted as a product failure.
    run_client_action "$a" "$e" "$p" "$pg" "$inc"
    case $? in
      0) ;;
      2) env_skipped=$((env_skipped+1)) ;;
      *) failed=$((failed+1)) ;;
    esac
  done <<<"$actions"

  local done_list
  done_list=$(printf '%s' "$actions" | awk -F'\t' '{print $1}' | paste -sd, -)
  req POST "/api/agent/tools/$executionId/complete-client-actions" \
      "{\"success\":true,\"output\":\"$done_list\"}" >/dev/null
  # The plan is allowed to finish either way, but a failed action must never be reported as a clean
  # run: code generation and the post-build test are exactly the steps this harness exists to check.
  if [ "$failed" -gt 0 ]; then
    local env_note=""
    if [ "$env_skipped" -gt 0 ]; then env_note=", $env_skipped ENV-skipped"; fi
    say "  reported $count client action(s): $failed FAILED$env_note"
  elif [ "$env_skipped" -gt 0 ]; then
    say "  reported $count client action(s): $env_skipped ENV-skipped, 0 product failures"
  else
    say "  reported $count client action(s) complete"
  fi
  return 0
}

# ------------------------------------------------------------------- driving

drive_plan() {
  local convId="$1" deadline=$((SECONDS + DRIVE_TIMEOUT_S)) steps=0
  while [ "$SECONDS" -lt "$deadline" ] && [ "$steps" -lt "$MAX_DRIVE_STEPS" ]; do
    steps=$((steps+1))
    local plan planid pstatus done total
    plan=$(req GET "/api/agent/work-plans/active?projectId=$PROJECT_ID")
    planid=$(printf '%s' "$plan" | jget data.id)
    [ -z "$planid" ] && { say "  no active plan - stopping"; return 2; }
    pstatus=$(printf '%s' "$plan" | jget data.status)
    done=$(printf '%s' "$plan" | jget data.completedTasks)
    total=$(printf '%s' "$plan" | jget data.totalTasks)

    case "$pstatus" in
      Completed) say "  plan Completed ($done/$total)"; return 0 ;;
      Cancelled|Superseded) say "  plan $pstatus - stopping"; return 3 ;;
      AwaitingConfirmation)
        # The plan is drafted but not yet approved. Confirm it the way the UI does, then let the
        # next iteration drive the batches.
        local ver hash cres
        ver=$(printf '%s' "$plan" | jget data.structureVersion)
        hash=$(printf '%s' "$plan" | jget data.contentHash)
        cres=$(req POST "/api/agent/work-plans/$planid/confirm" \
          "{\"expectedStructureVersion\":$ver,\"contentHash\":\"$hash\"}")
        say "  confirmed plan: $(printf '%s' "$cres" | jget code) $(printf '%s' "$cres" | jget message)"
        [ "$(printf '%s' "$cres" | jget code)" = "200" ] || {
          say "  CONFIRM FAILED: $(printf '%s' "$cres" | jget message | head -c 200)"
          return 9
        }
        continue ;;
    esac

    say "  [step $steps] plan=$pstatus tasks=$done/$total"

    local res code
    res=$(req POST "/api/agent/work-plans/$planid/resume?conversationId=$convId")
    code=$(printf '%s' "$res" | jget code)
    if [ "$code" != "200" ]; then
      say "  RESUME ERROR: $(printf '%s' "$res" | jget message)"
      return 4
    fi

    local execId execStatus
    execId=$(printf '%s' "$res" | jget data.execution.id)
    execStatus=$(printf '%s' "$res" | jget data.execution.status)
    if [ -z "$execId" ]; then
      say "  server says: $(printf '%s' "$res" | jget data.message)"
      return 5
    fi

    case "$execStatus" in
      Failed)
        say "  EXECUTION FAILED: $(printf '%s' "$res" | jget data.execution.errorMessage)"
        return 6 ;;
      AwaitingClientExecution)
        complete_client_actions "$execId" "$res" || return 8 ;;
    esac
  done
  say "  drive loop exceeded its budget (steps=$steps)"
  return 7
}

# ------------------------------------------------------------------ full loop

cmd_run() {
  [ -f "$REQ_FILE" ] || { say "FATAL: requirement file not found: $REQ_FILE"; return 1; }
  local requirement
  requirement=$(cat "$REQ_FILE")

  login || return 1

  step "provider"
  local prov provId
  prov=$(req GET "/api/agent/providers")
  provId=$("$NODE_BIN" -e '
let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{
  let j; try{ j=JSON.parse(d) }catch(e){ return }
  const want=process.argv[1];
  const list=(j.data||[]);
  const hit=list.find(p=>p.name===want && p.supportsTools && p.isEnabled)
        || list.find(p=>p.name===want);
  if(!hit){ return }
  if(!hit.supportsTools){
    console.error("  WARNING: "+want+" has supportsTools=false - its connection test must pass first");
  }
  console.log(hit.id);
});' "$PROVIDER_NAME" <<<"$prov" 2>&1)
  if [ -z "$provId" ]; then
    say "FATAL: provider '$PROVIDER_NAME' not found"
    return 1
  fi
  say "  using $PROVIDER_NAME ($provId)"

  # SKIP_RESET=1 keeps whatever the project already holds, which is how a real user iterates: the
  # model is asked for more on top of an existing model. The guards that reset exists to dodge —
  # "already exists", "already registered" — are exactly what that scenario has to survive, so
  # skipping the wipe is the point, not a shortcut.
  if [ "${SKIP_RESET:-0}" = "1" ]; then
    cmd_reset keep-metadata
  else
    cmd_reset
  fi

  step "new conversation"
  local conv convId
  # Ids go over the wire as strings on purpose: a snowflake id is 19 digits and
  # would lose precision if it passed through a JS number.
  conv=$(req POST "/api/agent/conversations" "{\"projectId\":\"$PROJECT_ID\",\"providerId\":\"$provId\"}")
  convId=$(printf '%s' "$conv" | jget data.id)
  [ -z "$convId" ] && { say "FATAL: could not create conversation: $(printf '%s' "$conv" | jget message)"; return 1; }
  say "  conversation $convId  mode=$(printf '%s' "$conv" | jget data.agentMode)"

  step "requirement turn (Requirements mode)"
  local reply
  reply=$(chat_turn "$convId" "e2e-req" "$requirement") || return 1
  say "  assistant: $(printf '%s' "$reply" | jget data.message.content | head -c 400)"
  say "  metadata : $(printf '%s' "$reply" | jget data.message.metadataJson | head -c 220)"

  step "switch to Code mode"
  local modeRes
  modeRes=$(req POST "/api/agent/conversations/$convId/mode" "{\"conversationId\":$convId,\"agentMode\":\"Code\"}")
  say "  mode=$(printf '%s' "$modeRes" | jget data.agentMode)  msg=$(printf '%s' "$modeRes" | jget message)"

  step "generation turn"
  reply=$(chat_turn "$convId" "e2e-gen" "确认，按已讨论的需求开始生成代码") || return 1
  say "  assistant: $(printf '%s' "$reply" | jget data.message.content | head -c 400)"

  step "drive plan"
  drive_plan "$convId"
  local driveRc=$?

  report_state

  step "result"
  if [ "$driveRc" -eq 0 ]; then
    say "  PASS - plan completed"
  else
    say "  FAIL (drive rc=$driveRc) - see the log above for the first error"
  fi
  return "$driveRc"
}

# ------------------------------------------------------------- fresh project

# Creates a project whose codegen metadata is genuinely empty and echoes its id on stdout.
#
# The soft-delete based reset cannot produce a clean slate. It only hides rows, so the next run
# inherits the previous run's modules, entities, relations and view definitions; those leftovers
# then trip the "already exists" / "already registered" guards, and the run fails on idempotency
# instead of exercising the pipeline. Codegen metadata is stored per project id, so a new project
# really is empty — which is the only way to tell "the logic is wrong" from "the debris is in the way".
#
# Diagnostics go to stderr so the caller can capture the id with a plain command substitution.
cmd_newproject() {
  local base dbtype conn stamp name path created newId
  # This endpoint returns the project object directly, without the usual data wrapper.
  base=$(req GET "/api/codegen/project/getbyid/$PROJECT_ID")
  dbtype=$(printf '%s' "$base" | jget databaseType)
  conn=$(printf '%s' "$base" | jget connectionString)
  if [ -z "$dbtype" ]; then
    printf 'FATAL: could not read base project %s\n' "$PROJECT_ID" >&2
    return 1
  fi

  stamp=$(date +%Y%m%d-%H%M%S)
  name="AgentE2E_$stamp"
  # Layout matters. The project database resolves to
  #   {ProjectPath}/{ProjectName}/{ProjectName}.Migrator/{dbFile}
  # and ProjectPath only keeps its own value when it already ends with the project name. Anything
  # else nests one level deeper and SQLite reports the missing folder as the far less helpful
  # "unable to open database file (SQLite Error 14)". Mirror the layout a real project has.
  path="D:/MyHomeWorks/AgentModeE2E-Fresh/$name"
  # Give the copy its own database file. Pointing it at the base project's file would leave the two
  # projects writing the same generated tables.
  conn=$("$NODE_BIN" -e '
let c=process.argv[1]||"", file=process.argv[2]||"";
if(/Data Source\s*=/i.test(c)) process.stdout.write(c.replace(/Data Source\s*=\s*[^;]+/i, "Data Source=" + file + ".db"));
else process.stdout.write(c);
' "$conn" "$name" 2>/dev/null)

  # The project folder has to exist before anything touches the project database: the view stage
  # opens it through SQLite, which reports the missing folder as "unable to open database file"
  # rather than as a path problem. A project created through the UI gets this from initialization.
  mkdir -p "$path/$name.Migrator" 2>/dev/null || printf '  WARN: could not create %s\n' "$path/$name.Migrator" >&2

  created=$(req POST "/api/codegen/project/create" \
    "{\"projectName\":\"$name\",\"displayName\":\"Agent 干净验收 $stamp\",\"databaseType\":$dbtype,\"connectionString\":\"$conn\",\"projectPath\":\"$path\",\"projectType\":0}")
  newId=$("$NODE_BIN" -e '
let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{
  let j; try{ j=JSON.parse(d) }catch(e){ j=null }
  let v=j;
  if(j && typeof j==="object") v = (j.data!==undefined) ? j.data : (j.result!==undefined ? j.result : j.value);
  if(v && typeof v==="object") v = (v.id!==undefined) ? v.id : v.Id;
  process.stdout.write(v==null?"":String(v));
});' <<<"$created" 2>/dev/null)

  if [ -z "$newId" ]; then
    printf 'FATAL: could not create project: %s\n' "$(printf '%s' "$created" | head -c 300)" >&2
    return 1
  fi

  printf '  created project %s  path=%s\n' "$name" "$path" >&2
  printf '%s' "$newId"
}

# Runs the full loop against a brand-new project, so the run starts from an empty metadata slate.
cmd_runfresh() {
  login || return 1
  step "fresh project"
  local newId
  newId=$(cmd_newproject) || return 1
  if [ -z "$newId" ]; then
    say "FATAL: the fresh project id came back empty"
    return 1
  fi
  PROJECT_ID="$newId"
  say "  running against project $PROJECT_ID"
  cmd_run
}

# ---------------------------------------------------------------------- entry

cmd_status() { login || return 1; report_state; }

case "${1:-run}" in
  run)       cmd_run ;;
  run-fresh) cmd_runfresh ;;
  newproject) login || exit 1; cmd_newproject; echo ;;
  reset)     login && cmd_reset && report_state ;;
  status)    cmd_status ;;
  *)         say "usage: $0 {run|run-fresh|newproject|reset|status}"; exit 2 ;;
esac
