#!/bin/bash

function find_free_port() {
    # 使用随机起始端口，避免并发测试时的端口冲突
    local start_port=$((19000 + RANDOM % 1000))
    local port=$start_port
    local max_port=65535
    local attempts=0
    local max_attempts=100

    while [ $attempts -lt $max_attempts ] && [ $port -le $max_port ]; do
        # 优先使用 netstat 检测端口占用
        if command -v netstat >/dev/null 2>&1; then
            # 检查端口是否被监听（LISTENING 状态）
            if ! netstat -an | grep -E ":$port\s+.*LISTENING" >/dev/null 2>&1; then
                echo $port
                return 0
            fi
        # 使用 lsof
        elif command -v lsof >/dev/null 2>&1; then
            if ! lsof -i :$port >/dev/null 2>&1; then
                echo $port
                return 0
            fi
        else
            # 最后的备选方案：直接尝试连接测试
            if ! timeout 1 bash -c "</dev/tcp/localhost/$port" 2>/dev/null; then
                echo $port
                return 0
            fi
        fi
        port=$((port + 1))
        attempts=$((attempts + 1))
    done

    echo "No free port found in range $start_port-$max_port after $max_attempts attempts" >&2
    return 1
}

set -xe

export HTTP_PORT
HTTP_PORT=$(find_free_port)
export APPLY_MIGRATIONS
APPLY_MIGRATIONS=false

COMPOSE_ARGS="-f docker-compose.yml -f ci/docker-compose.override.yml -p ${PROJECT_NAME}"

function cleanup() {
    # shellcheck disable=SC2086  # 允许在COMPOSE_ARGS变量不使用引号
    docker compose ${COMPOSE_ARGS} logs || echo "didn't get logs"
    # shellcheck disable=SC2086  # 允许在COMPOSE_ARGS变量不使用引号
    docker compose ${COMPOSE_ARGS} down --volumes --remove-orphans
}

trap cleanup EXIT

# shellcheck disable=SC2086  # 允许在COMPOSE_ARGS变量不使用引号
docker compose ${COMPOSE_ARGS} up -d

echo "Waiting for the container to start..."
sleep 10

curl -f "http://localhost:${HTTP_PORT}/health" || {
    echo "Health check failed"
    exit 1
}
