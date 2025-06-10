#!/usr/bin/env bash
set -euo pipefail

API_URL="http://localhost:8496"

curl --verbose "$API_URL/calculator/stack/size"

curl --verbose -X PUT "$API_URL/calculator/stack/arguments" \
  -H "Content-Type: application/json" \
  -d '{"arguments":[2,3]}'

curl --verbose -X POST "$API_URL/calculator/independent/calculate" \
  -H "Content-Type: application/json" \
  -d '{"arguments":[4,2],"operation":"divide"}'

curl --verbose "$API_URL/calculator/stack/size"

curl --verbose -X PUT "$API_URL/logs/level?logger-name=stack-logger&logger-level=DEBUG"

curl --verbose "$API_URL/calculator/stack/size"

curl --verbose "$API_URL/calculator/stack/operate?operation=fact"

curl --verbose "$API_URL/calculator/stack/operate?operation=minus"

curl --verbose -X PUT "$API_URL/calculator/stack/arguments" \
  -H "Content-Type: application/json" \
  -d '{"arguments":[8,5]}'

curl --verbose "$API_URL/calculator/stack/operate?operation=minus"

curl --verbose -X PUT "$API_URL/logs/level?logger-name=request-logger&logger-level=DEBUG"

curl --verbose -X PUT "$API_URL/calculator/stack/arguments" \
  -H "Content-Type: application/json" \
  -d '{"arguments":[2,3]}'

curl --verbose "$API_URL/calculator/history"

curl --verbose "$API_URL/calculator/stack/operate?operation=abs"

curl --verbose -X DELETE "$API_URL/calculator/stack/arguments?count=1"

curl --verbose "$API_URL/calculator/stack/size"

curl --verbose "$API_URL/calculator/history?flavor=STACK"
