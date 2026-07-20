#!/usr/bin/env bash
# LINE webhook 本機測試腳本(Git Bash 執行:bash test/webhook-test.sh)
# 前提:server 已在跑(dotnet run),且 CHANNEL_SECRET 與 appsettings.json 的 Line:ChannelSecret 一致

set -u

BASE_URL="${BASE_URL:-http://localhost:5000}"
CHANNEL_SECRET="${CHANNEL_SECRET:-REPLACE_WITH_CHANNEL_SECRET}"

PASS=0
FAIL=0

sign() {
  # payload 與簽名必須來自同一份 raw 字串
  printf '%s' "$1" | openssl dgst -sha256 -hmac "$CHANNEL_SECRET" -binary | base64
}

post() {
  local sig="$1" payload="$2"
  # payload 走 stdin,避免 Windows curl 對含中文的參數做 codepage 轉換,改掉 raw bytes
  printf '%s' "$payload" | curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE_URL/webhook" \
    -H 'Content-Type: application/json' \
    -H "x-line-signature: $sig" \
    --data-binary @-
}

check() {
  local name="$1" expected="$2" actual="$3"
  if [ "$actual" = "$expected" ]; then
    echo "PASS: $name (HTTP $actual)"
    PASS=$((PASS + 1))
  else
    echo "FAIL: $name (expected $expected, got $actual)"
    FAIL=$((FAIL + 1))
  fi
}

# case 1: 正確簽名 + group text message → 200
PAYLOAD1='{"destination":"xxxxxxxxxx","events":[{"type":"message","mode":"active","timestamp":1720000000000,"source":{"type":"group","groupId":"Ctestgroup001","userId":"Utestuser001"},"webhookEventId":"01TEST","deliveryContext":{"isRedelivery":false},"message":{"id":"100001","type":"text","text":"測試訊息 hello"}}]}'
check "valid signature + group text message" 200 "$(post "$(sign "$PAYLOAD1")" "$PAYLOAD1")"

# case 2: 錯誤簽名 → 401
check "invalid signature" 401 "$(post "aW52YWxpZHNpZ25hdHVyZQ==" "$PAYLOAD1")"

# case 3: 正確簽名 + 空 events(模擬 LINE Verify)→ 200
PAYLOAD3='{"destination":"xxxxxxxxxx","events":[]}'
check "valid signature + empty events (LINE Verify)" 200 "$(post "$(sign "$PAYLOAD3")" "$PAYLOAD3")"

echo "----------------------------------------"
echo "Result: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
