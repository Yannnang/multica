#!/usr/bin/env bash
# 幂等迁移部署入口：生成幂等脚本并对目标库执行（可重复执行，无副作用）。
# 用法：PLATFORM_DB_CONNECTION='<连接串>' ./deploy/apply-migrations.sh
# 也可先用 `dotnet ef migrations script --idempotent -o deploy/idempotent_migration.sql` 生成脚本交给 DBA。
set -euo pipefail

cd "$(dirname "$0")/.."

: "${PLATFORM_DB_CONNECTION:?PLATFORM_DB_CONNECTION environment variable is required}"

SCRIPT_PATH="deploy/idempotent_migration.sql"

echo "==> Generating idempotent migration script -> ${SCRIPT_PATH}"
dotnet ef migrations script --idempotent \
  --project src/ChatBi.Infrastructure \
  --startup-project src/ChatBi.Api \
  --connection "${PLATFORM_DB_CONNECTION}" \
  -o "${SCRIPT_PATH}"

echo "==> Applying ${SCRIPT_PATH} (idempotent; safe to re-run)"
# -I: QUOTED_IDENTIFIER ON（过滤唯一索引要求）；-b: 任一语句失败即退出非零。
sqlcmd -S "${CHATBI_SQL_SERVER:?CHATBI_SQL_SERVER is required (e.g. localhost,1433)}" \
  -d "${CHATBI_SQL_DATABASE:-ChatBi_Platform}" -I -b -i "${SCRIPT_PATH}"

echo "==> Done."
